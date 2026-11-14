using System.Runtime.InteropServices;

namespace PCL;

/// <summary>
///     向游戏窗口发送模拟的鼠标与键盘输入（宏）。<br/>
///     分支说明：<br/>
///     - 用 SendInput 而不是 keybd_event / mouse_event —— 前者是官方推荐，
///       且能正确进入游戏的输入队列。<br/>
///     - 输入只会送达**前台窗口**，所以执行前必须先关掉覆盖层、把游戏切回前台，
///       否则按键会被面板自己吃掉。<br/>
///     - 文本输入用 KEYEVENTF_UNICODE 逐字符发送，这样中文等非 ASCII 字符也能正确输入。
/// </summary>
internal static partial class ModGameMacro
{
    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;

    #endregion

    #region 基础发送

    /// <summary>按下或松开一个虚拟键。</summary>
    private static void Key(ushort vk, bool up)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Vk = vk,
                    Scan = 0,
                    Flags = up ? KeyEventKeyUp : 0,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    /// <summary>完整敲一下某个键。</summary>
    public static async Task TapKeyAsync(ushort vk, int holdMs = 40)
    {
        Key(vk, false);
        await Task.Delay(holdMs).ConfigureAwait(false);
        Key(vk, true);
    }

    /// <summary>组合键：依次按下 modifiers，敲主键，再逆序松开。</summary>
    public static async Task TapComboAsync(ushort[] modifiers, ushort vk)
    {
        foreach (var m in modifiers) Key(m, false);
        await Task.Delay(30).ConfigureAwait(false);

        await TapKeyAsync(vk).ConfigureAwait(false);

        for (var i = modifiers.Length - 1; i >= 0; i--) Key(modifiers[i], true);
    }

    /// <summary>
    ///     发送一段文本。<br/>
    ///     用 Unicode 模式逐字符发送，中文也能正确输入。
    /// </summary>
    public static async Task TypeTextAsync(string text)
    {
        foreach (var ch in text)
        {
            var input = new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        Vk = 0,
                        Scan = ch,
                        Flags = KeyEventUnicode,
                        Time = 0,
                        ExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(1, [input], Marshal.SizeOf<Input>());

            var up = input;
            up.Data.Keyboard.Flags = KeyEventUnicode | KeyEventKeyUp;
            SendInput(1, [up], Marshal.SizeOf<Input>());

            await Task.Delay(12).ConfigureAwait(false);
        }
    }

    /// <summary>鼠标点击（在当前位置）。</summary>
    public static async Task ClickMouseAsync(MouseButton button = MouseButton.Left)
    {
        var (down, up) = button switch
        {
            MouseButton.Right => (MouseEventRightDown, MouseEventRightUp),
            MouseButton.Middle => (MouseEventMiddleDown, MouseEventMiddleUp),
            _ => (MouseEventLeftDown, MouseEventLeftUp)
        };

        SendMouse(down);
        await Task.Delay(40).ConfigureAwait(false);
        SendMouse(up);
    }

    /// <summary>连点若干次，用于挂机点怪之类的场景。</summary>
    public static async Task ClickRepeatedlyAsync(int times, int intervalMs, MouseButton button = MouseButton.Left)
    {
        for (var i = 0; i < times; i++)
        {
            await ClickMouseAsync(button).ConfigureAwait(false);
            await Task.Delay(intervalMs).ConfigureAwait(false);
        }
    }

    /// <summary>滚轮滚动。</summary>
    public static void ScrollWheel(int notches)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = 0,
                    Dy = 0,
                    MouseData = unchecked((uint)(notches * 120)),
                    Flags = MouseEventWheel,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendMouse(uint flags)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = 0,
                    Dy = 0,
                    MouseData = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    public enum MouseButton
    {
        Left,
        Right,
        Middle
    }

    #endregion

    /// <summary>
    ///     在游戏窗口上执行一段宏。<br/>
    ///     执行前会把游戏切回前台并等一小会儿 —— 覆盖层刚关闭时焦点切换还没完成，
    ///     立刻发按键会被丢掉。
    /// </summary>
    public static async Task RunOnGameAsync(Func<Task> body, int settleMs = 320)
    {
        try
        {
            var handle = ModGameOverlay.GetGameWindow();
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);

            await Task.Delay(settleMs).ConfigureAwait(false);
            await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Macro] 执行宏失败", ModBase.LogLevel.Hint);
        }
    }

    #region 虚拟键常量

    // 只列用得到的，避免引一整个键表
    public const ushort VkT = 0x54;
    public const ushort VkE = 0x45;
    public const ushort VkQ = 0x51;
    public const ushort VkF = 0x46;
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkSpace = 0x20;
    public const ushort VkEnter = 0x0D;
    public const ushort VkEscape = 0x1B;
    public const ushort VkTab = 0x09;
    public const ushort VkF1 = 0x70;
    public const ushort VkF2 = 0x71;
    public const ushort VkF3 = 0x72;
    public const ushort VkF5 = 0x74;
    public const ushort VkF11 = 0x7A;

    /// <summary>数字键 1..9 的虚拟键码。</summary>
    public static ushort DigitKey(int digit) => (ushort)('0' + Math.Clamp(digit, 1, 9));

    #endregion
}
