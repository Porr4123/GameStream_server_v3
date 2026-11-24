using System.Runtime.InteropServices;
using GameStream.Server.Interop;

namespace GameStream.Server.Streaming;

internal enum CaptureMethod
{
    /// <summary>PrintWindow: funciona com a janela fora da tela ou coberta.</summary>
    PrintWindow,

    /// <summary>Copia da tela: a janela precisa estar visível e sem nada por cima.</summary>
    Screen
}

/// <summary>Contadores da captura desde a última leitura.</summary>
internal readonly record struct CaptureCounters(long Attempts, long Failures, long HungSkips, long TotalAttempts, long TotalFailures);

/// <summary>
/// Captura só a área do jogo (sem borda nem barra de título) e a desenha, redimensionada,
/// em outro contexto de desenho (o quadro que vai para o codificador).
/// NÃO funciona com a janela minimizada.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly CaptureMethod _method;
    private readonly IntPtr _memDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldBitmap;
    private readonly IntPtr _bits;

    private long _attempts, _failures, _hungSkips, _totalAttempts, _totalFailures;

    public CaptureMethod Method => _method;
    public int Width { get; }
    public int Height { get; }

    public WindowCapture(IntPtr hwnd, CaptureMethod method)
    {
        _hwnd = hwnd;
        _method = method;

        if (!Native.GetClientRect(hwnd, out var rc))
            throw new InvalidOperationException("Não consegui medir a janela do jogo.");

        Width = rc.Right - rc.Left;
        Height = rc.Bottom - rc.Top;
        if (Width < 16 || Height < 16 || Native.IsIconic(hwnd))
            throw new InvalidOperationException("A janela do jogo está minimizada ou sem área visível.");

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = Width,
                biHeight = -Height, // negativo = de cima para baixo
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0   // BI_RGB
            }
        };

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Native.CreateCompatibleDC(screenDc);
            _bitmap = Native.CreateDIBSection(screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (_memDc == IntPtr.Zero || _bitmap == IntPtr.Zero)
        {
            if (_bitmap != IntPtr.Zero) Native.DeleteObject(_bitmap);
            if (_memDc != IntPtr.Zero) Native.DeleteDC(_memDc);
            throw new InvalidOperationException("Falha ao criar o buffer de captura (GDI).");
        }

        _oldBitmap = Native.SelectObject(_memDc, _bitmap);
    }

    /// <summary>
    /// Lê e zera os contadores. Chamar com o mesmo lock de <see cref="DrawTo"/> (o FrameComposer cuida disso).
    /// Failures = o Windows disse que a captura FALHOU (PrintWindow/BitBlt devolveram false).
    /// </summary>
    public CaptureCounters TakeCounters()
    {
        var c = new CaptureCounters(_attempts, _failures, _hungSkips, _totalAttempts, _totalFailures);
        _attempts = _failures = _hungSkips = 0;
        return c;
    }

    /// <summary>Captura um quadro do jogo e o desenha (redimensionado) no retângulo indicado de destDc.</summary>
    public void DrawTo(IntPtr destDc, int dx, int dy, int dw, int dh)
    {
        if (_method == CaptureMethod.PrintWindow)
        {
            // PrintWindow espera o jogo responder e não tem limite de tempo: com a janela travada ele prenderia a
            // captura (e, com ela, todo o vídeo). Se o Windows já considera a janela sem resposta, repete a última
            // imagem capturada: o vídeo continua fluindo e volta ao normal sozinho quando o jogo destravar.
            if (Native.IsHungAppWindow(_hwnd))
            {
                _hungSkips++;
            }
            else
            {
                _attempts++;
                _totalAttempts++;
                if (!Native.PrintWindow(_hwnd, _memDc, Native.PW_CLIENTONLY | Native.PW_RENDERFULLCONTENT))
                {
                    _failures++;
                    _totalFailures++;
                }
            }
        }
        else
        {
            var origin = new Native.POINT();
            Native.ClientToScreen(_hwnd, ref origin);

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            try
            {
                _attempts++;
                _totalAttempts++;
                if (!Native.BitBlt(_memDc, 0, 0, Width, Height, screen, origin.X, origin.Y,
                        Native.SRCCOPY | Native.CAPTUREBLT))
                {
                    _failures++;
                    _totalFailures++;
                }
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        if (dw == Width && dh == Height)
        {
            Native.BitBlt(destDc, dx, dy, dw, dh, _memDc, 0, 0, Native.SRCCOPY); // 1:1: cópia direta, sem reamostrar
        }
        else
        {
            // Só reduz aqui quando o jogo é maior que a transmissão. HALFTONE tira a média dos pixels (sem serrilhado);
            // COLORONCOLOR simplesmente jogava pixels fora.
            Native.SetStretchBltMode(destDc, Native.HALFTONE);
            Native.SetBrushOrgEx(destDc, 0, 0, IntPtr.Zero);
            Native.StretchBlt(destDc, dx, dy, dw, dh, _memDc, 0, 0, Width, Height, Native.SRCCOPY);
        }
    }

    public void Dispose()
    {
        Native.SelectObject(_memDc, _oldBitmap);
        Native.DeleteObject(_bitmap);
        Native.DeleteDC(_memDc);
    }
}
