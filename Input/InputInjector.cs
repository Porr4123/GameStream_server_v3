using System.Runtime.InteropServices;
using GameStream.Server.Interop;

namespace GameStream.Server.Input;

/// <summary>O que aconteceu com uma tecla pedida pelo celular.</summary>
public enum KeyOutcome
{
    /// <summary>Enviada ao jogo.</summary>
    Sent,

    /// <summary>Repetição (já apertada) ou soltura de tecla que não estava apertada: nada a fazer.</summary>
    Ignored,

    /// <summary>Fora da lista segura (<see cref="KeyMap"/>) ou combinação bloqueada (<see cref="KeyPolicy"/>).</summary>
    Rejected,

    /// <summary>O jogo não está em primeiro plano: a tecla foi DESCARTADA em vez de ir para outra janela.</summary>
    NoFocus
}

/// <summary>
/// Injeta teclas no jogo com SendInput usando scancodes (jogos de RPG Maker leem o estado do teclado).
///
/// Garantias:
///  - só as teclas de <see cref="KeyMap"/> (letras, números, setas, CTRL, SHIFT, ESC): nada de F1-F12, PrintScreen, Windows...;
///  - CTRL+ESC e CTRL+SHIFT+ESC (menu Iniciar / Gerenciador de Tarefas) são bloqueados;
///  - tecla APERTADA só é enviada se o jogo (ou outra janela do mesmo processo) estiver em primeiro plano naquele instante;
///    se o Windows não deixar trazer o jogo para frente, a tecla é descartada, nunca enviada à janela errada;
///  - só existe teclado: não há caminho para mouse.
/// </summary>
public sealed class InputInjector
{
    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _hwnd;
    private readonly uint _gamePid;
    private readonly Action? _afterFocus;
    private readonly object _lock = new();

    private long _sent, _focusAttempts, _focusFailures, _totalFocusFailures;
    private string _lastForeground = "";

    /// <summary>Teclas enviadas, e quantas vezes o Windows recusou dar foco ao jogo (as teclas, então, são descartadas).</summary>
    public (long Sent, long FocusAttempts, long FocusFailures, long TotalFocusFailures, string Foreground) TakeStats()
    {
        lock (_lock)
        {
            var r = (_sent, _focusAttempts, _focusFailures, _totalFocusFailures, _lastForeground);
            _sent = _focusAttempts = _focusFailures = 0;
            return r;
        }
    }

    /// <param name="afterFocus">Chamado depois de trazer o jogo para o foco (ex.: devolver a janela para o fundo da pilha).</param>
    public InputInjector(IntPtr gameWindow, Action? afterFocus = null)
    {
        _hwnd = gameWindow;
        _gamePid = Native.GetOwnerPid(gameWindow);
        _afterFocus = afterFocus;
    }

    public KeyOutcome Handle(string key, bool down)
    {
        if (string.IsNullOrEmpty(key) || !KeyMap.TryGet(key, out var code)) return KeyOutcome.Rejected;
        key = key.ToUpperInvariant();

        lock (_lock)
        {
            bool isDown = _down.Contains(key);

            if (!down)
            {
                if (!isDown) return KeyOutcome.Ignored;
                _down.Remove(key);
                Send(code, false); // soltar é sempre permitido: nenhuma tecla pode ficar presa
                return KeyOutcome.Sent;
            }

            if (isDown) return KeyOutcome.Ignored; // repetição
            if (KeyPolicy.IsBlockedCombo(key, _down)) return KeyOutcome.Rejected;

            if (!EnsureFocus()) return KeyOutcome.NoFocus;

            if (!Send(code, true)) return KeyOutcome.NoFocus; // o jogo perdeu o foco no último instante
            _down.Add(key);
            return KeyOutcome.Sent;
        }
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

    /// <summary>A janela em primeiro plano é do jogo? (a própria janela ou outra do mesmo processo, como uma caixa de diálogo)</summary>
    private bool GameIsForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        return fg == _hwnd || (_gamePid != 0 && Native.GetOwnerPid(fg) == _gamePid);
    }

    private bool EnsureFocus()
    {
        if (!Native.IsWindow(_hwnd)) return false;
        if (GameIsForeground()) return true;

        _focusAttempts++;
        Native.SetForegroundWindow(_hwnd);

        if (!GameIsForeground())
        {
            // O Windows só deixa trocar o foco a quem está "ligado" à janela ativa: liga as filas de entrada por um instante.
            var fg = Native.GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
            uint me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            try { Native.SetForegroundWindow(_hwnd); }
            finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
        }

        if (!GameIsForeground())
        {
            // Último recurso, ainda sem injetar tecla: restaura se estiver minimizada e usa SwitchToThisWindow (ignora o bloqueio de foreground).
            if (Native.IsIconic(_hwnd)) Native.ShowWindowAsync(_hwnd, 9); // SW_RESTORE
            Native.BringWindowToTop(_hwnd);
            Native.SwitchToThisWindow(_hwnd, false);
            Thread.Sleep(20);
        }

        if (GameIsForeground())
        {
            try { _afterFocus?.Invoke(); } catch { /* ajuste de pilha de janelas nunca derruba a tecla */ }
            return GameIsForeground();
        }

        _focusFailures++;
        _totalFocusFailures++;
        var now = Native.GetForegroundWindow();
        _lastForeground = now == IntPtr.Zero ? "(nenhuma)" : $"\"{Native.GetTitle(now)}\" [{Native.GetClass(now)}]";
        return false;
    }

    private bool Send(KeyCode code, bool down)
    {
        // Última barreira: só scancodes da lista segura chegam ao SendInput.
        if (!KeyMap.IsKnown(code)) return false;
        // E uma tecla apertada só vai se o jogo ainda está em primeiro plano neste exato instante.
        if (down && !GameIsForeground()) return false;

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
        _sent++;
        return true;
    }
}
