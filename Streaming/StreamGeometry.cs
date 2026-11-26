namespace GameStream.Server.Streaming;

/// <summary>Como o jogo ocupa o quadro transmitido.</summary>
public enum FitMode
{
    /// <summary>Mostra o jogo inteiro, mantendo a proporção (pode sobrar faixa preta).</summary>
    Fit,
    /// <summary>Ocupa o quadro todo mantendo a proporção; corta o que passar das bordas.</summary>
    Fill,
    /// <summary>Ocupa o quadro todo deformando a imagem.</summary>
    Stretch
}

/// <summary>Tela do cliente, informada na conexão (?sw=&amp;sh=&amp;fit=). Largura/altura 0 = não informada.</summary>
public readonly record struct ClientDisplay(int ScreenWidth, int ScreenHeight, FitMode Fit);

/// <summary>Contas de tamanho do quadro (sem dependência do Windows, para poder ser testado).</summary>
public static class StreamGeometry
{
    private const double MinAspect = 1.0;
    private const double MaxAspect = 2.5;

    public static FitMode ParseFit(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "fill" => FitMode.Fill,
        "stretch" => FitMode.Stretch,
        _ => FitMode.Fit
    };

    /// <summary>
    /// Tamanho do quadro da transmissão para uma tela de proporção screenW:screenH. Parte da ALTURA do perfil e ajusta a
    /// largura; se passar da quantidade de pixels do perfil (em macroblocos de 16x16), reduz mantendo a proporção. Assim
    /// uma tela comprida (20:9) não custa mais CPU e rede que o tamanho que o dono do servidor escolheu.
    /// Sem tela informada, devolve o tamanho do perfil. Sempre par (exigência do yuv420p).
    /// </summary>
    public static (int Width, int Height) CanvasSize(int profileWidth, int profileHeight, int screenWidth, int screenHeight)
    {
        int ph = Math.Max(2, profileHeight) & ~1;
        int pw = Math.Max(2, profileWidth) & ~1;
        if (screenWidth <= 0 || screenHeight <= 0) return (pw, ph);

        int budget = Macroblocks(pw, ph);
        double aspect = Math.Clamp((double)screenWidth / screenHeight, MinAspect, MaxAspect);
        int h = ph;
        int w = Even(h * aspect);

        while (Macroblocks(w, h) > budget && w > 16 && h > 16)
        {
            w = Even(w * 0.97);
            h = Even(h * 0.97);
        }
        return (Math.Max(2, w), Math.Max(2, h));
    }

    public static int Macroblocks(int width, int height) => ((width + 15) / 16) * ((height + 15) / 16);

    /// <summary>Teto de taxa proporcional à quantidade de pixels do quadro novo (entre 0,5x e 2x a do perfil).</summary>
    public static int ScaleBitrate(int profileKbps, int profilePixels, int width, int height)
    {
        if (profilePixels <= 0) return profileKbps;
        double ratio = Math.Clamp((double)width * height / profilePixels, 0.5, 2.0);
        return (int)(profileKbps * ratio);
    }

    /// <summary>
    /// Fator máximo de redução que o Windows precisa fazer antes de mandar o quadro ao ffmpeg (nunca aumenta).
    /// Fit/Stretch: o jogo inteiro tem que caber; Fill: tem que cobrir o quadro.
    /// </summary>
    public static double PreScale(int canvasWidth, int canvasHeight, int srcWidth, int srcHeight, FitMode mode)
    {
        double sx = (double)canvasWidth / srcWidth;
        double sy = (double)canvasHeight / srcHeight;
        double s = mode == FitMode.Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        return Math.Min(1.0, s);
    }

    /// <summary>
    /// Converte um ponto do quadro transmitido (0..1 em cada eixo, como o celular vê) para um ponto do jogo (0..1 da área do jogo).
    /// null = o ponto caiu na faixa preta do modo Fit (fora do jogo).
    /// </summary>
    public static (double X, double Y)? MapToGame(int canvasW, int canvasH, int gameW, int gameH, FitMode mode, double nx, double ny)
    {
        if (canvasW <= 0 || canvasH <= 0 || gameW <= 0 || gameH <= 0) return null;
        if (!(nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1)) return null; // inclui NaN
        if (mode == FitMode.Stretch) return (nx, ny);

        double sx = (double)canvasW / gameW, sy = (double)canvasH / gameH;
        double s = mode == FitMode.Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        double w = gameW * s, h = gameH * s;
        double gx = (nx * canvasW - (canvasW - w) / 2) / w;
        double gy = (ny * canvasH - (canvasH - h) / 2) / h;
        if (gx < 0 || gx > 1 || gy < 0 || gy > 1) return null;
        return (gx, gy);
    }

    private static int Even(double value) => ((int)value) & ~1;
}
