namespace GameStream.Server.Audio;

/// <summary>
/// Separa o fluxo ADTS que o ffmpeg escreve em quadros AAC "crus" (sem o cabeçalho de 7/9 bytes).
/// O celular recebe cada quadro cru e usa a configuração fixa AAC-LC 48 kHz estéreo (AudioSpecificConfig 0x11 0x90).
/// </summary>
internal sealed class AdtsParser
{
    private byte[] _buf = new byte[8192];
    private int _len;

    /// <summary>Quantos bytes foram descartados por não terem cabeçalho ADTS válido (deve ficar em 0).</summary>
    public long Resyncs { get; private set; }

    public void Feed(byte[] data, int count, Action<byte[]> onFrame)
    {
        if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
        Buffer.BlockCopy(data, 0, _buf, _len, count);
        _len += count;

        int pos = 0;
        while (true)
        {
            // procura o sync 0xFFF
            while (pos + 1 < _len && !(_buf[pos] == 0xFF && (_buf[pos + 1] & 0xF0) == 0xF0))
            {
                pos++;
                Resyncs++;
            }
            if (pos + 7 > _len) break;

            int headerLen = (_buf[pos + 1] & 1) == 0 ? 9 : 7;            // protection_absent = 0 -> CRC de 2 bytes
            int frameLen = ((_buf[pos + 3] & 3) << 11) | (_buf[pos + 4] << 3) | (_buf[pos + 5] >> 5);
            if (frameLen < headerLen)
            {
                pos++; // cabeçalho falso
                Resyncs++;
                continue;
            }
            if (pos + frameLen > _len) break; // quadro incompleto: espera o resto

            var frame = new byte[frameLen - headerLen];
            Buffer.BlockCopy(_buf, pos + headerLen, frame, 0, frame.Length);
            onFrame(frame);
            pos += frameLen;
        }

        if (pos > 0)
        {
            _len -= pos;
            if (_len > 0) Buffer.BlockCopy(_buf, pos, _buf, 0, _len);
        }
    }
}
