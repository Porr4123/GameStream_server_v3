namespace GameStream.Server.Diagnostics;

internal readonly record struct SpsInfo(
    int ProfileIdc, int ConstraintFlags, int LevelIdc, int Width, int Height, int MaxRefFrames)
{
    public string ProfileName => ProfileIdc switch
    {
        66 => (ConstraintFlags & 0x40) != 0 ? "Constrained Baseline" : "Baseline",
        77 => "Main",
        88 => "Extended",
        100 => "High",
        110 => "High 10",
        122 => "High 4:2:2",
        244 => "High 4:4:4",
        _ => $"perfil {ProfileIdc}"
    };

    public string LevelName => $"{LevelIdc / 10}.{LevelIdc % 10}";

    /// <summary>Texto para o log. O codec do Android seria "avc1.PPCCLL" (hex) com estes mesmos bytes.</summary>
    public override string ToString() =>
        $"{ProfileName} nível {LevelName}, {Width}x{Height}, {MaxRefFrames} quadro(s) de referência " +
        $"(avc1.{ProfileIdc:X2}{ConstraintFlags:X2}{LevelIdc:X2})";
}

/// <summary>Lê o SPS (NAL tipo 7) de um quadro H.264 Annex B: confere o que o servidor realmente declara ao celular.</summary>
internal static class H264Info
{
    public static SpsInfo? ParseSps(byte[] annexB)
    {
        for (int i = 0; i + 4 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0) continue;

            int nal;
            if (annexB[i + 2] == 1) nal = i + 3;
            else if (annexB[i + 2] == 0 && annexB[i + 3] == 1) nal = i + 4;
            else continue;

            if (nal >= annexB.Length || (annexB[nal] & 0x1F) != 7) { i = nal - 1; continue; }

            int end = annexB.Length;
            for (int j = nal + 1; j + 2 < annexB.Length; j++)
                if (annexB[j] == 0 && annexB[j + 1] == 0 && (annexB[j + 2] == 1 || annexB[j + 2] == 0)) { end = j; break; }

            return Parse(Unescape(annexB, nal + 1, end));
        }
        return null;
    }

    /// <summary>Tira os bytes de "emulation prevention" (00 00 03 -> 00 00).</summary>
    private static byte[] Unescape(byte[] data, int start, int end)
    {
        var rbsp = new List<byte>(end - start);
        int zeros = 0;
        for (int i = start; i < end; i++)
        {
            byte b = data[i];
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            rbsp.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return rbsp.ToArray();
    }

    private static SpsInfo? Parse(byte[] d)
    {
        try
        {
            var r = new BitReader(d);
            int profile = (int)r.Bits(8);
            int constraints = (int)r.Bits(8);
            int level = (int)r.Bits(8);
            r.Ue(); // seq_parameter_set_id

            int chroma = 1;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chroma = (int)r.Ue();
                if (chroma == 3) r.Bits(1);
                r.Ue(); r.Ue(); r.Bits(1);
                if (r.Bits(1) == 1)
                {
                    int lists = chroma != 3 ? 8 : 12;
                    for (int k = 0; k < lists; k++)
                    {
                        if (r.Bits(1) == 0) continue;
                        int size = k < 6 ? 16 : 64, last = 8, next = 8;
                        for (int j = 0; j < size; j++)
                        {
                            if (next != 0)
                            {
                                next = (last + r.Se() + 256) % 256;
                            }
                            last = next == 0 ? last : next;
                        }
                    }
                }
            }

            r.Ue();                                  // log2_max_frame_num_minus4
            uint pocType = r.Ue();
            if (pocType == 0)
            {
                r.Ue();
            }
            else if (pocType == 1)
            {
                r.Bits(1); r.Se(); r.Se();
                uint cycle = r.Ue();
                for (uint k = 0; k < cycle; k++) r.Se();
            }

            int maxRef = (int)r.Ue();
            r.Bits(1);                               // gaps_in_frame_num_value_allowed_flag
            int mbsW = (int)r.Ue() + 1;
            int mapUnitsH = (int)r.Ue() + 1;
            int frameMbsOnly = (int)r.Bits(1);
            if (frameMbsOnly == 0) r.Bits(1);
            r.Bits(1);                               // direct_8x8_inference_flag

            int width = mbsW * 16;
            int height = mapUnitsH * 16 * (2 - frameMbsOnly);

            if (r.Bits(1) == 1)                      // frame_cropping_flag
            {
                int left = (int)r.Ue(), right = (int)r.Ue(), top = (int)r.Ue(), bottom = (int)r.Ue();
                int cropX = chroma is 1 or 2 ? 2 : 1;
                int cropY = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
                width -= cropX * (left + right);
                height -= cropY * (top + bottom);
            }

            return new SpsInfo(profile, constraints, level, width, height, maxRef);
        }
        catch (IndexOutOfRangeException)
        {
            return null; // SPS truncado
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _d;
        private int _pos; // em bits

        public BitReader(byte[] data) => _d = data;

        public uint Bits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                int byteIndex = _pos >> 3;
                if (byteIndex >= _d.Length) throw new IndexOutOfRangeException();
                v = (v << 1) | (uint)((_d[byteIndex] >> (7 - (_pos & 7))) & 1);
                _pos++;
            }
            return v;
        }

        public uint Ue()
        {
            int zeros = 0;
            while (Bits(1) == 0)
                if (++zeros > 32) throw new IndexOutOfRangeException();
            return zeros == 0 ? 0 : (1u << zeros) - 1 + Bits(zeros);
        }

        public int Se()
        {
            uint k = Ue();
            return (k & 1) == 1 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }
    }
}
