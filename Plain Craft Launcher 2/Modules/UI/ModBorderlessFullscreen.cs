using System.Runtime.InteropServices;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     由启动器实现的「无边框全屏」，并接管 F11 以阻止游戏进入自带全屏。<br/>
///     <b>为什么这么做</b>：<br/>
///     - 游戏自带的全屏是「无边框 + 覆盖整个显示器」，会触发 Windows 的
///       独立翻转（Independent Flip）而绕过 DWM，游戏内覆盖层因此没有绘制路径
///       （实测：全屏时按 Win 键画面会「黑一下」，那正是系统退出独立翻转的过程）；<br/>
///     - 本模块把游戏窗口改成「无边框 + 只覆盖工作区」，并<b>刻意比显示器小 1 像素</b>，
///       确保不满足「覆盖整个显示器」这一条件，于是不触发独立翻转 ——
///       DWM 正常合成，覆盖层可正常叠加。<br/>
///     <b>安全性</b>：全程不注入、不 hook，只调用标准的窗口样式/位置 API；
///     原样式与原位置会被完整保存，再按一次 F11 即可还原。
/// </summary>
internal static class ModBorderlessFullscreen
{
    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;

    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsPopup = unchecked((int)0x80000000);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private const uint MonitorDefaultToNearest = 2;

    #endregion

    /// <summary>
    ///     上次切换时间，用于过滤键盘自动重复。<br/>
    ///     分支说明：按住 F11 不放时系统会连续派发 WM_HOTKEY（实测 1 秒内可达十余次），
    ///     窗口会在两种形态之间高频闪烁；加入防抖后每次按键只生效一次。
    /// </summary>
    private static DateTime _lastToggle = DateTime.MinValue;

    private const int ToggleDebounceMs = 300;

    private static bool _active;
    private static int _savedStyle;
    private static int _savedExStyle;
    private static Rect _savedRect;

    /// <summary>当前是否处于「启动器实现的无边框全屏」。</summary>
    public static bool IsActive => _active;

    /// <summary>游戏重新启动时清掉状态（窗口已销毁，旧状态无意义）。</summary>
    public static void Reset()
    {
        _active = false;
        _lastToggle = DateTime.MinValue;
    }

    /// <summary>F11 回调：在「无边框全屏」与「原窗口状态」之间切换。</summary>
    public static void Toggle()
    {
        if (!Config.Tool.F11BorderlessFullscreen) return;

        var now = DateTime.Now;
        if ((now - _lastToggle).TotalMilliseconds < ToggleDebounceMs) return;
        _lastToggle = now;

        var hwnd = ModGameOverlay.GetGameWindow();
        if (hwnd == IntPtr.Zero)
        {
            HintService.Hint(Lang.Text("Main.Overlay.NoGame"));
            return;
        }

        if (_active) ExitFullscreen(hwnd);
        else EnterFullscreen(hwnd);
    }

    private static void EnterFullscreen(IntPtr hwnd)
    {
        try
        {
            // 保存原状，供还原用
            _savedStyle = GetWindowLong(hwnd, GwlStyle);
            _savedExStyle = GetWindowLong(hwnd, GwlExstyle);
            if (!GetWindowRect(hwnd, out _savedRect)) return;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return;

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            // 目标：覆盖工作区（不含任务栏）
            var work = info.Work;
            var width = work.Width;
            var height = work.Height;

            // 关键：确保**不覆盖整个显示器**。
            // 若任务栏是自动隐藏的，rcWork 会等于整个屏幕 —— 那就必须主动缩小 1 像素，
            // 否则「无边框 + 覆盖整屏」又会触发独立翻转，覆盖层照样看不见。
            var monitorWidth = info.Monitor.Width;
            var monitorHeight = info.Monitor.Height;
            if (width >= monitorWidth) width = monitorWidth - 1;
            if (height >= monitorHeight) height = monitorHeight - 1;

            // 去掉窗口框架，改成弹出式无边框
            var newStyle = (_savedStyle
                            & ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox))
                           | WsPopup;
            SetWindowLong(hwnd, GwlStyle, newStyle);

            SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, width, height,
                SwpFrameChanged | SwpNoZorder | SwpNoActivate | SwpShowWindow);

            _active = true;
            ModBase.Log($"[Borderless] 已切换为无边框全屏：{width}x{height}@{work.Left},{work.Top}，" +
                        $"显示器 {monitorWidth}x{monitorHeight}（刻意不完全覆盖，以避开独立翻转）");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Borderless] 进入无边框全屏失败", ModBase.LogLevel.Hint);
        }
    }

    private static void ExitFullscreen(IntPtr hwnd)
    {
        try
        {
            SetWindowLong(hwnd, GwlStyle, _savedStyle);
            SetWindowLong(hwnd, GwlExstyle, _savedExStyle);

            SetWindowPos(hwnd, IntPtr.Zero,
                _savedRect.Left, _savedRect.Top, _savedRect.Width, _savedRect.Height,
                SwpFrameChanged | SwpNoZorder | SwpNoActivate | SwpShowWindow);

            _active = false;
            ModBase.Log("[Borderless] 已还原为切换前的窗口状态");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Borderless] 还原窗口状态失败", ModBase.LogLevel.Hint);
        }
    }
}
