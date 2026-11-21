using System.Runtime.InteropServices;
using GameStream.Server.Interop;

namespace GameStream.Server.Streaming;

/// <summary>
/// Produz, num tamanho FIXO, o quadro que vai para o codificador:
///  - tela de espera: fundo preto + logo + texto de status (enquanto o jogo abre, se der erro ou se fechar);
///  - jogo: a captura da janela, redimensionada e centralizada (sem distorcer a proporção).
/// Como o tamanho nunca muda, o cliente recebe vídeo desde o primeiro instante, com ou sem jogo.
/// </summary>
internal sealed class FrameComposer : IFrameSource, IDisposable
{
    private readonly object _lock = new();
    private readonly int _w;
    private readonly int _h;
    private readonly string? _logoPath;

    private readonly IntPtr _dc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldBitmap;
    private readonly IntPtr _bits;
    private readonly byte[] _frame;

    private WindowCapture? _capture;
    private int _dx, _dy, _dw, _dh;
    private bool _disposed;

    public int Width => _w;
    public int Height => _h;

    public FrameComposer(int width, int height, string? logoPath)
    {
        _w = width;
        _h = height;
        _logoPath = logoPath;

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = _w,
                biHeight = -_h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            _dc = Native.CreateCompatibleDC(screenDc);
            _bitmap = Native.CreateDIBSection(screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (_dc == IntPtr.Zero || _bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            if (_bitmap != IntPtr.Zero) Native.DeleteObject(_bitmap);
            if (_dc != IntPtr.Zero) Native.DeleteDC(_dc);
            throw new InvalidOperationException("Falha ao criar o quadro de vídeo (GDI).");
        }

        _oldBitmap = Native.SelectObject(_dc, _bitmap);
        _frame = new byte[_w * _h * 4];
        Native.PatBlt(_dc, 0, 0, _w, _h, Native.BLACKNESS);
    }

    /// <summary>Mostra a tela de espera (preto + logo) com um texto embaixo.</summary>
    public void ShowStatus(string text)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _capture?.Dispose();
            _capture = null;
            DrawPlaceholder(text);
        }
    }

    /// <summary>true enquanto o que vai para o vídeo é a captura do jogo (e não a tela de espera).</summary>
    public bool HasGame
    {
        get { lock (_lock) return _capture is not null; }
    }

    /// <summary>Contadores da captura desde a última leitura. Zerado se não há jogo.</summary>
    public CaptureCounters TakeCaptureCounters()
    {
        lock (_lock) return _capture?.TakeCounters() ?? default;
    }

    /// <summary>Passa a mostrar o jogo. Se falhar (ex.: janela minimizada), a tela atual continua. Devolve um texto descrevendo o enquadramento.</summary>
    public string ShowGame(IntPtr hwnd, CaptureMethod method)
    {
        lock (_lock)
        {
            if (_disposed) return "composição já encerrada";

            var capture = new WindowCapture(hwnd, method);

            // Cabe dentro do quadro mantendo a proporção; sobra faixa preta nas laterais ou em cima/embaixo.
            double scale = Math.Min((double)_w / capture.Width, (double)_h / capture.Height);
            _dw = Math.Max(2, (int)(capture.Width * scale));
            _dh = Math.Max(2, (int)(capture.Height * scale));
            _dx = (_w - _dw) / 2;
            _dy = (_h - _dh) / 2;

            _capture?.Dispose();
            _capture = capture;
            Native.PatBlt(_dc, 0, 0, _w, _h, Native.BLACKNESS);

            return $"captura {capture.Width}x{capture.Height} ({method}) -> {_dw}x{_dh} em ({_dx},{_dy}) dentro de {_w}x{_h}";
        }
    }

    /// <summary>Devolve o quadro atual (BGRA). O buffer é reaproveitado: use antes de pedir o próximo.</summary>
    public byte[] NextFrame()
    {
        lock (_lock)
        {
            if (_disposed) return _frame;

            _capture?.DrawTo(_dc, _dx, _dy, _dw, _dh);
            Native.GdiFlush();
            Marshal.Copy(_bits, _frame, 0, _frame.Length);
            return _frame;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _capture?.Dispose();
            _capture = null;

            Native.SelectObject(_dc, _oldBitmap);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(_dc);
        }
    }

    // ---------------- tela de espera ----------------

    private void DrawPlaceholder(string status)
    {
        Native.PatBlt(_dc, 0, 0, _w, _h, Native.BLACKNESS);

        int unit = Math.Min(_w, _h);
        int cx = _w / 2;
        int cy = (int)(_h * 0.36);

        if (!TryDrawLogoFile(cx, cy, out _))
        {
            int bottom = DrawDefaultLogo(cx, cy, unit);
            DrawText("GameStream", unit * 7 / 100, 700, Native.Rgb(255, 255, 255),
                new Native.RECT { Left = 0, Top = bottom + unit * 3 / 100, Right = _w, Bottom = bottom + unit * 13 / 100 },
                Native.DT_CENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        }

        DrawText(status, unit * 45 / 1000, 400, Native.Rgb(170, 170, 170),
            new Native.RECT { Left = _w / 10, Top = (int)(_h * 0.72), Right = _w - _w / 10, Bottom = _h - unit / 20 },
            Native.DT_CENTER | Native.DT_WORDBREAK | Native.DT_NOPREFIX | Native.DT_END_ELLIPSIS);
    }

    /// <summary>Logo padrão: círculo azul com um triângulo de "play". Devolve a coordenada Y da borda de baixo.</summary>
    private int DrawDefaultLogo(int cx, int cy, int unit)
    {
        int r = unit * 16 / 100;

        IntPtr nullPen = Native.GetStockObject(Native.NULL_PEN);
        IntPtr oldPen = Native.SelectObject(_dc, nullPen);

        IntPtr accent = Native.CreateSolidBrush(Native.Rgb(0x3B, 0x82, 0xF6));
        IntPtr oldBrush = Native.SelectObject(_dc, accent);
        Native.Ellipse(_dc, cx - r, cy - r, cx + r, cy + r);

        IntPtr white = Native.CreateSolidBrush(Native.Rgb(255, 255, 255));
        Native.SelectObject(_dc, white);
        var triangle = new[]
        {
            new Native.POINT { X = cx - r * 30 / 100, Y = cy - r * 45 / 100 },
            new Native.POINT { X = cx - r * 30 / 100, Y = cy + r * 45 / 100 },
            new Native.POINT { X = cx + r * 55 / 100, Y = cy }
        };
        Native.Polygon(_dc, triangle, triangle.Length);

        Native.SelectObject(_dc, oldBrush);
        Native.SelectObject(_dc, oldPen);
        Native.DeleteObject(accent);
        Native.DeleteObject(white);

        return cy + r;
    }

    /// <summary>Logo do dono: arquivo .bmp (desenhado no centro, mantendo a proporção).</summary>
    private bool TryDrawLogoFile(int cx, int cy, out int bottom)
    {
        bottom = cy;
        if (string.IsNullOrWhiteSpace(_logoPath) || !File.Exists(_logoPath)) return false;

        IntPtr bmp = Native.LoadImageW(IntPtr.Zero, _logoPath, Native.IMAGE_BITMAP, 0, 0, Native.LR_LOADFROMFILE);
        if (bmp == IntPtr.Zero) return false;

        IntPtr src = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            if (Native.GetObjectW(bmp, Marshal.SizeOf<Native.BITMAP>(), out var info) == 0 ||
                info.bmWidth <= 0 || info.bmHeight <= 0)
                return false;

            src = Native.CreateCompatibleDC(_dc);
            old = Native.SelectObject(src, bmp);

            double scale = Math.Min(_w * 0.6 / info.bmWidth, _h * 0.45 / info.bmHeight);
            int dw = Math.Max(1, (int)(info.bmWidth * scale));
            int dh = Math.Max(1, (int)(info.bmHeight * scale));
            int dx = cx - dw / 2;
            int dy = cy - dh / 2;

            Native.SetStretchBltMode(_dc, Native.HALFTONE);
            Native.SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero);
            Native.StretchBlt(_dc, dx, dy, dw, dh, src, 0, 0, info.bmWidth, info.bmHeight, Native.SRCCOPY);

            bottom = dy + dh;
            return true;
        }
        finally
        {
            if (src != IntPtr.Zero)
            {
                Native.SelectObject(src, old);
                Native.DeleteDC(src);
            }
            Native.DeleteObject(bmp);
        }
    }

    private void DrawText(string text, int fontHeight, int weight, uint color, Native.RECT rect, uint format)
    {
        IntPtr font = Native.CreateFontW(-Math.Max(10, fontHeight), 0, 0, 0, weight, 0, 0, 0,
            1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
        IntPtr oldFont = Native.SelectObject(_dc, font);

        Native.SetBkMode(_dc, Native.BK_TRANSPARENT);
        Native.SetTextColor(_dc, color);
        Native.DrawTextW(_dc, text, -1, ref rect, format);

        Native.SelectObject(_dc, oldFont);
        Native.DeleteObject(font);
    }
}
