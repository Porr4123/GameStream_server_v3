using System.ComponentModel;
using System.Diagnostics;
using GameStream.Server.Config;

namespace GameStream.Server.Streaming;

/// <summary>
/// Um ffmpeg por sessão, sempre rodando. O servidor entrega quadros BGRA de tamanho fixo (do FrameComposer)
/// pelo stdin em ritmo constante; o ffmpeg codifica em H.264 de baixa latência e devolve Annex B no stdout.
/// Assim o vídeo nunca para: com ou sem jogo, o cliente sempre recebe quadros.
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly FrameComposer _composer;
    private readonly GameProfile _profile;
    private readonly int _fps;
    private readonly ulong _affinityMask;
    private Process? _process;

    /// <param name="affinityMask">Núcleos permitidos ao ffmpeg (os que o jogo não usa). 0 = sem restrição.</param>
    public FfmpegEncoder(string ffmpegPath, FrameComposer composer, GameProfile profile, int fps, ulong affinityMask)
    {
        _ffmpegPath = ffmpegPath;
        _composer = composer;
        _profile = profile;
        _fps = Math.Max(1, fps);
        _affinityMask = affinityMask;
    }

    public async Task RunAsync(Func<byte[], Task> onAccessUnit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in BuildArgs()) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("ffmpeg não encontrado. Instale-o ou ajuste FfmpegPath no appsettings.json.");
        }
        _process = process;

        if (_affinityMask != 0)
        {
            try { process.ProcessorAffinity = (IntPtr)(long)_affinityMask; }
            catch { /* sem permissão: segue sem restrição */ }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                    Console.WriteLine($"[ffmpeg] {line}");
            }
            catch { /* processo encerrado */ }
        });

        var pump = PumpFramesAsync(process.StandardInput.BaseStream, ct);
        try
        {
            var splitter = new AnnexBSplitter();
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;

            while (!ct.IsCancellationRequested)
            {
                int n = await stdout.ReadAsync(buffer, ct);
                if (n == 0) break; // ffmpeg terminou

                foreach (var au in splitter.Push(buffer, n))
                    await onAccessUnit(au);
            }
        }
        finally
        {
            Dispose(); // encerra o ffmpeg para o bombeamento de quadros terminar
            try { await pump; } catch { /* já tratado */ }
        }
    }

    /// <summary>Pede um quadro ao compositor e o envia ao ffmpeg, em ritmo constante.</summary>
    private async Task PumpFramesAsync(Stream stdin, CancellationToken ct)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(1.0 / _fps);
            var clock = Stopwatch.StartNew();
            long count = 0;

            while (!ct.IsCancellationRequested)
            {
                var frame = _composer.NextFrame();
                await stdin.WriteAsync(frame, ct);
                await stdin.FlushAsync(ct);
                count++;

                var wait = TimeSpan.FromTicks(interval.Ticks * count) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }
                else if (wait < -interval * 3)
                {
                    clock.Restart(); // ficou muito atrasado: recomeça o ritmo em vez de mandar rajada
                    count = 0;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // ffmpeg encerrou ou a sessão acabou
        }
    }

    private List<string> BuildArgs()
    {
        int kbps = Math.Max(200, _profile.BitrateKbps);

        return new List<string>
        {
            "-hide_banner", "-loglevel", "warning",

            // Entrada: quadros BGRA brutos pelo stdin (o canal alfa é ignorado: bgr0).
            "-f", "rawvideo", "-pixel_format", "bgr0",
            "-video_size", $"{_composer.Width}x{_composer.Height}",
            "-framerate", _fps.ToString(), "-i", "pipe:0",

            "-vf", "format=yuv420p",

            // Saída: H.264 baseline, sem B-frames, AUD em todo quadro, SPS/PPS em todo keyframe.
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-level:v", "3.1",
            "-bf", "0", "-g", (_fps * 2).ToString(),
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps / 2}k",
            "-x264-params", "aud=1:repeat-headers=1",

            // Remove as mensagens SEI do x264 (texto de versão): alguns decodificadores de celular implicam com elas.
            "-bsf:v", "filter_units=remove_types=6",

            "-f", "h264", "pipe:1"
        };
    }

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* ignora */ }
        _process?.Dispose();
        _process = null;
    }
}
