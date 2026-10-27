using System.Runtime.InteropServices;

namespace OniAdvisor.Companion;

/// <summary>Window focus and the Win+H shortcut, for in-game push-to-talk.</summary>
static class NativeFocus
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
    const ushort VK_LWIN = 0x5B, VK_H = 0x48, VK_MENU = 0x12;

    public static IntPtr Foreground() => GetForegroundWindow();

    /// <summary>Brings a window to the front and waits (up to ~1 s) until Windows has actually given it focus.</summary>
    public static async Task<bool> ActivateAsync(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, 9); // SW_RESTORE
            Keys(Key(VK_MENU, 0), Key(VK_MENU, KEYEVENTF_KEYUP)); // an Alt tap lets a background app take focus
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            await Task.Delay(200);
            if (GetForegroundWindow() == hWnd) return true;
        }
        return false;
    }

    public static void Minimize(IntPtr hWnd) => ShowWindow(hWnd, 7); // SW_SHOWMINNOACTIVE: minimize, focus stays where it is

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Visible on screen: shown and not minimized.</summary>
    public static bool OnScreen(IntPtr hWnd) => IsWindowVisible(hWnd) && !IsIconic(hWnd);

    /// <summary>Shows a window without taking keyboard focus (restoring a minimized one if needed).</summary>
    public static void ShowNoActivate(IntPtr hWnd) => ShowWindow(hWnd, IsIconic(hWnd) ? 4 : 8); // SW_SHOWNOACTIVATE / SW_SHOWNA

    /// <summary>Hides a window completely (no taskbar entry); focus stays where it is.</summary>
    public static void Hide(IntPtr hWnd) => ShowWindow(hWnd, 0); // SW_HIDE

    /// <summary>Presses Win+H, which opens Windows voice typing for the focused text box.</summary>
    public static void StartVoiceTyping() =>
        Keys(Key(VK_LWIN, KEYEVENTF_EXTENDEDKEY), Key(VK_H, 0), Key(VK_H, KEYEVENTF_KEYUP),
             Key(VK_LWIN, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP));

    static INPUT Key(ushort vk, uint flags) => new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };

    static void Keys(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
