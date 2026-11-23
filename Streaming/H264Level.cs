namespace GameStream.Server.Streaming;

/// <summary>
/// Escolhe o nível (level) H.264 que realmente comporta a resolução e o fps da transmissão.
/// Fixar "3.1" para qualquer tamanho faz o fluxo declarar um nível menor do que usa; decodificadores
/// de celular que respeitam o nível podem recusar ou travar.
/// Para 640x480 a 30 fps continua sendo 3.1 (o mesmo codec "avc1.42001f" do test.html).
/// </summary>
internal static class H264Level
{
    // Tabela do Anexo A da norma: nível, MaxFS (macroblocos por quadro), MaxMBPS (macroblocos por segundo).
    private static readonly (string Name, long MaxFs, long MaxMbps)[] Levels =
    {
        ("3.1", 3600, 108000),
        ("3.2", 5120, 216000),
        ("4.0", 8192, 245760),
        ("4.2", 8704, 522240),
        ("5.0", 22080, 589824),
        ("5.1", 36864, 983040),
        ("5.2", 36864, 2073600)
    };

    public static string For(int width, int height, int fps)
    {
        long mbs = (long)((width + 15) / 16) * ((height + 15) / 16);
        long rate = mbs * Math.Max(1, fps);

        foreach (var l in Levels)
            if (mbs <= l.MaxFs && rate <= l.MaxMbps) return l.Name;

        return Levels[^1].Name;
    }
}
