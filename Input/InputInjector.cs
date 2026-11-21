using System.Runtime.InteropServices;
using GameStream.Server.Interop;

namespace GameStream.Server.Input;

/// <summary>
/// Injeta teclas no jogo com SendInput usando scancodes (jogos de RPG Maker leem o estado do teclado).
/// Só aceita as teclas listadas no perfil do jogo. Não existe nenhum caminho para mouse.
/// </summary>
public sealed class InputInjector
{
    private readonly HashSet<string> _allowed;
    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _hwnd;
    private readonly object _lock = new();

    public InputInjector(IEnumerable<string> allowedKeys, IntPtr gameWindow)
    {
        _allowed = new HashSet<string>(allowedKeys, StringComparer.OrdinalIgnoreCase);
        _hwnd = gameWindow;
    }

    /// <returns>false se a tecla não é permitida ou não existe.</returns>
    public bool Handle(string key, bool down)
    {
        if (!_allowed.Contains(key) || !KeyMap.TryGet(key, out var code)) return false;

        lock (_lock)
        {
            if (down == _down.Contains(key)) return true; // ignora repetição

            if (down)
            {
                EnsureFocus();
                _down.Add(key);
            }
            else
            {
                _down.Remove(key);
            }

            Send(code, down);
        }
        return true;
    }

    /// <summary>Solta tudo (chamar sempre que o cliente desconectar, para nenhuma tecla ficar presa).</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (var key in _down.ToArray())
                if (KeyMap.TryGet(key, out var code)) Send(code, false);
            _down.Clear();
        }
    }

    private void EnsureFocus()
    {
        // O Windows pode recusar trazer a janela para frente; por isso o ideal é o jogo
        // rodar num monitor virtual / sessão dedicada. TODO.
        if (Native.IsWindow(_hwnd) && Native.GetForegroundWindow() != _hwnd)
            Native.SetForegroundWindow(_hwnd);
    }

    private static void Send(KeyCode code, bool down)
    {
        uint flags = Native.KEYEVENTF_SCANCODE;
        if (code.Extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= Native.KEYEVENTF_KEYUP;

        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT { wVk = 0, wScan = code.Scan, dwFlags = flags }
            }
        };

        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }
}
