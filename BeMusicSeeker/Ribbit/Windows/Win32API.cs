using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Ribbit.Windows;

public static class Win32API
{
    [Serializable]
    public struct POINT(int x, int y)
    {
        public int X = x;

        public int Y = y;

        public POINT(Point pt)
            : this(pt.X, pt.Y)
        {
        }

        public static implicit operator Point(POINT p)
        {
            return new Point(p.X, p.Y);
        }

        public static implicit operator POINT(Point p)
        {
            return new POINT(p.X, p.Y);
        }
    }

    [Serializable]
    public struct RECT(int left, int top, int right, int bottom)
    {
        public int Left = left;

        public int Top = top;

        public int Right = right;

        public int Bottom = bottom;

        public int X
        {
            readonly get
            {
                return Left;
            }
            set
            {
                Right -= Left - value;
                Left = value;
            }
        }

        public int Y
        {
            readonly get
            {
                return Top;
            }
            set
            {
                Bottom -= Top - value;
                Top = value;
            }
        }

        public int Height
        {
            readonly get
            {
                return Bottom - Top;
            }
            set
            {
                Bottom = value + Top;
            }
        }

        public int Width
        {
            readonly get
            {
                return Right - Left;
            }
            set
            {
                Right = value + Left;
            }
        }

        public Point Location
        {
            readonly get
            {
                return new Point(Left, Top);
            }
            set
            {
                X = value.X;
                Y = value.Y;
            }
        }

        public Size Size
        {
            readonly get
            {
                return new Size(Width, Height);
            }
            set
            {
                Width = value.Width;
                Height = value.Height;
            }
        }

        public RECT(Rectangle r)
            : this(r.Left, r.Top, r.Right, r.Bottom)
        {
        }

        public static implicit operator Rectangle(RECT r)
        {
            return new Rectangle(r.Left, r.Top, r.Width, r.Height);
        }

        public static implicit operator RECT(Rectangle r)
        {
            return new RECT(r);
        }

        public static bool operator ==(RECT r1, RECT r2)
        {
            return r1.Equals(r2);
        }

        public static bool operator !=(RECT r1, RECT r2)
        {
            return !r1.Equals(r2);
        }

        public readonly bool Equals(RECT r)
        {
            if (r.Left == Left && r.Top == Top && r.Right == Right)
            {
                return r.Bottom == Bottom;
            }
            return false;
        }

        public override readonly bool Equals(object obj)
        {
            if (obj is RECT)
            {
                return Equals((RECT)obj);
            }
            if (obj is Rectangle)
            {
                return Equals(new RECT((Rectangle)obj));
            }
            return false;
        }

        public override readonly int GetHashCode()
        {
            return ((Rectangle)this/*cast due to .constrained prefix*/).GetHashCode();
        }

        public override readonly string ToString()
        {
            return string.Format(CultureInfo.CurrentCulture, "{{Left={0},Top={1},Right={2},Bottom={3}}}", Left, Top, Right, Bottom);
        }
    }

    [Serializable]
    public struct WINDOWPLACEMENT
    {
        public int Length;

        public int Flags;

        public ShowWindowCommands ShowCmd;

        public POINT MinPosition;

        public POINT MaxPosition;

        public RECT NormalPosition;
    }

    [Flags]
    public enum SetWindowPosFlags : uint
    {
        AsynchronousWindowPosition = 0x4000u,
        DeferErase = 0x2000u,
        DrawFrame = 0x20u,
        FrameChanged = 0x20u,
        HideWindow = 0x80u,
        DoNotActivate = 0x10u,
        DoNotCopyBits = 0x100u,
        IgnoreMove = 2u,
        DoNotChangeOwnerZOrder = 0x200u,
        DoNotRedraw = 8u,
        DoNotReposition = 0x200u,
        DoNotSendChangingEvent = 0x400u,
        IgnoreResize = 1u,
        IgnoreZOrder = 4u,
        ShowWindow = 0x40u
    }

    public enum ShowWindowCommands
    {
        Hide = 0,
        Normal = 1,
        ShowMinimized = 2,
        Maximize = 3,
        ShowMaximized = 3,
        ShowNoActivate = 4,
        Show = 5,
        Minimize = 6,
        ShowMinNoActive = 7,
        ShowNA = 8,
        Restore = 9,
        ShowDefault = 10,
        ForceMinimize = 11
    }

