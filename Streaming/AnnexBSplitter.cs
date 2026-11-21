namespace GameStream.Server.Streaming;

/// <summary>
/// Recebe o fluxo H.264 (Annex B) do ffmpeg e devolve um "access unit" (um quadro completo) por vez.
/// Depende do ffmpeg emitir um AUD (NAL tipo 9) antes de cada quadro (x264-params aud=1).
/// Obs.: o último quadro só sai quando o AUD do próximo chega (+1 quadro de latência). Dá para melhorar depois.
/// </summary>
public sealed class AnnexBSplitter
{
    private const int MaxBuffer = 4 * 1024 * 1024;
    private byte[] _buf = new byte[256 * 1024];
    private int _len;

    public List<byte[]> Push(byte[] data, int count)
    {
        if (_buf.Length < _len + count)
            Array.Resize(ref _buf, Math.Max(_len + count, _buf.Length * 2));
        Buffer.BlockCopy(data, 0, _buf, _len, count);
        _len += count;

        var result = new List<byte[]>();
        int start = -1;
        int searchFrom = 0;

        while (true)
        {
            int aud = FindAud(searchFrom);
            if (aud < 0) break;

            if (start >= 0)
            {
                var au = new byte[aud - start];
                Buffer.BlockCopy(_buf, start, au, 0, au.Length);
                result.Add(au);
            }

            start = aud;
            searchFrom = aud + 4;
        }

        if (start < 0)
        {
            if (_len > MaxBuffer) _len = 0; // lixo sem AUD: descarta
        }
        else
        {
            // Mantém só o quadro ainda incompleto.
            Buffer.BlockCopy(_buf, start, _buf, 0, _len - start);
            _len -= start;
        }

        return result;
    }

    /// <summary>Posição de início do próximo AUD (incluindo o zero extra do start code de 4 bytes), ou -1.</summary>
    private int FindAud(int from)
    {
        for (int i = Math.Max(from, 0); i + 3 < _len; i++)
        {
            if (_buf[i] == 0 && _buf[i + 1] == 0 && _buf[i + 2] == 1 && (_buf[i + 3] & 0x1F) == 9)
                return (i > 0 && _buf[i - 1] == 0) ? i - 1 : i;
        }
        return -1;
    }

    /// <summary>true se o quadro contém um slice IDR (NAL tipo 5).</summary>
    public static bool IsKeyFrame(byte[] au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
            {
                int type = au[i + 3] & 0x1F;
                if (type == 5) return true;
                if (type == 1) return false;
            }
        }
        return false;
    }
}
