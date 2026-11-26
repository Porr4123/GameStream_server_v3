using GameStream.Server.Interop;
using GameStream.Server.Streaming;

namespace GameStream.Server.Input;

public enum MouseOutcome { Sent, Ignored, Rejected }

/// <summary>Como as mensagens chegam à janela. Separado para poder testar sem Windows.</summary>
internal interface IWindowMessenger
{
    /// <summary>Tamanho da área de cliente da janela do jogo.</summary>
    (int W, int H) ClientSize(IntPtr hwnd);

    /// <summary>Janela filha mais profunda sob o ponto (coordenadas de cliente de <paramref name="hwnd"/>), com o ponto já convertido para ela.</summary>
    (IntPtr Child, int X, int Y) ChildAt(IntPtr hwnd, int x, int y);

    uint OwnerPid(IntPtr hwnd);
    void Post(IntPtr hwnd, uint msg, nint wParam, nint lParam);
}

internal sealed class Win32Messenger : IWindowMessenger
{
    public (int W, int H) ClientSize(IntPtr hwnd) =>
        Native.GetClientRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);

    public (IntPtr, int, int) ChildAt(IntPtr hwnd, int x, int y)
    {
        var pt = new Native.POINT { X = x, Y = y };
        var current = hwnd;
        for (int depth = 0; depth < 8; depth++)
        {
            var child = Native.ChildWindowFromPointEx(current, pt, Native.CWP_SKIPINVISIBLE);
            if (child == IntPtr.Zero || child == current) break;
            // converte o ponto da janela "current" para a filha
            var screen = pt;
            Native.ClientToScreen(current, ref screen);
            Native.ScreenToClient(child, ref screen);
            pt = screen;
            current = child;
        }
        return (current, pt.X, pt.Y);
    }

    public uint OwnerPid(IntPtr hwnd) => Native.GetOwnerPid(hwnd);
    public void Post(IntPtr hwnd, uint msg, nint wParam, nint lParam) => Native.PostMessage(hwnd, msg, wParam, lParam);
}

/// <summary>
/// Mouse "isolado": o celular manda posição e botão; o servidor posta mensagens de mouse DIRETAMENTE na janela do jogo.
/// O mouse real do PC não se mexe, o foco não muda e nada chega a outras janelas.
///
/// Garantias:
///  - só botão esquerdo e direito e movimento (sem roda, sem botões extras);
///  - o ponto precisa cair DENTRO do jogo (faixa preta do modo Fit é descartada) e fora das caixas de exclusão do perfil;
///  - a janela que recebe a mensagem precisa ser do mesmo processo do jogo; senão a mensagem é recusada;
///  - soltar é sempre permitido e <see cref="ReleaseAll"/> solta tudo no fim da sessão (nenhum botão fica preso).
/// </summary>
public sealed class MouseInjector
{
    private const int MoveIntervalMs = 8;     // no máximo ~120 movimentos por segundo
    private const int ClickHoldMs = 40;       // tempo entre apertar e soltar num clique

    private readonly IntPtr _hwnd;
    private readonly uint _gamePid;
    private readonly IWindowMessenger _win;
    private readonly Func<(int W, int H, FitMode Fit)> _canvas;
    private readonly IReadOnlyList<(double X, double Y, double W, double H)> _exclude;
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();

    private bool _leftDown, _rightDown;
    private IntPtr _lastTarget;
    private int _lastX, _lastY;
    private long _lastMoveAt = long.MinValue / 2;
    private long _sent, _rejected, _outside;

