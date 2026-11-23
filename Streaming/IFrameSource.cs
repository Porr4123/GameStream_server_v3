namespace GameStream.Server.Streaming;

/// <summary>
/// Origem dos quadros BGRA de tamanho fixo que vão para o codificador.
/// Em produção é o <see cref="FrameComposer"/>; o autoteste usa uma origem sintética (sem Windows/GDI).
/// </summary>
internal interface IFrameSource
{
    int Width { get; }
    int Height { get; }

    /// <summary>Quadro atual (Width*Height*4 bytes, BGRA). O buffer pode ser reaproveitado entre chamadas.</summary>
    byte[] NextFrame();
}