    public enum VK
    {
        LBUTTON = 1,
        RBUTTON = 2,
        CANCEL = 3,
        MBUTTON = 4,
        XBUTTON1 = 5,
        XBUTTON2 = 6,
        BACK = 8,
        TAB = 9,
        CLEAR = 12,
        RETURN = 13,
        SHIFT = 16,
        CONTROL = 17,
        MENU = 18,
        PAUSE = 19,
        CAPITAL = 20,
        KANA = 21,
        HANGUL = 21,
        JUNJA = 23,
        FINAL = 24,
        HANJA = 25,
        KANJI = 25,
        ESCAPE = 27,
        CONVERT = 28,
        NONCONVERT = 29,
        ACCEPT = 30,
        MODECHANGE = 31,
        SPACE = 32,
        PRIOR = 33,
        NEXT = 34,
        END = 35,
        HOME = 36,
        LEFT = 37,
        UP = 38,
        RIGHT = 39,
        DOWN = 40,
        SELECT = 41,
        PRINT = 42,
        EXECUTE = 43,
        SNAPSHOT = 44,
        INSERT = 45,
        DELETE = 46,
        HELP = 47,
        KEY_0 = 48,
        KEY_1 = 49,
        KEY_2 = 50,
        KEY_3 = 51,
        KEY_4 = 52,
        KEY_5 = 53,
        KEY_6 = 54,
        KEY_7 = 55,
        KEY_8 = 56,
        KEY_9 = 57,
        KEY_A = 65,
        KEY_B = 66,
        KEY_C = 67,
        KEY_D = 68,
        KEY_E = 69,
        KEY_F = 70,
        KEY_G = 71,
        KEY_H = 72,
        KEY_I = 73,
        KEY_J = 74,
        KEY_K = 75,
        KEY_L = 76,
        KEY_M = 77,
        KEY_N = 78,
        KEY_O = 79,
        KEY_P = 80,
        KEY_Q = 81,
        KEY_R = 82,
        KEY_S = 83,
        KEY_T = 84,
        KEY_U = 85,
        KEY_V = 86,
        KEY_W = 87,
        KEY_X = 88,
        KEY_Y = 89,
        KEY_Z = 90,
        LWIN = 91,
        RWIN = 92,
        APPS = 93,
        SLEEP = 95,
        NUMPAD0 = 96,
        NUMPAD1 = 97,
        NUMPAD2 = 98,
        NUMPAD3 = 99,
        NUMPAD4 = 100,
        NUMPAD5 = 101,
        NUMPAD6 = 102,
        NUMPAD7 = 103,
        NUMPAD8 = 104,
        NUMPAD9 = 105,
        MULTIPLY = 106,
        ADD = 107,
        SEPARATOR = 108,
        SUBTRACT = 109,
        DECIMAL = 110,
        DIVIDE = 111,
        F1 = 112,
        F2 = 113,
        F3 = 114,
        F4 = 115,
        F5 = 116,
        F6 = 117,
        F7 = 118,
        F8 = 119,
        F9 = 120,
        F10 = 121,
        F11 = 122,
        F12 = 123,
        F13 = 124,
        F14 = 125,
        F15 = 126,
        F16 = 127,
        F17 = 128,
        F18 = 129,
        F19 = 130,
        F20 = 131,
        F21 = 132,
        F22 = 133,
        F23 = 134,
        F24 = 135,
        NUMLOCK = 144,
        SCROLL = 145,
        LSHIFT = 160,
        RSHIFT = 161,
        LCONTROL = 162,
        RCONTROL = 163,
        LMENU = 164,
        RMENU = 165,
        BROWSER_BACK = 166,
        BROWSER_FORWARD = 167,
        BROWSER_REFRESH = 168,
        BROWSER_STOP = 169,
        BROWSER_SEARCH = 170,
        BROWSER_FAVORITES = 171,
        BROWSER_HOME = 172,
        VOLUME_MUTE = 173,
        VOLUME_DOWN = 174,
        VOLUME_UP = 175,
        MEDIA_NEXT_TRACK = 176,
        MEDIA_PREV_TRACK = 177,
        MEDIA_STOP = 178,
        MEDIA_PLAY_PAUSE = 179,
        LAUNCH_MAIL = 180,
        LAUNCH_MEDIA_SELECT = 181,
        LAUNCH_APP1 = 182,
        LAUNCH_APP2 = 183,
        OEM_1 = 186,
        OEM_PLUS = 187,
        OEM_COMMA = 188,
        OEM_MINUS = 189,
        OEM_PERIOD = 190,
        OEM_2 = 191,
        OEM_3 = 192,
        OEM_4 = 219,
        OEM_5 = 220,
        OEM_6 = 221,
        OEM_7 = 222,
        OEM_8 = 223,
        OEM_102 = 226,
        PROCESSKEY = 229,
        PACKET = 231,
        ATTN = 246,
        CRSEL = 247,
        EXSEL = 248,
        EREOF = 249,
        PLAY = 250,
        ZOOM = 251,
        NONAME = 252,
        PA1 = 253,
        OEM_CLEAR = 254
    }