    internal MouseInjector(IntPtr gameWindow, Func<(int W, int H, FitMode Fit)> canvas,
        IReadOnlyList<(double X, double Y, double W, double H)>? exclude = null,
        IWindowMessenger? messenger = null, Func<long>? nowMs = null)
    {
        _hwnd = gameWindow;
        _win = messenger ?? new Win32Messenger();
        _gamePid = _win.OwnerPid(gameWindow);
        _canvas = canvas;
        _exclude = exclude ?? Array.Empty<(double, double, double, double)>();
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public (long Sent, long Rejected, long Outside) TakeStats()
    {
        lock (_lock)
        {
            var r = (_sent, _rejected, _outside);
            _sent = _rejected = _outside = 0;
            return r;
        }
    }

    /// <param name="action">"move", "down", "up" ou "click".</param>
    /// <param name="button">"left" (padrão) ou "right".</param>
    /// <param name="x">0..1 do quadro transmitido (esquerda → direita).</param>
    /// <param name="y">0..1 do quadro transmitido (cima → baixo).</param>
    public MouseOutcome Handle(string? action, string? button, double x, double y)
    {
        bool right;
        switch ((button ?? "left").ToLowerInvariant())
        {
            case "left": right = false; break;
            case "right": right = true; break;
            default: return Count(MouseOutcome.Rejected);
        }

        string act = (action ?? "").ToLowerInvariant();
        if (act is not ("move" or "down" or "up" or "click")) return Count(MouseOutcome.Rejected);

        lock (_lock)
        {
            // Soltar nunca é bloqueado: usa o último ponto conhecido se o atual não vale.
            if (act == "up")
            {
                if (!IsHeld(right)) return MouseOutcome.Ignored;
                Release(right);
                return Count(MouseOutcome.Sent);
            }

            if (!TryResolve(x, y, out var target, out int cx, out int cy, out var bad))
                return Count(bad);

            switch (act)
            {
                case "move":
                {
                    long now = _nowMs();
                    if (now - _lastMoveAt < MoveIntervalMs) return MouseOutcome.Ignored;
                    _lastMoveAt = now;
                    Post(target, Native.WM_MOUSEMOVE, HeldFlags(), cx, cy);
                    break;
                }
                case "down":
                    if (IsHeld(right)) return MouseOutcome.Ignored; // repetição
                    Press(target, cx, cy, right);
                    break;
                case "click":
                    Press(target, cx, cy, right);
                    // Solta pouco depois; ReleaseAll também cobre se a sessão acabar antes.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(ClickHoldMs);
                        lock (_lock) { if (IsHeld(right)) Release(right); }
                    });
                    break;
            }
            return Count(MouseOutcome.Sent);
        }
    }

    /// <summary>Solta qualquer botão que ainda esteja apertado.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_leftDown) Release(false);
            if (_rightDown) Release(true);
        }
    }

    // ---------------- internos (sempre sob _lock) ----------------

    private bool IsHeld(bool right) => right ? _rightDown : _leftDown;
    private nint HeldFlags() => (nint)((_leftDown ? 0x1 : 0) | (_rightDown ? 0x2 : 0)); // MK_LBUTTON | MK_RBUTTON

    private void Press(IntPtr target, int cx, int cy, bool right)
    {
        Post(target, Native.WM_MOUSEMOVE, HeldFlags(), cx, cy);
        if (right) _rightDown = true; else _leftDown = true;
        Post(target, right ? Native.WM_RBUTTONDOWN : Native.WM_LBUTTONDOWN, HeldFlags(), cx, cy);
    }

    private void Release(bool right)
    {
        if (right) _rightDown = false; else _leftDown = false;
        if (_lastTarget != IntPtr.Zero)
            Post(_lastTarget, right ? Native.WM_RBUTTONUP : Native.WM_LBUTTONUP, HeldFlags(), _lastX, _lastY);
    }

    private void Post(IntPtr target, uint msg, nint wParam, int x, int y)
    {
        _lastTarget = target; _lastX = x; _lastY = y;
        nint lParam = (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        _win.Post(target, msg, wParam, lParam);
    }

    private bool TryResolve(double nx, double ny, out IntPtr target, out int cx, out int cy, out MouseOutcome bad)
    {
        target = IntPtr.Zero; cx = cy = 0; bad = MouseOutcome.Ignored;

        foreach (var b in _exclude)
            if (nx >= b.X && nx <= b.X + b.W && ny >= b.Y && ny <= b.Y + b.H) { bad = MouseOutcome.Ignored; return false; }

        var (cw, ch, fit) = _canvas();
        var (gw, gh) = _win.ClientSize(_hwnd);
        var mapped = StreamGeometry.MapToGame(cw, ch, gw, gh, fit, nx, ny);
        if (mapped is null) { _outside++; bad = MouseOutcome.Ignored; return false; }

        int px = Math.Clamp((int)(mapped.Value.X * gw), 0, Math.Max(0, gw - 1));
        int py = Math.Clamp((int)(mapped.Value.Y * gh), 0, Math.Max(0, gh - 1));

        var (child, x, y) = _win.ChildAt(_hwnd, px, py);
        if (child == IntPtr.Zero || _win.OwnerPid(child) != _gamePid) { bad = MouseOutcome.Rejected; return false; }

        target = child; cx = x; cy = y;
        return true;
    }

    private MouseOutcome Count(MouseOutcome o)
    {
        if (o == MouseOutcome.Sent) _sent++;
        else if (o == MouseOutcome.Rejected) _rejected++;
        return o;
    }
}
