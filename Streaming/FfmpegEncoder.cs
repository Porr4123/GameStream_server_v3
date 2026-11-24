using System.ComponentModel;
using System.Diagnostics;
using GameStream.Server.Config;
using GameStream.Server.Diagnostics;

namespace GameStream.Server.Streaming;

/// <summary>
/// Um ffmpeg por sessão, sempre rodando. O servidor entrega quadros BGRA (do FrameComposer) pelo stdin em ritmo
/// constante; o ffmpeg ajusta o tamanho (bicubic + faixas pretas), converte a cor (BT.709) e codifica em H.264 de baixa
/// latência, devolvendo Annex B no stdout. Assim o vídeo nunca para: com ou sem jogo, o cliente sempre recebe quadros.
///
/// O tamanho de ENTRADA pode mudar (tela de espera -> jogo no tamanho nativo): o ffmpeg é reiniciado com o novo tamanho
/// e a SAÍDA continua com o mesmo tamanho, perfil e nível, então o decodificador do celular não precisa ser reconfigurado.
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    /// <summary>Intervalo entre keyframes. Curto de propósito: depois de um descarte por rede lenta, o vídeo volta em até 1 s.</summary>
    public const int KeyframeIntervalSeconds = 1;

    /// <summary>Captura que demora mais que isso gera aviso no log (janela do jogo travada ou muito pesada).</summary>
    private static readonly TimeSpan SlowCapture = TimeSpan.FromMilliseconds(250);

    private readonly string _ffmpegPath;
    private readonly IFrameSource _source;
    private readonly GameProfile _profile;
    private readonly int _fps;
    private readonly ulong _affinityMask;
    private readonly ILogger? _log;
    private readonly VideoProbe? _probe;
    private readonly int _outW, _outH;
    private Process? _process;
    private volatile bool _sizeChanged;

    /// <param name="affinityMask">Núcleos permitidos ao ffmpeg (os que o jogo não usa). 0 = sem restrição.</param>
    /// <param name="outWidth">Tamanho da transmissão (0 = o tamanho que a origem tem no começo).</param>
    public FfmpegEncoder(string ffmpegPath, IFrameSource source, GameProfile profile, int fps, ulong affinityMask,
        ILogger? log = null, VideoProbe? probe = null, int outWidth = 0, int outHeight = 0)
    {
        _probe = probe;
        _ffmpegPath = ffmpegPath;
        _source = source;
        _profile = profile;
        _fps = Math.Max(1, fps);
        _affinityMask = affinityMask;
        _log = log;
        _outW = (outWidth > 0 ? outWidth : source.Width) & ~1;
        _outH = (outHeight > 0 ? outHeight : source.Height) & ~1;
    }

    /// <summary>Parâmetros efetivos (depois de validar o game.json).</summary>
    public EncoderSettings Settings =>
        EncoderSettings.From(_profile, _outW, _outH, _fps,
            _profile.EncoderThreads > 0 ? _profile.EncoderThreads : EncoderSettings.AutoThreads(_affinityMask));

    /// <summary>
    /// Roda até o ffmpeg terminar ou o token ser cancelado. <paramref name="onAccessUnit"/> recebe um quadro por vez
    /// e NÃO pode bloquear: se bloquear, o ffmpeg para de ser lido e o atraso cresce.
    /// </summary>
    public async Task RunAsync(Action<byte[]> onAccessUnit, CancellationToken ct)
    {
        var settings = Settings;
        _log?.LogInformation(
            "Codificação: saída {W}x{H} a {Fps} fps, x264 {Preset}, perfil {Profile}, {Rate}, ajuste de tamanho {Scale}, {Threads} thread(s), nível {Level}",
            settings.OutWidth, settings.OutHeight, settings.Fps, settings.Preset, settings.Profile,
            settings.Crf > 0 ? $"CRF {settings.Crf} com teto de {settings.BitrateKbps} kbps" : $"{settings.BitrateKbps} kbps fixos",
            settings.ScaleFlags, settings.Threads, H264Level.For(settings.OutWidth, settings.OutHeight, settings.Fps));

        while (!ct.IsCancellationRequested)
        {
            // Lê o tamanho de entrada junto com o primeiro quadro, para os dois combinarem.
            var first = _source.NextFrame(out int inW, out int inH);
            _sizeChanged = false;

            bool restart = await RunOnceAsync(first, inW, inH, settings, onAccessUnit, ct);
            if (!restart) break;
            _log?.LogInformation("Tamanho de entrada mudou: reiniciando o ffmpeg (a saída continua {W}x{H}).", settings.OutWidth, settings.OutHeight);
        }
    }

    /// <returns>true se o ffmpeg foi parado só porque o tamanho de entrada mudou (é para subir outro).</returns>
    private async Task<bool> RunOnceAsync(byte[] firstFrame, int inW, int inH, EncoderSettings settings,
        Action<byte[]> onAccessUnit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in BuildArgs(settings, inW, inH))
            psi.ArgumentList.Add(arg);

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

        _log?.LogInformation("ffmpeg iniciado (pid {Pid}, entrada {InW}x{InH}): {Path} {Args}", process.Id, inW, inH, _ffmpegPath,
            string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    if (_log is not null) _log.LogWarning("[ffmpeg] {Line}", line);
                    else Console.WriteLine($"[ffmpeg] {line}");
                }
            }
            catch { /* processo encerrado */ }
        });

        var pump = PumpFramesAsync(process.StandardInput.BaseStream, firstFrame, inW, inH, ct);
        try
        {
            var splitter = new AnnexBSplitter();
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;

            while (!ct.IsCancellationRequested)
            {
                int n = await stdout.ReadAsync(buffer, ct);
                if (n == 0) // ffmpeg terminou
                {
                    if (!ct.IsCancellationRequested && !_sizeChanged)
                    {
                        string code = "?";
                        try { if (process.WaitForExit(2000)) code = process.ExitCode.ToString(); } catch { /* ignora */ }
                        _log?.LogError("O ffmpeg TERMINOU sozinho (código de saída {Code}). Sem ele não há vídeo: a sessão será encerrada. " +
                                       "Veja as linhas [ffmpeg] acima para o motivo.", code);
                    }
                    break;
                }

                foreach (var au in splitter.Push(buffer, n))
                    onAccessUnit(au);
            }
        }
        finally
        {
            Dispose(); // encerra o ffmpeg para o bombeamento de quadros terminar
            try { await pump; } catch { /* já tratado */ }
        }

        return _sizeChanged && !ct.IsCancellationRequested;
    }

    /// <summary>Pede um quadro ao compositor e o envia ao ffmpeg, em ritmo constante. Para sozinho se o tamanho mudar.</summary>
    private async Task PumpFramesAsync(Stream stdin, byte[] firstFrame, int inW, int inH, CancellationToken ct)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(1.0 / _fps);
            var clock = Stopwatch.StartNew();
            long count = 0;
            var lastWarn = TimeSpan.MinValue;
            byte[]? pending = firstFrame;

            while (!ct.IsCancellationRequested)
            {
                var before = clock.Elapsed;
                byte[] frame;
                int w = inW, h = inH;
                if (pending is not null)
                {
                    frame = pending;
                    pending = null;
                }
                else
                {
                    frame = _source.NextFrame(out w, out h);
                }
                var took = clock.Elapsed - before;

                if (w != inW || h != inH)
                {
                    // Fecha a entrada: o ffmpeg termina o que tem, sai, e RunAsync sobe outro com o tamanho novo.
                    _sizeChanged = true;
                    try { stdin.Close(); } catch { /* ignora */ }
                    return;
                }

                _probe?.Record(frame, w, h, took);

                if (took > SlowCapture && (lastWarn == TimeSpan.MinValue || clock.Elapsed - lastWarn > TimeSpan.FromSeconds(5)))
                {
                    lastWarn = clock.Elapsed;
                    _log?.LogWarning(
                        "A captura do quadro levou {Ms} ms. O jogo pode estar travado, carregando ou pesado demais para a captura.",
                        (long)took.TotalMilliseconds);
                }

                await stdin.WriteAsync(frame.AsMemory(0, w * h * 4), ct);
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
                    lastWarn = TimeSpan.MinValue;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // ffmpeg encerrou ou a sessão acabou
        }
    }

    /// <summary>Configuração da primeira versão (baseline, ultrafast, taxa fixa, sem ajuste de tamanho): usada pelos testes antigos.</summary>
    internal static List<string> BuildArgs(int width, int height, int fps, int bitrateKbps) =>
        BuildArgs(EncoderSettings.Legacy(width, height, fps, bitrateKbps), width, height);

    /// <summary>Argumentos do ffmpeg. Separado para o autoteste conferir exatamente o que o servidor usa.</summary>
    /// <param name="inW">Tamanho dos quadros que chegam pelo stdin.</param>
    internal static List<string> BuildArgs(EncoderSettings s, int inW, int inH)
    {
        int outW = s.OutWidth, outH = s.OutHeight;
        var (fitW, fitH) = EncoderSettings.Fit(inW, inH, outW, outH);
        int kbps = s.BitrateKbps;

        // Ajuste de tamanho feito pelo ffmpeg (bicubic etc.) em vez do GDI: sem serrilhado e sem borrar à toa.
        // A conversão para YUV usa a matriz BT.709 e faixa "tv", e a mesma informação vai marcada no fluxo (VUI),
        // então o celular reproduz as cores certas (sem isso, a cor sai levemente deslocada em HD).
        string vf =
            $"scale={fitW}:{fitH}:flags={s.ScaleFlags}+accurate_rnd+full_chroma_int:out_color_matrix=bt709:out_range=tv," +
            $"pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:color=black,format=yuv420p";

        var x264 = "aud=1:repeat-headers=1" + (s.Threads == 1 ? ":slices=1" : "");

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",

            // Entrada: quadros BGRA brutos pelo stdin (o canal alfa é ignorado: bgr0).
            "-f", "rawvideo", "-pixel_format", "bgr0",
            "-video_size", $"{inW}x{inH}",
            "-framerate", s.Fps.ToString(), "-i", "pipe:0",

            "-vf", vf,

            // Saída: H.264 sem B-frames, SPS/PPS em todo keyframe.
            // - nível calculado pela resolução/fps (e não fixo);
            // - AUD em todo quadro só para o servidor separar os quadros (ele é removido antes de ir para o cliente).
            "-c:v", "libx264", "-preset", s.Preset, "-tune", "zerolatency",
            "-profile:v", s.Profile, "-level:v", H264Level.For(outW, outH, s.Fps),
            "-threads", s.Threads.ToString(),
            "-bf", "0", "-g", (s.Fps * KeyframeIntervalSeconds).ToString()
        };

        // Qualidade constante com teto de taxa (CRF + VBV): imagem parada fica nítida gastando pouco, e o movimento
        // pode usar até o teto. Com crf 0 volta à taxa fixa.
        if (s.Crf > 0) args.AddRange(new[] { "-crf", s.Crf.ToString() });
        else args.AddRange(new[] { "-b:v", $"{kbps}k" });
        args.AddRange(new[] { "-maxrate", $"{kbps}k", "-bufsize", $"{Math.Max(100, kbps / 2)}k" });

        args.AddRange(new[]
        {
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-x264-params", x264,

            // Remove as mensagens SEI do x264 (texto de versão): alguns decodificadores de celular implicam com elas.
            "-bsf:v", "filter_units=remove_types=6",

            // Cada pacote sai do pipe na hora, sem esperar o buffer do ffmpeg encher.
            "-flush_packets", "1",

            "-f", "h264", "pipe:1"
        });
        return args;
    }

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* ignora */ }
        _process?.Dispose();
        _process = null;
    }
}
