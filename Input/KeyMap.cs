namespace GameStream.Server.Input;

/// <summary>Scancode (conjunto 1) e se a tecla é "estendida".</summary>
public readonly record struct KeyCode(ushort Scan, bool Extended);

public static class KeyMap
{
    private static readonly Dictionary<string, KeyCode> Map = Build();

    public static bool TryGet(string name, out KeyCode key) => Map.TryGetValue(name, out key);

    private static Dictionary<string, KeyCode> Build()
    {
        var m = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);

        void Row(string keys, int firstScan)
        {
            for (int i = 0; i < keys.Length; i++)
                m[keys[i].ToString()] = new KeyCode((ushort)(firstScan + i), false);
        }

        Row("1234567890", 0x02);
        Row("QWERTYUIOP", 0x10);
        Row("ASDFGHJKL", 0x1E);
        Row("ZXCVBNM", 0x2C);

        for (int i = 0; i < 10; i++) m[$"F{i + 1}"] = new KeyCode((ushort)(0x3B + i), false);
        m["F11"] = new KeyCode(0x57, false);
        m["F12"] = new KeyCode(0x58, false);

        m["ESC"] = new KeyCode(0x01, false);
        m["BACKSPACE"] = new KeyCode(0x0E, false);
        m["TAB"] = new KeyCode(0x0F, false);
        m["ENTER"] = new KeyCode(0x1C, false);
        m["CTRL"] = new KeyCode(0x1D, false);
        m["SHIFT"] = new KeyCode(0x2A, false);
        m["ALT"] = new KeyCode(0x38, false);
        m["SPACE"] = new KeyCode(0x39, false);

        // Teclas estendidas.
        m["HOME"] = new KeyCode(0x47, true);
        m["UP"] = new KeyCode(0x48, true);
        m["PGUP"] = new KeyCode(0x49, true);
        m["LEFT"] = new KeyCode(0x4B, true);
        m["RIGHT"] = new KeyCode(0x4D, true);
        m["END"] = new KeyCode(0x4F, true);
        m["DOWN"] = new KeyCode(0x50, true);
        m["PGDN"] = new KeyCode(0x51, true);

        return m;
    }
}
