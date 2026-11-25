using GameStream.Server.Interop;

namespace GameStream.Server.Streaming;

internal enum HideMode
{
    /// <summary>Fora de todos os monitores (-20000,-20000). Alguns jogos (Chromium, DirectX) param de desenhar assim.</summary>
    Offscreen,

    /// <summary>Dentro do monitor, quase 100% transparente, sem receber clique e SEMPRE abaixo de todas as outras janelas.</summary>
    Transparent
}

/// <summary>
/// Tira a janela do jogo da vista (fora do Alt+Tab e da barra de tarefas). A janela continua "aberta": o jogo roda
/// e a captura (PrintWindow) funciona.
///
/// Offscreen: fica fora de todos os monitores.
/// Transparent: o Windows só desenha de verdade o que está dentro da área de trabalho, então a janela fica DENTRO do
/// monitor, mas com opacidade mínima (1/255), transparente a cliques e no fundo da pilha de janelas.
/// </summary>
internal sealed class WindowHider
{
    private const int OffX = -20000;
    private const int OffY = -20000;

    /// <summary>Opacidade da janela no modo Transparent (0-255). 1 = invisível a olho nu, mas ainda "desenhada" pelo Windows.</summary>
    private const byte Alpha = 1;

    private readonly IntPtr _hwnd;
    private readonly HideMode _mode;
    private bool _styled;
    private bool _warnedTooBig;

    public WindowHider(IntPtr hwnd, HideMode mode = HideMode.Offscreen)
    {
        _hwnd = hwnd;
        _mode = mode;
    }

    public HideMode Mode => _mode;

