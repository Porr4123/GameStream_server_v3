using System.Runtime.InteropServices;
using System.Text;

namespace GameStream.Server.Interop;

internal static class Native
{
    // ---------------- Entrada (teclado) ----------------
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // A união precisa conter MOUSEINPUT (a maior) para o INPUT ter o tamanho certo.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ---------------- Janelas ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public const uint PW_CLIENTONLY = 0x1;
    public const uint PW_RENDERFULLCONTENT = 0x2;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x2;
    public const uint GW_HWNDNEXT = 2;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int cmdShow);
    /// <summary>true se a janela não processa mensagens há alguns segundos (não bloqueia, ao contrário de PrintWindow).</summary>
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool AdjustWindowRectEx(ref RECT rect, uint style, bool hasMenu, uint exStyle);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

    public static long GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index).ToInt64() : GetWindowLong32(hWnd, index);

    public static void SetWindowLongPtr(IntPtr hWnd, int index, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, index, (IntPtr)value);
        else SetWindowLong32(hWnd, index, (int)value);
    }

    public static string GetClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static uint GetOwnerPid(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid;
    }

    public static string GetTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Primeira janela visível cujo título contém o texto (ignorando as de <paramref name="exclude"/>).</summary>
    public static IntPtr FindWindowContaining(string text, ISet<IntPtr>? exclude = null)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || (exclude?.Contains(h) ?? false)) return true;
            var title = GetTitle(h);
            if (title.Length > 0 && title.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Todas as janelas visíveis cujo título contém o texto (para saber quais já existiam antes de abrir o jogo).</summary>
    public static HashSet<IntPtr> FindAllWindowsContaining(string text)
    {
        var all = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetTitle(h).Contains(text, StringComparison.OrdinalIgnoreCase)) all.Add(h);
            return true;
        }, IntPtr.Zero);
        return all;
    }

    // ---------------- Mouse por mensagens (sem mover o mouse de verdade) ----------------
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    public const int CWP_SKIPINVISIBLE = 0x0001;

    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, POINT point, uint flags);

    // ---------------- GDI ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();

    // ---------------- GDI: composição do quadro (tela de espera e jogo) ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const uint BLACKNESS = 0x00000042;
    public const int COLORONCOLOR = 3;   // vizinho mais próximo: mantém pixel art nítida
    public const int HALFTONE = 4;       // suavizado: bom para logos
    public const int BK_TRANSPARENT = 1;
    public const int NULL_PEN = 8;

    public const uint DT_CENTER = 0x0001;
    public const uint DT_WORDBREAK = 0x0010;
    public const uint DT_SINGLELINE = 0x0020;
    public const uint DT_NOPREFIX = 0x0800;
    public const uint DT_END_ELLIPSIS = 0x8000;

    public const uint IMAGE_BITMAP = 0;
    public const uint LR_LOADFROMFILE = 0x0010;

    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImageW(IntPtr hInstance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr oldOrigin);
    [DllImport("gdi32.dll")] public static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] public static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern bool Polygon(IntPtr hdc, POINT[] points, int count);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetObjectW(IntPtr handle, int size, out BITMAP bitmap);

    /// <summary>COLORREF (0x00BBGGRR).</summary>
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
