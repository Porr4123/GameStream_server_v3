namespace GameStream.Server.Streaming;

/// <summary>
/// Origem dos quadros BGRA que vão para o codificador. O tamanho pode MUDAR durante a sessão (tela de espera no
/// tamanho da transmissão; jogo no tamanho nativo dele): o codificador reinicia o ffmpeg quando isso acontece, e o
/// fluxo de saída continua com o mesmo tamanho. Em produção é o <see cref="FrameComposer"/>; o autoteste usa
/// origens sintéticas (sem Windows/GDI).
/// </summary>
internal interface IFrameSource
{
    int Width { get; }
    int Height { get; }

    /// <summary>Quadro atual (Width*Height*4 bytes, BGRA). O buffer pode ser reaproveitado entre chamadas.</summary>
    byte[] NextFrame();

    /// <summary>
    /// Quadro atual COM o tamanho dele, lidos juntos (se o tamanho mudar entre ler Width e chamar NextFrame, os bytes
    /// não bateriam com o tamanho). Origens de tamanho fixo não precisam implementar.
    /// </summary>
    byte[] NextFrame(out int width, out int height)
    {
        var frame = NextFrame();
        width = Width;
        height = Height;
        return frame;
    }
}
