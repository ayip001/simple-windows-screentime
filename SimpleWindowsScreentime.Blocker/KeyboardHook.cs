using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleWindowsScreentime.Blocker;

public class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    // Virtual key codes
    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_F4 = 0x73;
    private const int VK_F11 = 0x7A;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;  // Left Alt
    private const int VK_RMENU = 0xA5;  // Right Alt

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        _hookId = SetHook(_proc);
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;

        if (curModule == null)
            return IntPtr.Zero;

        return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            var flags = Marshal.ReadInt32(lParam + 8);

            bool altPressed = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 ||
                              (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;
            bool ctrlPressed = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                               (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
            bool shiftPressed = (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 ||
                                (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;

            // Block Windows key
            if (vkCode == VK_LWIN || vkCode == VK_RWIN)
            {
                return (IntPtr)1;
            }

            // Block Alt+Tab and Alt+Shift+Tab
            if (altPressed && vkCode == VK_TAB)
            {
                return (IntPtr)1;
            }

            // Block Alt+F4
            if (altPressed && vkCode == VK_F4)
            {
                return (IntPtr)1;
            }

            // Block Alt+Space (system menu)
            if (altPressed && vkCode == VK_SPACE)
            {
                return (IntPtr)1;
            }

            // Block Ctrl+Shift+Esc (Task Manager)
            if (ctrlPressed && shiftPressed && vkCode == VK_ESCAPE)
            {
                return (IntPtr)1;
            }

            // Block Ctrl+Alt+Delete is handled by system, can't intercept

            // Block F11 (fullscreen toggle in some apps)
            if (vkCode == VK_F11)
            {
                return (IntPtr)1;
            }

            // Block Alt+Escape
            if (altPressed && vkCode == VK_ESCAPE)
            {
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
