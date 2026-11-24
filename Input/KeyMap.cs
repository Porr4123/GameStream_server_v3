namespace GameStream.Server.Input;

/// <summary>Scancode (conjunto 1) e se a tecla é "estendida".</summary>
public readonly record struct KeyCode(ushort Scan, bool Extended);

/// <summary>
/// ÚNICA lista de teclas que o servidor sabe injetar: letras A-Z, números 0-9, setas, CTRL, SHIFT e ESC.
/// Nada mais existe aqui de propósito: F1-F12, PrintScreen, Windows, ALT, TAB, ENTER etc. não têm scancode
/// cadastrado, então não há como enviá-los nem por engano. Para liberar outra tecla é preciso mudar este
/// arquivo (e o autoteste, que confere a lista inteira).
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<string, KeyCode> Map = Build();

    public static bool TryGet(string name, out KeyCode key) => Map.TryGetValue(name, out key);

    /// <summary>Nomes aceitos, em maiúsculas.</summary>
    public static IReadOnlyCollection<string> Names => Map.Keys;

    /// <summary>true se este scancode pertence a alguma tecla da lista (conferência final antes do SendInput).</summary>
    public static bool IsKnown(KeyCode code) => Map.ContainsValue(code);

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

        m["ESC"] = new KeyCode(0x01, false);
        m["CTRL"] = new KeyCode(0x1D, false);
        m["SHIFT"] = new KeyCode(0x2A, false);

        // Setas (teclas estendidas).
        m["UP"] = new KeyCode(0x48, true);
        m["LEFT"] = new KeyCode(0x4B, true);
        m["RIGHT"] = new KeyCode(0x4D, true);
        m["DOWN"] = new KeyCode(0x50, true);

        return m;
    }
}

/// <summary>Combinações que o Windows trata fora do jogo, mesmo só com teclas da lista.</summary>
public static class KeyPolicy
{
    /// <summary>
    /// CTRL+ESC abre o menu Iniciar (é a tecla Windows por outro caminho) e CTRL+SHIFT+ESC abre o Gerenciador de
    /// Tarefas. Por isso ESC é recusado enquanto CTRL está apertado, e CTRL enquanto ESC está apertado.
    /// </summary>
    /// <param name="key">Tecla que está sendo apertada agora.</param>
    /// <param name="held">Teclas já apertadas (maiúsculas).</param>
    public static bool IsBlockedCombo(string key, IEnumerable<string> held)
    {
        bool Holding(string name) => held.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (key.Equals("ESC", StringComparison.OrdinalIgnoreCase)) return Holding("CTRL");
        if (key.Equals("CTRL", StringComparison.OrdinalIgnoreCase)) return Holding("ESC");
        return false;
    }
}
