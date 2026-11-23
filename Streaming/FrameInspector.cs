using System.Runtime.InteropServices;

namespace GameStream.Server.Streaming;

/// <summary>Resumo barato de uma imagem BGRA: quanto é preto, brilho médio e uma "impressão digital" para detectar mudança.</summary>
internal readonly record struct FrameStats(double BlackRatio, double MeanLuma, ulong Hash, int Samples);

/// <summary>
/// Ferramentas de diagnóstico para responder "o que o servidor está realmente capturando?":
/// analisa a imagem (preta? parada?) e a salva como .bmp para você abrir e olhar.
/// </summary>
internal static class FrameInspector
{
    /// <summary>
    /// Black/luma olham 1 de cada <paramref name="step"/> x <paramref name="step"/> pixels (padrão 2: ~1 ms).
    /// O Hash cobre a imagem INTEIRA (ignorando o canal alfa): qualquer pixel que mude muda o hash, então
    /// "mesmo Hash" = mesma imagem. Importante para não declarar "parada" uma imagem que só mexe num cantinho.
    /// </summary>
    public static FrameStats Analyze(byte[] bgra, int width, int height, int step = 2)
    {
        step = Math.Max(1, step);
        int length = Math.Min(bgra.Length, width * height * 4);
        long black = 0, samples = 0, lumaSum = 0;

        for (int y = 0; y < height; y += step)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x += step)
            {
                int o = row + x * 4;
                if (o + 2 >= length) break;

                byte b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
                samples++;
                if (r < 16 && g < 16 && b < 16) black++;
                lumaSum += (r * 3 + g * 6 + b) / 10;
            }
        }

        ulong hash = FullHash(bgra, length);
        if (samples == 0) return new FrameStats(1, 0, hash, 0);
        return new FrameStats(black / (double)samples, lumaSum / (double)samples, hash, (int)samples);
    }

    /// <summary>FNV-1a sobre palavras de 64 bits (2 pixels por vez), sem o byte de alfa de cada pixel.</summary>
    public static ulong FullHash(byte[] bgra, int length)
    {
        const ulong NoAlpha = 0x00FFFFFF_00FFFFFFUL;
        ulong hash = 14695981039346656037UL;

        var words = MemoryMarshal.Cast<byte, ulong>(bgra.AsSpan(0, length & ~7));
        foreach (var w in words)
            hash = (hash ^ (w & NoAlpha)) * 1099511628211UL;

        // Sobra (largura*altura ímpar): mistura os últimos bytes soltos.
        for (int i = length & ~7; i < length; i++)
            if ((i & 3) != 3) hash = (hash ^ bgra[i]) * 1099511628211UL;

        return hash;
    }

    /// <summary>BMP de 24 bits (abre em qualquer visualizador e no navegador). O canal alfa é descartado.</summary>
    public static byte[] ToBmp24(byte[] bgra, int width, int height)
    {
        int rowSize = (width * 3 + 3) & ~3;
        int dataSize = rowSize * height;
        var bmp = new byte[54 + dataSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt(bmp, 2, 54 + dataSize);
        WriteInt(bmp, 10, 54);
        WriteInt(bmp, 14, 40);       // tamanho do BITMAPINFOHEADER
        WriteInt(bmp, 18, width);
        WriteInt(bmp, 22, height);   // positivo = linhas de baixo para cima (o mais compatível)
        bmp[26] = 1;                 // planos
        bmp[28] = 24;                // bits por pixel
        WriteInt(bmp, 34, dataSize);

        for (int y = 0; y < height; y++)
        {
            int src = y * width * 4;
            int dst = 54 + (height - 1 - y) * rowSize;
            for (int x = 0; x < width; x++)
            {
                if (src + 2 >= bgra.Length) break;
                bmp[dst++] = bgra[src];
                bmp[dst++] = bgra[src + 1];
                bmp[dst++] = bgra[src + 2];
                src += 4;
            }
        }
        return bmp;
    }

    private static void WriteInt(byte[] b, int offset, int value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
        b[offset + 3] = (byte)(value >> 24);
    }
}
