using System.Diagnostics;
using GameStream.Server.Interop;
using GameStream.Server.Sandbox;

namespace GameStream.Server.Diagnostics;

/// <summary>Retrato do jogo num instante.</summary>
internal readonly record struct GameStatus(
    bool ProcessAlive,
    int ActiveProcesses,
    bool WindowExists,
    bool Hung,
    bool Minimized,
    bool Visible,
    bool Foreground,
    int ClientWidth,
    int ClientHeight,
    int WindowX,
    int WindowY,
    double CpuPercent);

/// <summary>Olha o processo e a janela do jogo (só Windows). Barato: pode ser chamado todo segundo.</summary>
internal sealed class GameProbe
{
    private readonly LaunchedGame _game;
    private TimeSpan _lastCpu;
    private DateTime _lastAt = DateTime.UtcNow;

    public GameProbe(LaunchedGame game)
    {
        _game = game;
        _lastCpu = game.GetAccounting()?.Cpu ?? TimeSpan.Zero;
    }

    public GameStatus Sample()
    {
        var hwnd = _game.WindowHandle;
        bool exists = Native.IsWindow(hwnd);

        bool alive;
        try { alive = !_game.Process.HasExited; } catch { alive = false; }

        int active = alive ? 1 : 0;
        double cpu = 0;
        var acc = _game.GetAccounting();
        if (acc is { } a)
        {
            active = a.ActiveProcesses;
            alive = alive || a.ActiveProcesses > 0; // o launcher pode ter saído e o jogo (filho) continuar
            var now = DateTime.UtcNow;
            double wall = (now - _lastAt).TotalSeconds;
            if (wall > 0.2)
            {
                cpu = Math.Max(0, (a.Cpu - _lastCpu).TotalSeconds / wall * 100.0);
                _lastCpu = a.Cpu;
                _lastAt = now;
            }
            else
            {
                cpu = double.NaN; // janela de medida curta demais: o chamador mantém o último valor
            }
        }

        int cw = 0, ch = 0, wx = 0, wy = 0;
        bool hung = false, min = false, vis = false, fg = false;
        if (exists)
        {
            hung = Native.IsHungAppWindow(hwnd);
            min = Native.IsIconic(hwnd);
            vis = Native.IsWindowVisible(hwnd);
            fg = Native.GetForegroundWindow() == hwnd;
            if (Native.GetClientRect(hwnd, out var c)) { cw = c.Right - c.Left; ch = c.Bottom - c.Top; }
            if (Native.GetWindowRect(hwnd, out var w)) { wx = w.Left; wy = w.Top; }
        }

        return new GameStatus(alive, active, exists, hung, min, vis, fg, cw, ch, wx, wy, cpu);
    }

    /// <summary>Texto de uma vez só, para o log da partida do jogo.</summary>
    public static string Describe(LaunchedGame game)
    {
        var hwnd = game.WindowHandle;
        uint owner = Native.GetOwnerPid(hwnd);
        string ownerName = "?";
        try { ownerName = Process.GetProcessById((int)owner).ProcessName; } catch { /* já saiu */ }

        string launched;
        try { launched = game.Process.ProcessName; } catch { launched = "?"; }

        var st = new GameProbe(game).Sample();
        string who = owner == (uint)game.Process.Id
            ? $"processo {game.Process.Id} ({launched})"
            : $"processo iniciado {game.Process.Id} ({launched}); a JANELA é de outro processo: {owner} ({ownerName}), provavelmente o .exe é um launcher";

        return $"{who}; janela 0x{hwnd.ToInt64():X} \"{game.WindowTitle}\" classe [{Native.GetClass(hwnd)}], " +
               $"área {st.ClientWidth}x{st.ClientHeight}, em ({st.WindowX},{st.WindowY}), " +
               $"visível={Sim(st.Visible)}, minimizada={Sim(st.Minimized)}, em primeiro plano={Sim(st.Foreground)}";
    }

    internal static string Sim(bool b) => b ? "sim" : "não";
}
