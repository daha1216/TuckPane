using System.Runtime.InteropServices;
using System.Text;

namespace TuckPane.Services;

internal static partial class NativeMethods
{
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int GWLP_WNDPROC = -4;
    internal const int GWLP_HWNDPARENT = -8;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_TRANSPARENT = 0x00000020L;
    internal const long WS_EX_APPWINDOW = 0x00040000L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;
    internal const long WS_POPUP = 0x80000000L;
    internal const long WS_CHILD = 0x40000000L;
    internal const long WS_VISIBLE = 0x10000000L;
    internal const long WS_EX_LAYERED = 0x00080000L;
    internal const long WS_CAPTION = 0x00C00000L;
    internal const long WS_THICKFRAME = 0x00040000L;
    internal const long WS_SYSMENU = 0x00080000L;
    internal const long WS_MINIMIZEBOX = 0x00020000L;
    internal const long WS_MAXIMIZEBOX = 0x00010000L;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOREDRAW = 0x0008;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_NOCOPYBITS = 0x0100;
    internal const uint SWP_NOOWNERZORDER = 0x0200;
    internal const uint GW_HWNDNEXT = 2;
    internal const uint GW_HWNDPREV = 3;
    internal const uint GA_ROOT = 2;
    internal const uint LWA_ALPHA = 0x00000002;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
    internal const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    internal const int DWMWA_CLOAK = 13;
    internal const int DWMWA_CLOAKED = 14;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;
    internal const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const int DWMWCP_ROUND = 2;
    internal const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    internal const int WH_MOUSE_LL = 14;
    internal const int WH_KEYBOARD_LL = 13;
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int VK_LBUTTON = 0x01;
    internal const uint MK_LBUTTON = 0x0001;
    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_APP_START_ITEM_EXTERNAL_DRAG = 0x8123;
    internal const int WM_CAPTURECHANGED = 0x0215;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_MBUTTONDOWN = 0x0207;
    internal const int WM_MBUTTONUP = 0x0208;
    internal const uint WM_MOUSEACTIVATE = 0x0021;
    internal const uint WM_SETCURSOR = 0x0020;
    internal const uint WM_NCHITTEST = 0x0084;
    internal const uint WM_NCLBUTTONDOWN = 0x00A1;
    internal const uint WM_NCLBUTTONUP = 0x00A2;
    internal const int HTCLIENT = 1;
    internal const int HTTRANSPARENT = -1;
    internal const int HTCAPTION = 2;
    internal const int HTLEFT = 10;
    internal const int HTRIGHT = 11;
    internal const int HTTOP = 12;
    internal const int HTTOPLEFT = 13;
    internal const int HTTOPRIGHT = 14;
    internal const int HTBOTTOM = 15;
    internal const int HTBOTTOMLEFT = 16;
    internal const int HTBOTTOMRIGHT = 17;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_HOTKEY = 0x0312;
    internal const int VK_ESCAPE = 0x1B;
    internal const int VK_CONTROL = 0x11;
    internal const uint IDC_ARROW = 32512;
    internal const uint IDC_SIZENWSE = 32642;
    internal const uint IDC_SIZENESW = 32643;
    internal const uint IDC_SIZEWE = 32644;
    internal const uint IDC_SIZENS = 32645;
    internal const uint WM_GETMINMAXINFO = 0x0024;
    internal const uint WM_NCACTIVATE = 0x0086;
    internal const uint WM_NCCALCSIZE = 0x0083;
    internal const uint WM_THEMECHANGED = 0x031A;
    internal const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const uint SC_MAXIMIZE = 0xF030;
    internal const int MA_NOACTIVATE = 3;
    internal const uint WM_APP = 0x8000;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_CONTEXTMENU = 0x007B;
    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_INFO = 0x00000010;
    internal const uint NOTIFYICON_VERSION_4 = 4;
    internal const uint NIIF_INFO = 0x00000001;
    internal const uint NIIF_WARNING = 0x00000002;
    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x0010;
    internal const uint LR_DEFAULTSIZE = 0x0040;
    internal const uint SIIGBF_ICONONLY = 0x00000004;
    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint SHGFI_ICON = 0x000000100;
    internal const uint SHGFI_LARGEICON = 0x000000000;
    internal const uint SHGFI_ADDOVERLAYS = 0x000000020;
    internal const uint SHGFI_OVERLAYINDEX = 0x000000040;
    internal const uint SHGFI_SYSICONINDEX = 0x000004000;
    internal const int SHIL_JUMBO = 4;
    internal const uint ILD_TRANSPARENT = 0x00000001;
    internal const uint DIB_RGB_COLORS = 0;
    internal const uint DI_NORMAL = 0x0003;
    internal const uint BI_RGB = 0;
    internal const uint SPI_GETICONMETRICS = 0x002D;
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const int SM_CXICONSPACING = 38;
    internal const int SM_CYICONSPACING = 39;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const int BLACK_BRUSH = 4;
    internal const uint LVM_GETITEMCOUNT = 0x1004;
    internal const uint LVM_GETITEMRECT = 0x100E;
    internal const uint LVM_GETITEMPOSITION = 0x1010;
    internal const uint LVM_GETITEMSPACING = 0x1033;
    internal const int LVIR_ICON = 1;
    internal const uint PROCESS_VM_OPERATION = 0x0008;
    internal const uint PROCESS_VM_READ = 0x0010;
    internal const uint PROCESS_VM_WRITE = 0x0020;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint MEM_COMMIT = 0x1000;
    internal const uint MEM_RESERVE = 0x2000;
    internal const uint MEM_RELEASE = 0x8000;
    internal const uint PAGE_READWRITE = 0x04;
    internal const uint RDW_INVALIDATE = 0x0001;
    internal const uint RDW_ALLCHILDREN = 0x0080;
    internal const uint RDW_UPDATENOW = 0x0100;

    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal static readonly IntPtr HWND_BOTTOM = new(1);
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NCCALCSIZE_PARAMS
    {
        public RECT ProposedClient;
        public RECT PreviousWindow;
        public RECT PreviousClient;
        public IntPtr WindowPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOGFONT
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharacterSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ICONMETRICS
    {
        public uint Size;
        public int HorizontalSpacing;
        public int VerticalSpacing;
        public int TitleWrap;
        public LOGFONT Font;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    internal delegate IntPtr WindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);
    internal delegate IntPtr SubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    internal static IntPtr CreateWindowEx(
        long extendedStyle,
        string className,
        string? windowName,
        long style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter) =>
        CreateWindowEx(
            unchecked((uint)extendedStyle),
            className,
            windowName,
            unchecked((uint)style),
            x,
            y,
            width,
            height,
            parent,
            menu,
            instance,
            parameter);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoForDpi(uint action, uint parameter, ref ICONMETRICS metrics, uint flags, uint dpi);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    internal static extern IntPtr LoadCursor(IntPtr instance, UIntPtr cursorName);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static extern int GetMessage(out MSG message, IntPtr hWnd, uint minimum, uint maximum);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out MSG message, IntPtr hWnd, uint minimum, uint maximum, uint removeMessage);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, out POINT buffer, UIntPtr size, out UIntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, out RECT buffer, UIntPtr size, out UIntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, ref RECT buffer, UIntPtr size, out UIntPtr bytesWritten);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, [Out] char[] className, int capacity);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr GetStockObject(int fnObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint loadFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? text);

    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    internal static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, UIntPtr id, IntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc callback, UIntPtr id);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);
}