    /// <summary>Tamanho máximo da janela em pixels (0 = o do monitor). Janela maior é reduzida mantendo a proporção: captura menos pixels e fica toda dentro da tela.</summary>
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }

    /// <summary>Reduz (w,h) para caber em (maxW,maxH) sem mudar a proporção. Nunca aumenta; tamanhos pares (o H.264 gosta).</summary>
    public static (int W, int H) FitSize(int w, int h, int maxW, int maxH)
    {
        if (w <= 0 || h <= 0 || maxW <= 0 || maxH <= 0) return (w, h);
        double k = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / h));
        if (k >= 1.0) return (w, h);
        return (Math.Max(2, (int)(w * k) & ~1), Math.Max(2, (int)(h * k) & ~1));
    }

    /// <summary>Limite efetivo: o configurado, ou o monitor quando 0.</summary>
    private (int W, int H) Limit()
    {
        int mw = MaxWidth, mh = MaxHeight;
        if (TryMonitor(out var m))
        {
            int bw = m.rcMonitor.Right - m.rcMonitor.Left, bh = m.rcMonitor.Bottom - m.rcMonitor.Top;
            mw = mw > 0 ? Math.Min(mw, bw) : bw;
            mh = mh > 0 ? Math.Min(mh, bh) : bh;
        }
        return (mw, mh);
    }

    /// <summary>Mensagens de aviso que o chamador deve escrever no log (ex.: janela maior que o monitor).</summary>
    public string? TakeWarning()
    {
        var w = _warning;
        _warning = null;
        return w;
    }

    private string? _warning;

    public void Hide()
    {
        if (!Native.IsWindow(_hwnd)) return;

        if (!_styled)
        {
            Style();
            _styled = true;
            KeepAtBottom(); // guarda quem ficou logo abaixo, para saber depois se algo passou por baixo
        }
        else
        {
            Place();
        }
    }

    /// <summary>Chamar de tempos em tempos: jogos se recentralizam, voltam ao topo ou refazem o estilo sozinhos.</summary>
    /// <returns>true se algo precisou ser corrigido.</returns>
    public bool EnsureHidden()
    {
        if (!Native.IsWindow(_hwnd)) return false;

        if (_mode == HideMode.Offscreen)
        {
            if (Native.GetWindowRect(_hwnd, out var r) && r.Left > OffX + 5000)
            {
                MoveOffscreen();
                return true;
            }
            return false;
        }

        bool fixedSomething = false;

        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        const long need = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW;
        if ((ex & need) != need)
        {
            ApplyTransparentStyle();
            fixedSomething = true;
        }

        if (!InsideMonitor())
        {
            MoveInsideMonitor();
            fixedSomething = true;
        }

        if (!AtBottom())
        {
            KeepAtBottom();
            fixedSomething = true;
        }

        return fixedSomething;
    }

    /// <summary>Manda a janela para o fundo SEM ativá-la (se ela é a janela com foco, continua recebendo o teclado).</summary>
    public void KeepAtBottom()
    {
        if (_mode != HideMode.Transparent || !Native.IsWindow(_hwnd)) return;
        // HWND_BOTTOM também tira o "sempre no topo" (WS_EX_TOPMOST) se o jogo tiver pedido.
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _belowMe = Native.GetWindow(_hwnd, Native.GW_HWNDNEXT);
        _bottomKnown = true;
    }

    /// <summary>Descrição para o log.</summary>
    public string Describe()
    {
        if (!Native.GetWindowRect(_hwnd, out var r)) return "janela inexistente";
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        return _mode == HideMode.Transparent
            ? $"transparente em ({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}, dentro do monitor: {InsideMonitor()}, " +
              $"no fundo: {AtBottom()}, layered={(ex & Native.WS_EX_LAYERED) != 0}"
            : $"fora da tela em ({r.Left},{r.Top})";
    }

    // ---------------- estilo ----------------

    private void Style()
    {
        // Guarda o tamanho da área do jogo: mudar o estilo muda a espessura da borda.
        Native.GetClientRect(_hwnd, out var client);
        int cw = client.Right - client.Left;
        int ch = client.Bottom - client.Top;

        if (_mode == HideMode.Transparent) ApplyTransparentStyle();
        else ApplyToolWindowStyle();

        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        // Refaz o tamanho da janela para a área do jogo continuar igual.
        long style = Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        var rect = new Native.RECT { Left = 0, Top = 0, Right = cw, Bottom = ch };
        Native.AdjustWindowRectEx(ref rect, (uint)style, false, (uint)ex);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;

        if (_mode == HideMode.Offscreen)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, OffX, OffY, w, h, Native.SWP_NOACTIVATE);
            return;
        }

        var (mx, my) = MonitorOrigin();
        var (lw, lh) = Limit();
        var (fw, fh) = FitSize(w, h, lw, lh);
        if (fw != w || fh != h)
        {
            _warning = $"A janela do jogo ({w}x{h}) passava do limite ({lw}x{lh}); reduzida para {fw}x{fh} (mesma proporção) para a captura ficar mais leve.";
            w = fw; h = fh;
            _warnedTooBig = true;
        }
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, mx, my, w, h, Native.SWP_NOACTIVATE);
        CheckTooBig();
    }

    private void ApplyToolWindowStyle()
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    private void ApplyTransparentStyle()
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, ex);
        if (!Native.SetLayeredWindowAttributes(_hwnd, 0, Alpha, Native.LWA_ALPHA))
            _warning = "SetLayeredWindowAttributes falhou: a janela pode não ter ficado transparente (use hideMode \"offscreen\").";
    }

    // ---------------- posição e pilha de janelas ----------------

    private void Place()
    {
        if (_mode == HideMode.Offscreen) { MoveOffscreen(); return; }
        ApplyTransparentStyle();
        MoveInsideMonitor();
        KeepAtBottom();
    }

    private void MoveOffscreen() =>
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, OffX, OffY, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    private bool TryMonitor(out Native.MONITORINFO info)
    {
        info = new Native.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        var mon = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        return mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref info);
    }

    private (int X, int Y) MonitorOrigin() =>
        TryMonitor(out var m) ? (m.rcMonitor.Left, m.rcMonitor.Top) : (0, 0);

    /// <summary>A janela inteira está dentro dos limites do monitor? (parte fora da tela não é desenhada pelo Windows)</summary>
    private bool InsideMonitor()
    {
        if (!Native.GetWindowRect(_hwnd, out var r) || !TryMonitor(out var m)) return true;
        var b = m.rcMonitor;
        bool tooBig = r.Right - r.Left > b.Right - b.Left || r.Bottom - r.Top > b.Bottom - b.Top;
        if (tooBig) return r.Left == b.Left && r.Top == b.Top; // maior que o monitor: o melhor possível é o canto
        return r.Left >= b.Left && r.Top >= b.Top && r.Right <= b.Right && r.Bottom <= b.Bottom;
    }

    private void MoveInsideMonitor()
    {
        if (!Native.GetWindowRect(_hwnd, out var r) || !TryMonitor(out var m)) return;
        var b = m.rcMonitor;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        int x = Math.Clamp(r.Left, b.Left, Math.Max(b.Left, b.Right - w));
        int y = Math.Clamp(r.Top, b.Top, Math.Max(b.Top, b.Bottom - h));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        CheckTooBig();
    }

    private void CheckTooBig()
    {
        if (_warnedTooBig || !Native.GetWindowRect(_hwnd, out var r) || !TryMonitor(out var m)) return;
        var b = m.rcMonitor;
        if (r.Right - r.Left > b.Right - b.Left || r.Bottom - r.Top > b.Bottom - b.Top)
        {
            _warnedTooBig = true;
            _warning = $"A janela do jogo ({r.Right - r.Left}x{r.Bottom - r.Top}) é maior que o monitor " +
                       $"({b.Right - b.Left}x{b.Bottom - b.Top}): parte dela fica fora da tela e o Windows não desenha essa parte. " +
                       "Diminua a resolução/janela do jogo.";
        }
    }

    // Depois de HWND_BOTTOM o Windows pode deixar algumas janelas do sistema (ex.: a área de trabalho) atrás da nossa.
    // Por isso "no fundo" = a janela logo abaixo é a mesma que estava logo depois de mandarmos para o fundo.
    private IntPtr _belowMe;
    private bool _bottomKnown;

    /// <summary>true se a janela continua no fundo da pilha (e não virou "sempre no topo").</summary>
    private bool AtBottom()
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TOPMOST) != 0) return false;
        var below = Native.GetWindow(_hwnd, Native.GW_HWNDNEXT);
        return _bottomKnown ? below == _belowMe : below == IntPtr.Zero;
    }
}
