using System;
using System.Runtime.InteropServices;

namespace Ribbit.Windows;

public static class DirectInputSendKey
{
#pragma warning disable CS0649
    private struct MOUSEINPUT
    {
        public int dx;

        public int dy;

        public int mouseData;

        public int dwFlags;

        public int time;

        public IntPtr dwExtraInfo;
    }

    private struct KEYBDINPUT
    {
        public short wVk;

        public short wScan;

        public int dwFlags;

        public int time;

        public IntPtr dwExtraInfo;
    }

    private struct HARDWAREINPUT
    {
        public int uMsg;

        public short wParamL;

        public short wParamH;
    }

    private struct INPUT
    {
        public int type;

        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }
#pragma warning restore CS0649

    [Flags]
    public enum KEYEVENTF
    {
        KEYEVENTF_KEYDOWN = 0,
        KEYEVENTF_EXTENDEDKEY = 1,
        KEYEVENTF_KEYUP = 2,
        KEYEVENTF_UNICODE = 4,
        KEYEVENTF_SCANCODE = 8
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray, SizeConst = 1)] INPUT[] pInputs, int cbSize);

    public static void SendKey(short DIK, KEYEVENTF KeyUporDown, short VK = 0)
    {
        INPUT[] array = new INPUT[1];
        array[0].type = 1;
        array[0].u.ki.wVk = (short)(((KeyUporDown & KEYEVENTF.KEYEVENTF_UNICODE) != KEYEVENTF.KEYEVENTF_UNICODE) ? VK : 0);
        array[0].u.ki.wScan = DIK;
        array[0].u.ki.dwFlags = (int)KeyUporDown;
        array[0].u.ki.time = 0;
        array[0].u.ki.dwExtraInfo = IntPtr.Zero;
        SendInput(1u, array, Marshal.SizeOf(typeof(INPUT)));
    }
}
