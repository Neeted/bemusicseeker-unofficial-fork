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
            get
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
            get
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
            get
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
            get
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
            get
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
            get
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

        public bool Equals(RECT r)
        {
            if (r.Left == Left && r.Top == Top && r.Right == Right)
            {
                return r.Bottom == Bottom;
            }
            return false;
        }

        public override bool Equals(object obj)
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

        public override int GetHashCode()
        {
            return ((Rectangle)this/*cast due to .constrained prefix*/).GetHashCode();
        }

        public override string ToString()
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

    public struct INPUT
    {
        public uint type;

        public InputUnion U;

        public static int Size => Marshal.SizeOf(typeof(INPUT));
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    public struct MOUSEINPUT
    {
        public int dx;

        public int dy;

        public int mouseData;

        public MOUSEEVENTF dwFlags;

        public uint time;

        public UIntPtr dwExtraInfo;
    }

    public struct KEYBDINPUT
    {
        public VirtualKeyShort wVk;

        public ScanCodeShort wScan;

        public KEYEVENTF dwFlags;

        public int time;

        public UIntPtr dwExtraInfo;
    }

    public struct HARDWAREINPUT
    {
        public int uMsg;

        public short wParamL;

        public short wParamH;
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

    public enum WindowLongFlags
    {
        GWL_EXSTYLE = -20,
        GWLP_HINSTANCE = -6,
        GWLP_HWNDPARENT = -8,
        GWL_ID = -12,
        GWL_STYLE = -16,
        GWL_USERDATA = -21,
        GWL_WNDPROC = -4,
        DWLP_USER = 8,
        DWLP_MSGRESULT = 0,
        DWLP_DLGPROC = 4
    }

    [Flags]
    public enum MOUSEEVENTF : uint
    {
        ABSOLUTE = 0x8000u,
        HWHEEL = 0x1000u,
        MOVE = 1u,
        MOVE_NOCOALESCE = 0x2000u,
        LEFTDOWN = 2u,
        LEFTUP = 4u,
        RIGHTDOWN = 8u,
        RIGHTUP = 0x10u,
        MIDDLEDOWN = 0x20u,
        MIDDLEUP = 0x40u,
        VIRTUALDESK = 0x4000u,
        WHEEL = 0x800u,
        XDOWN = 0x80u,
        XUP = 0x100u
    }

    [Flags]
    public enum KEYEVENTF : uint
    {
        EXTENDEDKEY = 1u,
        KEYUP = 2u,
        SCANCODE = 8u,
        UNICODE = 4u
    }

    public enum VirtualKeyShort : short
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

    public enum ScanCodeShort : short
    {
        LBUTTON = 0,
        RBUTTON = 0,
        CANCEL = 70,
        MBUTTON = 0,
        XBUTTON1 = 0,
        XBUTTON2 = 0,
        BACK = 14,
        TAB = 15,
        CLEAR = 76,
        RETURN = 28,
        SHIFT = 42,
        CONTROL = 29,
        MENU = 56,
        PAUSE = 0,
        CAPITAL = 58,
        KANA = 0,
        HANGUL = 0,
        JUNJA = 0,
        FINAL = 0,
        HANJA = 0,
        KANJI = 0,
        ESCAPE = 1,
        CONVERT = 0,
        NONCONVERT = 0,
        ACCEPT = 0,
        MODECHANGE = 0,
        SPACE = 57,
        PRIOR = 73,
        NEXT = 81,
        END = 79,
        HOME = 71,
        LEFT = 75,
        UP = 72,
        RIGHT = 77,
        DOWN = 80,
        SELECT = 0,
        PRINT = 0,
        EXECUTE = 0,
        SNAPSHOT = 84,
        INSERT = 82,
        DELETE = 83,
        HELP = 99,
        KEY_0 = 11,
        KEY_1 = 2,
        KEY_2 = 3,
        KEY_3 = 4,
        KEY_4 = 5,
        KEY_5 = 6,
        KEY_6 = 7,
        KEY_7 = 8,
        KEY_8 = 9,
        KEY_9 = 10,
        KEY_A = 30,
        KEY_B = 48,
        KEY_C = 46,
        KEY_D = 32,
        KEY_E = 18,
        KEY_F = 33,
        KEY_G = 34,
        KEY_H = 35,
        KEY_I = 23,
        KEY_J = 36,
        KEY_K = 37,
        KEY_L = 38,
        KEY_M = 50,
        KEY_N = 49,
        KEY_O = 24,
        KEY_P = 25,
        KEY_Q = 16,
        KEY_R = 19,
        KEY_S = 31,
        KEY_T = 20,
        KEY_U = 22,
        KEY_V = 47,
        KEY_W = 17,
        KEY_X = 45,
        KEY_Y = 21,
        KEY_Z = 44,
        LWIN = 91,
        RWIN = 92,
        APPS = 93,
        SLEEP = 95,
        NUMPAD0 = 82,
        NUMPAD1 = 79,
        NUMPAD2 = 80,
        NUMPAD3 = 81,
        NUMPAD4 = 75,
        NUMPAD5 = 76,
        NUMPAD6 = 77,
        NUMPAD7 = 71,
        NUMPAD8 = 72,
        NUMPAD9 = 73,
        MULTIPLY = 55,
        ADD = 78,
        SEPARATOR = 0,
        SUBTRACT = 74,
        DECIMAL = 83,
        DIVIDE = 53,
        F1 = 59,
        F2 = 60,
        F3 = 61,
        F4 = 62,
        F5 = 63,
        F6 = 64,
        F7 = 65,
        F8 = 66,
        F9 = 67,
        F10 = 68,
        F11 = 87,
        F12 = 88,
        F13 = 100,
        F14 = 101,
        F15 = 102,
        F16 = 103,
        F17 = 104,
        F18 = 105,
        F19 = 106,
        F20 = 107,
        F21 = 108,
        F22 = 109,
        F23 = 110,
        F24 = 118,
        NUMLOCK = 69,
        SCROLL = 70,
        LSHIFT = 42,
        RSHIFT = 54,
        LCONTROL = 29,
        RCONTROL = 29,
        LMENU = 56,
        RMENU = 56,
        BROWSER_BACK = 106,
        BROWSER_FORWARD = 105,
        BROWSER_REFRESH = 103,
        BROWSER_STOP = 104,
        BROWSER_SEARCH = 101,
        BROWSER_FAVORITES = 102,
        BROWSER_HOME = 50,
        VOLUME_MUTE = 32,
        VOLUME_DOWN = 46,
        VOLUME_UP = 48,
        MEDIA_NEXT_TRACK = 25,
        MEDIA_PREV_TRACK = 16,
        MEDIA_STOP = 36,
        MEDIA_PLAY_PAUSE = 34,
        LAUNCH_MAIL = 108,
        LAUNCH_MEDIA_SELECT = 109,
        LAUNCH_APP1 = 107,
        LAUNCH_APP2 = 33,
        OEM_1 = 39,
        OEM_PLUS = 13,
        OEM_COMMA = 51,
        OEM_MINUS = 12,
        OEM_PERIOD = 52,
        OEM_2 = 53,
        OEM_3 = 41,
        OEM_4 = 26,
        OEM_5 = 43,
        OEM_6 = 27,
        OEM_7 = 40,
        OEM_8 = 0,
        OEM_102 = 86,
        PROCESSKEY = 0,
        PACKET = 0,
        ATTN = 0,
        CRSEL = 0,
        EXSEL = 0,
        EREOF = 93,
        PLAY = 0,
        ZOOM = 98,
        NONAME = 0,
        PA1 = 0,
        OEM_CLEAR = 0
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

    public enum WM : uint
    {
        NULL = 0u,
        CREATE = 1u,
        DESTROY = 2u,
        MOVE = 3u,
        SIZE = 5u,
        ACTIVATE = 6u,
        SETFOCUS = 7u,
        KILLFOCUS = 8u,
        ENABLE = 10u,
        SETREDRAW = 11u,
        SETTEXT = 12u,
        GETTEXT = 13u,
        GETTEXTLENGTH = 14u,
        PAINT = 15u,
        CLOSE = 16u,
        QUERYENDSESSION = 17u,
        QUERYOPEN = 19u,
        ENDSESSION = 22u,
        QUIT = 18u,
        ERASEBKGND = 20u,
        SYSCOLORCHANGE = 21u,
        SHOWWINDOW = 24u,
        WININICHANGE = 26u,
        SETTINGCHANGE = 26u,
        DEVMODECHANGE = 27u,
        ACTIVATEAPP = 28u,
        FONTCHANGE = 29u,
        TIMECHANGE = 30u,
        CANCELMODE = 31u,
        SETCURSOR = 32u,
        MOUSEACTIVATE = 33u,
        CHILDACTIVATE = 34u,
        QUEUESYNC = 35u,
        GETMINMAXINFO = 36u,
        PAINTICON = 38u,
        ICONERASEBKGND = 39u,
        NEXTDLGCTL = 40u,
        SPOOLERSTATUS = 42u,
        DRAWITEM = 43u,
        MEASUREITEM = 44u,
        DELETEITEM = 45u,
        VKEYTOITEM = 46u,
        CHARTOITEM = 47u,
        SETFONT = 48u,
        GETFONT = 49u,
        SETHOTKEY = 50u,
        GETHOTKEY = 51u,
        QUERYDRAGICON = 55u,
        COMPAREITEM = 57u,
        GETOBJECT = 61u,
        COMPACTING = 65u,
        [Obsolete]
        COMMNOTIFY = 68u,
        WINDOWPOSCHANGING = 70u,
        WINDOWPOSCHANGED = 71u,
        [Obsolete]
        POWER = 72u,
        COPYDATA = 74u,
        CANCELJOURNAL = 75u,
        NOTIFY = 78u,
        INPUTLANGCHANGEREQUEST = 80u,
        INPUTLANGCHANGE = 81u,
        TCARD = 82u,
        HELP = 83u,
        USERCHANGED = 84u,
        NOTIFYFORMAT = 85u,
        CONTEXTMENU = 123u,
        STYLECHANGING = 124u,
        STYLECHANGED = 125u,
        DISPLAYCHANGE = 126u,
        GETICON = 127u,
        SETICON = 128u,
        NCCREATE = 129u,
        NCDESTROY = 130u,
        NCCALCSIZE = 131u,
        NCHITTEST = 132u,
        NCPAINT = 133u,
        NCACTIVATE = 134u,
        GETDLGCODE = 135u,
        SYNCPAINT = 136u,
        NCMOUSEMOVE = 160u,
        NCLBUTTONDOWN = 161u,
        NCLBUTTONUP = 162u,
        NCLBUTTONDBLCLK = 163u,
        NCRBUTTONDOWN = 164u,
        NCRBUTTONUP = 165u,
        NCRBUTTONDBLCLK = 166u,
        NCMBUTTONDOWN = 167u,
        NCMBUTTONUP = 168u,
        NCMBUTTONDBLCLK = 169u,
        NCXBUTTONDOWN = 171u,
        NCXBUTTONUP = 172u,
        NCXBUTTONDBLCLK = 173u,
        INPUT_DEVICE_CHANGE = 254u,
        INPUT = 255u,
        KEYFIRST = 256u,
        KEYDOWN = 256u,
        KEYUP = 257u,
        CHAR = 258u,
        DEADCHAR = 259u,
        SYSKEYDOWN = 260u,
        SYSKEYUP = 261u,
        SYSCHAR = 262u,
        SYSDEADCHAR = 263u,
        UNICHAR = 265u,
        KEYLAST = 265u,
        IME_STARTCOMPOSITION = 269u,
        IME_ENDCOMPOSITION = 270u,
        IME_COMPOSITION = 271u,
        IME_KEYLAST = 271u,
        INITDIALOG = 272u,
        COMMAND = 273u,
        SYSCOMMAND = 274u,
        TIMER = 275u,
        HSCROLL = 276u,
        VSCROLL = 277u,
        INITMENU = 278u,
        INITMENUPOPUP = 279u,
        MENUSELECT = 287u,
        MENUCHAR = 288u,
        ENTERIDLE = 289u,
        MENURBUTTONUP = 290u,
        MENUDRAG = 291u,
        MENUGETOBJECT = 292u,
        UNINITMENUPOPUP = 293u,
        MENUCOMMAND = 294u,
        CHANGEUISTATE = 295u,
        UPDATEUISTATE = 296u,
        QUERYUISTATE = 297u,
        CTLCOLORMSGBOX = 306u,
        CTLCOLOREDIT = 307u,
        CTLCOLORLISTBOX = 308u,
        CTLCOLORBTN = 309u,
        CTLCOLORDLG = 310u,
        CTLCOLORSCROLLBAR = 311u,
        CTLCOLORSTATIC = 312u,
        MOUSEFIRST = 512u,
        MOUSEMOVE = 512u,
        LBUTTONDOWN = 513u,
        LBUTTONUP = 514u,
        LBUTTONDBLCLK = 515u,
        RBUTTONDOWN = 516u,
        RBUTTONUP = 517u,
        RBUTTONDBLCLK = 518u,
        MBUTTONDOWN = 519u,
        MBUTTONUP = 520u,
        MBUTTONDBLCLK = 521u,
        MOUSEWHEEL = 522u,
        XBUTTONDOWN = 523u,
        XBUTTONUP = 524u,
        XBUTTONDBLCLK = 525u,
        MOUSEHWHEEL = 526u,
        MOUSELAST = 526u,
        PARENTNOTIFY = 528u,
        ENTERMENULOOP = 529u,
        EXITMENULOOP = 530u,
        NEXTMENU = 531u,
        SIZING = 532u,
        CAPTURECHANGED = 533u,
        MOVING = 534u,
        POWERBROADCAST = 536u,
        DEVICECHANGE = 537u,
        MDICREATE = 544u,
        MDIDESTROY = 545u,
        MDIACTIVATE = 546u,
        MDIRESTORE = 547u,
        MDINEXT = 548u,
        MDIMAXIMIZE = 549u,
        MDITILE = 550u,
        MDICASCADE = 551u,
        MDIICONARRANGE = 552u,
        MDIGETACTIVE = 553u,
        MDISETMENU = 560u,
        ENTERSIZEMOVE = 561u,
        EXITSIZEMOVE = 562u,
        DROPFILES = 563u,
        MDIREFRESHMENU = 564u,
        IME_SETCONTEXT = 641u,
        IME_NOTIFY = 642u,
        IME_CONTROL = 643u,
        IME_COMPOSITIONFULL = 644u,
        IME_SELECT = 645u,
        IME_CHAR = 646u,
        IME_REQUEST = 648u,
        IME_KEYDOWN = 656u,
        IME_KEYUP = 657u,
        MOUSEHOVER = 673u,
        MOUSELEAVE = 675u,
        NCMOUSEHOVER = 672u,
        NCMOUSELEAVE = 674u,
        WTSSESSION_CHANGE = 689u,
        TABLET_FIRST = 704u,
        TABLET_LAST = 735u,
        CUT = 768u,
        COPY = 769u,
        PASTE = 770u,
        CLEAR = 771u,
        UNDO = 772u,
        RENDERFORMAT = 773u,
        RENDERALLFORMATS = 774u,
        DESTROYCLIPBOARD = 775u,
        DRAWCLIPBOARD = 776u,
        PAINTCLIPBOARD = 777u,
        VSCROLLCLIPBOARD = 778u,
        SIZECLIPBOARD = 779u,
        ASKCBFORMATNAME = 780u,
        CHANGECBCHAIN = 781u,
        HSCROLLCLIPBOARD = 782u,
        QUERYNEWPALETTE = 783u,
        PALETTEISCHANGING = 784u,
        PALETTECHANGED = 785u,
        HOTKEY = 786u,
        PRINT = 791u,
        PRINTCLIENT = 792u,
        APPCOMMAND = 793u,
        THEMECHANGED = 794u,
        CLIPBOARDUPDATE = 797u,
        DWMCOMPOSITIONCHANGED = 798u,
        DWMNCRENDERINGCHANGED = 799u,
        DWMCOLORIZATIONCOLORCHANGED = 800u,
        DWMWINDOWMAXIMIZEDCHANGE = 801u,
        GETTITLEBARINFOEX = 831u,
        HANDHELDFIRST = 856u,
        HANDHELDLAST = 863u,
        AFXFIRST = 864u,
        AFXLAST = 895u,
        PENWINFIRST = 896u,
        PENWINLAST = 911u,
        APP = 32768u,
        USER = 1024u,
        CPL_LAUNCH = 5120u,
        CPL_LAUNCHED = 5121u,
        SYSTIMER = 280u,
        HSHELL_ACCESSIBILITYSTATE = 11u,
        HSHELL_ACTIVATESHELLWINDOW = 3u,
        HSHELL_APPCOMMAND = 12u,
        HSHELL_GETMINRECT = 5u,
        HSHELL_LANGUAGE = 8u,
        HSHELL_REDRAW = 6u,
        HSHELL_TASKMAN = 7u,
        HSHELL_WINDOWCREATED = 1u,
        HSHELL_WINDOWDESTROYED = 2u,
        HSHELL_WINDOWACTIVATED = 4u,
        HSHELL_WINDOWREPLACED = 13u
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

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

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
    public static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint nInputs, [In][MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);
}
