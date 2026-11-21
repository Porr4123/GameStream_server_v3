using GameStream.Server.Interop;

namespace GameStream.Server.Streaming;

/// <summary>
/// Tira a janela do jogo da vista: some do Alt+Tab e da barra de tarefas e vai para uma posição
/// fora de todos os monitores. A janela continua "aberta" (o jogo roda e a captura funciona),
/// só não aparece em lugar nenhum da tela.
/// </summary>
internal sealed class WindowHider
{
    private const int OffX = -20000;
    private const int OffY = -20000;

    private readonly IntPtr _hwnd;
    private bool _styled;

    public WindowHider(IntPtr hwnd) => _hwnd = hwnd;

    public void Hide()
    {
        if (!Native.IsWindow(_hwnd)) return;

        if (!_styled)
        {
            StyleAsToolWindow();
            _styled = true;
        }
        else
        {
            MoveOffscreen();
        }
    }

    /// <summary>Chamar de tempos em tempos: alguns jogos se recentralizam sozinhos.</summary>
    public void EnsureHidden()
    {
        if (!Native.IsWindow(_hwnd)) return;
        if (Native.GetWindowRect(_hwnd, out var r) && r.Left > OffX + 5000)
            MoveOffscreen();
    }

    private void StyleAsToolWindow()
    {
        // Guarda o tamanho da área do jogo: mudar o estilo muda a espessura da borda.
        Native.GetClientRect(_hwnd, out var client);
        int cw = client.Right - client.Left;
        int ch = client.Bottom - client.Top;

        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, ex);

        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        // Refaz o tamanho da janela para a área do jogo continuar igual, e já move para fora da tela.
        long style = Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
        var rect = new Native.RECT { Left = 0, Top = 0, Right = cw, Bottom = ch };
        Native.AdjustWindowRectEx(ref rect, (uint)style, false, (uint)ex);

        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, OffX, OffY,
            rect.Right - rect.Left, rect.Bottom - rect.Top, Native.SWP_NOACTIVATE);
    }

    private void MoveOffscreen() =>
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, OffX, OffY, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
}
