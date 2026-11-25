using System.Diagnostics;
using GameStream.Server.Net;

namespace GameStream.Server.Audio;

/// <summary>
/// Som do jogo para o celular: captura SÓ o áudio do processo do jogo (WASAPI) -> ffmpeg (AAC-LC 48 kHz estéreo, ADTS)
/// -> quadros AAC crus -> <see cref="WsSender.EnqueueAudio"/>. Nunca derruba a sessão: se algo falhar, o jogo segue sem som.
/// </summary>
internal sealed class AudioStreamer : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly int _bitrateKbps;
    private readonly long _affinityMask;
    private readonly WsSender _sender;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts;

    private Process? _ffmpeg;
    private Thread? _captureThread;
    private Task? _readTask;
    private long _pcmBytes, _silenceBytes, _frames;
    private IAudioCapture? _cap;
    private readonly string _captureDevice;
    private readonly bool _beep;

    public AudioStreamer(string ffmpegPath, int bitrateKbps, long affinityMask, WsSender sender, ILogger log, CancellationToken ct, bool beep = true, string captureDevice = "")
    {
        _captureDevice = captureDevice ?? "";
        _beep = beep;
        _ffmpegPath = ffmpegPath;
        _bitrateKbps = Math.Clamp(bitrateKbps, 32, 320);
        _affinityMask = affinityMask;
        _sender = sender;
        _log = log;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public long Frames => Interlocked.Read(ref _frames);
    public long LoudPackets => _cap?.LoudPackets ?? _loudFinal;
    private long _loudFinal;
    public long PcmBytes => Interlocked.Read(ref _pcmBytes);
    public long SilenceBytes => Interlocked.Read(ref _silenceBytes);

    /// <summary>Liga o som do processo <paramref name="processId"/>. Devolve false (e registra o motivo) se não deu.</summary>
    public bool TryStart(int processId)
    {
        IAudioCapture capture = string.IsNullOrWhiteSpace(_captureDevice) ? new ProcessLoopbackCapture() : new DeviceLoopbackCapture(_captureDevice);
        try
        {
            capture.Open(processId);
            _cap = capture;

            var psi = new ProcessStartInfo(_ffmpegPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "warning", "-fflags", "nobuffer", "-flags", "low_delay",
                "-probesize", "32", "-analyzeduration", "0",
                "-f", capture.Format.FfmpegName, "-ar", capture.Format.Rate.ToString(), "-ac", capture.Format.Channels.ToString(), "-i", "pipe:0",
                "-c:a", "aac", "-profile:a", "aac_low", "-b:a", _bitrateKbps + "k", "-ar", "48000", "-ac", "2",
                "-flush_packets", "1", "-f", "adts", "pipe:1"
            }) psi.ArgumentList.Add(a);

            _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("não consegui iniciar o ffmpeg do áudio");
            if (_affinityMask != 0)
            {
                try { _ffmpeg.ProcessorAffinity = (IntPtr)_affinityMask; } catch { /* ignora */ }
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await _ffmpeg.StandardError.ReadLineAsync()) != null)
                        _log.LogWarning("ffmpeg (áudio): {Line}", line);
                }
                catch { /* ffmpeg encerrou */ }
            });

            var ffmpeg = _ffmpeg;
            _readTask = Task.Run(() => ReadAdts(ffmpeg));
            _captureThread = new Thread(() => CaptureLoop(capture, ffmpeg)) { IsBackground = true, Name = "GameStream audio" };
            _captureThread.Start();

            _log.LogInformation("Som do jogo ligado: captura só do processo {Pid} (WASAPI, modo \"{Mode}\") -> AAC {Kbps} kbps, 48 kHz estéreo.",
                processId, capture.Mode, _bitrateKbps);
            return true;
        }
        catch (Exception ex)
        {
            capture.Dispose();
            _log.LogWarning("Som DESLIGADO nesta sessão (o jogo segue sem áudio): {Type}: {Msg}", ex.GetType().Name, ex.Message);
            Dispose();
            return false;
        }
    }

    private void CaptureLoop(IAudioCapture capture, Process ffmpeg)
    {
        var fmt = capture.Format;
        try
        {
            var input = ffmpeg.StandardInput.BaseStream;
            var clock = Stopwatch.StartNew();
            long framesWritten = 0;

            // Bipe de teste (0,4 s, 440 Hz) logo ao ligar: se o celular apitar, o caminho servidor -> celular funciona e
            // qualquer mudez vem da captura do jogo. Se não apitar, o problema está no envio ou no app.
            if (_beep)
            {
                int beepFrames = fmt.Rate * 4 / 10;
                var beep = new byte[beepFrames * fmt.BytesPerFrame];
                for (int i = 0; i < beepFrames; i++)
                {
                    double fade = Math.Min(1.0, Math.Min(i, beepFrames - i) / 2400.0);
                    double v = Math.Sin(2 * Math.PI * 440 * i / fmt.Rate) * 0.275 * fade;
                    for (int c = 0; c < fmt.Channels; c++)
                        fmt.WriteSample(beep, i * fmt.BytesPerFrame + c * fmt.BytesPerSample, v);
                }
                input.Write(beep, 0, beep.Length);
                framesWritten += beepFrames;
                input.Flush();
                _log.LogInformation("Bipe de teste de som enviado ao celular (0,4 s, 440 Hz).");
            }

            var statWatch = Stopwatch.StartNew();
            int statLines = 0;

            while (!_cts.IsCancellationRequested)
            {
                int bytes = capture.Read(input, 10);
                framesWritten += bytes / fmt.BytesPerFrame;
                Interlocked.Add(ref _pcmBytes, bytes);

                // Sem nada tocando o WASAPI não entrega pacotes. O ffmpeg e o celular esperam um fluxo contínuo,
                // então completa com silêncio para manter o relógio do som igual ao do relógio real.
                long expected = (long)(clock.Elapsed.TotalSeconds * fmt.Rate);
                long missing = expected - framesWritten;
                if (missing > fmt.Rate / 50) // mais de 20 ms atrasado
                {
                    missing = Math.Min(missing, fmt.Rate / 5);
                    var zeros = new byte[missing * fmt.BytesPerFrame];
                    input.Write(zeros, 0, zeros.Length);
                    framesWritten += missing;
                    Interlocked.Add(ref _silenceBytes, zeros.Length);
                }
                else if (missing < -fmt.Rate / 2)
                {
                    // O dispositivo entregou rajadas à frente do relógio: reajusta para o atraso não crescer.
                    clock.Restart();
                    framesWritten = 0;
                }
                input.Flush();

                if (statLines < 12 && statWatch.Elapsed.TotalSeconds >= 5)
                {
                    statWatch.Restart();
                    statLines++;
                    var (sentFrames, _) = _sender.AudioTotals;
                    int peak = capture.TakePeak();
                    _log.LogInformation("Som (5 s): {Frames} quadros AAC gerados, {Sent} enviados, {Loud} pacotes não-zero, pico do som do jogo {Peak}/32768{Hint}, {Silence} KB de silêncio completado.",
                        Frames, sentFrames, capture.LoudPackets, peak, peak < 20 ? " (SILÊNCIO: o jogo não tocou nada ou a captura veio muda)" : "", SilenceBytes / 1024);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                _log.LogWarning("A captura de som parou: {Type}: {Msg}. O jogo segue sem áudio.", ex.GetType().Name, ex.Message);
        }
        finally
        {
            _loudFinal = capture.LoudPackets;
            capture.Dispose();
            try { ffmpeg.StandardInput.Close(); } catch { /* ignora */ }
        }
    }

    private void ReadAdts(Process ffmpeg)
    {
        try
        {
            var parser = new AdtsParser();
            var buf = new byte[4096];
            var stdout = ffmpeg.StandardOutput.BaseStream;
            int n;
            while ((n = stdout.Read(buf, 0, buf.Length)) > 0)
            {
                parser.Feed(buf, n, frame =>
                {
                    Interlocked.Increment(ref _frames);
                    _sender.EnqueueAudio(frame);
                });
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            // encerrando
        }
        catch (Exception ex)
        {
            _log.LogWarning("Leitura do áudio codificado parou: {Msg}", ex.Message);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignora */ }
        try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(); } catch { /* ignora */ }
        try { _captureThread?.Join(1000); } catch { /* ignora */ }
        try { _ffmpeg?.Dispose(); } catch { /* ignora */ }
        _cts.Dispose();
    }
}
