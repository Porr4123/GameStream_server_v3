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
    private ProcessLoopbackCapture? _cap;

    public AudioStreamer(string ffmpegPath, int bitrateKbps, long affinityMask, WsSender sender, ILogger log, CancellationToken ct)
    {
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
        var capture = new ProcessLoopbackCapture();
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
                "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", "pipe:0",
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

            _log.LogInformation("Som do jogo ligado: captura só do processo {Pid} (WASAPI) -> AAC {Kbps} kbps, 48 kHz estéreo.",
                processId, _bitrateKbps);
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

    private void CaptureLoop(ProcessLoopbackCapture capture, Process ffmpeg)
    {
        try
        {
            var input = ffmpeg.StandardInput.BaseStream;
            var clock = Stopwatch.StartNew();
            long framesWritten = 0;

            while (!_cts.IsCancellationRequested)
            {
                int bytes = capture.Read(input, 10);
                framesWritten += bytes / ProcessLoopbackCapture.BytesPerFrame;
                Interlocked.Add(ref _pcmBytes, bytes);

                // Sem nada tocando o WASAPI não entrega pacotes. O ffmpeg e o celular esperam um fluxo contínuo,
                // então completa com silêncio para manter o relógio do som igual ao do relógio real.
                long expected = (long)(clock.Elapsed.TotalSeconds * ProcessLoopbackCapture.SampleRate);
                long missing = expected - framesWritten;
                if (missing > ProcessLoopbackCapture.SampleRate / 50) // mais de 20 ms atrasado
                {
                    missing = Math.Min(missing, ProcessLoopbackCapture.SampleRate / 5);
                    var zeros = new byte[missing * ProcessLoopbackCapture.BytesPerFrame];
                    input.Write(zeros, 0, zeros.Length);
                    framesWritten += missing;
                    Interlocked.Add(ref _silenceBytes, zeros.Length);
                }
                else if (missing < -ProcessLoopbackCapture.SampleRate / 2)
                {
                    // O dispositivo entregou rajadas à frente do relógio: reajusta para o atraso não crescer.
                    clock.Restart();
                    framesWritten = 0;
                }
                input.Flush();
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