    public const uint WS_OVERLAPPED = 0u;

    public const uint WS_POPUP = 2147483648u;

    public const uint WS_CHILD = 1073741824u;

    public const uint WS_MINIMIZE = 536870912u;

    public const uint WS_VISIBLE = 268435456u;

    public const uint WS_DISABLED = 134217728u;

    public const uint WS_CLIPSIBLINGS = 67108864u;

    public const uint WS_CLIPCHILDREN = 33554432u;

    public const uint WS_MAXIMIZE = 16777216u;

    public const uint WS_CAPTION = 12582912u;

    public const uint WS_BORDER = 8388608u;

    public const uint WS_DLGFRAME = 4194304u;

    public const uint WS_VSCROLL = 2097152u;

    public const uint WS_HSCROLL = 1048576u;

    public const uint WS_SYSMENU = 524288u;

    public const uint WS_THICKFRAME = 262144u;

    public const uint WS_GROUP = 131072u;

    public const uint WS_TABSTOP = 65536u;

    public const uint WS_MINIMIZEBOX = 131072u;

    public const uint WS_MAXIMIZEBOX = 65536u;

    public const uint WS_TILED = 0u;

    public const uint WS_ICONIC = 536870912u;

    public const uint WS_SIZEBOX = 262144u;

    public const uint WS_EX_DLGMODALFRAME = 1u;

    public const uint WS_EX_NOPARENTNOTIFY = 4u;

    public const uint WS_EX_TOPMOST = 8u;

    public const uint WS_EX_ACCEPTFILES = 16u;

    public const uint WS_EX_TRANSPARENT = 32u;

    public const uint WS_EX_MDICHILD = 64u;

    public const uint WS_EX_TOOLWINDOW = 128u;

    public const uint WS_EX_WINDOWEDGE = 256u;

    public const uint WS_EX_CLIENTEDGE = 512u;

    public const uint WS_EX_CONTEXTHELP = 1024u;

    public const uint WS_EX_RIGHT = 4096u;

    public const uint WS_EX_LEFT = 0u;

    public const uint WS_EX_RTLREADING = 8192u;

    public const uint WS_EX_LTRREADING = 0u;

    public const uint WS_EX_LEFTSCROLLBAR = 16384u;

    public const uint WS_EX_RIGHTSCROLLBAR = 0u;

    public const uint WS_EX_CONTROLPARENT = 65536u;

    public const uint WS_EX_STATICEDGE = 131072u;

    public const uint WS_EX_APPWINDOW = 262144u;

    public const uint WS_EX_OVERLAPPEDWINDOW = 768u;

    public const uint WS_EX_PALETTEWINDOW = 392u;

    public const uint WS_EX_LAYERED = 524288u;

    public const uint WS_EX_NOINHERITLAYOUT = 1048576u;

    public const uint WS_EX_LAYOUTRTL = 4194304u;

    public const uint WS_EX_COMPOSITED = 33554432u;

    public const uint WS_EX_NOACTIVATE = 134217728u;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, SetWindowPosFlags uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(HandleRef hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

}
