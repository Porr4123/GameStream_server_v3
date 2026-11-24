using GameStream.Server.Config;

namespace GameStream.Server.Streaming;

/// <summary>
/// Parâmetros do x264 vindos do game.json, já validados. Textos que entram na linha do ffmpeg (preset, perfil, filtro)
/// só podem ser valores das listas abaixo: um game.json com texto estranho cai no padrão em vez de virar argumento.
/// </summary>
internal sealed record EncoderSettings(
    int OutWidth, int OutHeight, int Fps, int BitrateKbps, int Crf, string Preset, string Profile, string ScaleFlags, int Threads)
{
    /// <summary>Como o jogo ocupa o quadro: inteiro com faixas (Fit), cortando as bordas (Fill) ou esticado (Stretch).</summary>
    public FitMode Mode { get; init; } = FitMode.Fit;

    public static readonly string[] Presets = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium" };
    public static readonly string[] Profiles = { "baseline", "main", "high" };
    public static readonly string[] ScaleModes = { "bicubic", "lanczos", "bilinear", "neighbor" };

    /// <param name="threads">Já resolvido (&gt;= 1).</param>
    public static EncoderSettings From(GameProfile p, int outWidth, int outHeight, int fps, int threads) => new(
        outWidth & ~1, outHeight & ~1, Math.Clamp(fps, 1, 120),
        Math.Max(200, p.BitrateKbps),
        p.Crf <= 0 ? 0 : Math.Clamp(p.Crf, 10, 40),
        Pick(p.Preset, Presets, "veryfast"),
        Pick(p.H264Profile, Profiles, "main"),
        Pick(p.ScaleMode, ScaleModes, "bicubic"),
        Math.Clamp(threads, 1, 8));

    /// <summary>Configuração da primeira versão (baseline, ultrafast, 1 thread, taxa fixa): usada pelos testes antigos.</summary>
    public static EncoderSettings Legacy(int width, int height, int fps, int bitrateKbps) =>
        new(width & ~1, height & ~1, Math.Max(1, fps), Math.Max(200, bitrateKbps), 0, "ultrafast", "baseline", "bicubic", 1);

    /// <summary>Núcleos que o ffmpeg pode usar (máscara de afinidade); 0 = todos.</summary>
    public static int AutoThreads(ulong affinityMask)
    {
        int cores = affinityMask == 0
            ? Environment.ProcessorCount
            : System.Numerics.BitOperations.PopCount(affinityMask);
        return Math.Clamp(cores, 1, 4);
    }

    private static string Pick(string? value, string[] allowed, string fallback)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return allowed.Contains(v) ? v : fallback;
    }

    /// <summary>Menor tamanho (par) que COBRE outW x outH mantendo a proporção de inW x inH (o excesso é cortado).</summary>
    public static (int W, int H) Cover(int inW, int inH, int outW, int outH)
    {
        double scale = Math.Max((double)outW / inW, (double)outH / inH);
        int w = (int)Math.Ceiling(inW * scale), h = (int)Math.Ceiling(inH * scale);
        w = Math.Max(outW, (w + 1) & ~1);
        h = Math.Max(outH, (h + 1) & ~1);
        return (w, h);
    }

    /// <summary>Maior tamanho (par) que cabe em outW x outH mantendo a proporção de inW x inH. Nunca aumenta além do necessário para caber.</summary>
    public static (int W, int H) Fit(int inW, int inH, int outW, int outH)
    {
        double scale = Math.Min((double)outW / inW, (double)outH / inH);
        int w = Math.Min(outW, Math.Max(2, (int)Math.Round(inW * scale))) & ~1;
        int h = Math.Min(outH, Math.Max(2, (int)Math.Round(inH * scale))) & ~1;
        return (Math.Max(2, w), Math.Max(2, h));
    }
}
