using System.IO;
using System.Runtime.InteropServices;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     游戏窗口形态管理：让游戏以「窗口化 / 最大化」运行，而不是真全屏。<br/>
///     <b>为什么这么做</b>：<br/>
///     - 游戏真全屏时走「独立翻转（Independent Flip）」，DWM 被绕过；<br/>
///     - 覆盖层是分层窗口，必须由 DWM 合成才能显示 —— 因此全屏时它没有绘制路径；<br/>
///     - 实测结论：按 Win 键画面会「黑一下」再显示开始菜单，那正是系统退出独立翻转的过程；<br/>
///     - 而<b>最大化的窗口化</b>带标题栏与边框，不满足全屏优化的条件，DWM 正常合成，
///       覆盖层始终可用 —— 这是唯一「效果确定 + 风险低」的路径。<br/>
///     代价只是多一条细标题栏。本模块不做任何窗口样式改写（那种做法曾把玩家窗口弄坏）。
/// </summary>
internal static class ModGameFullscreen
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

        public override readonly string ToString() => $"{Width}x{Height}@{Left},{Top}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    private const int GwlStyle = -16;

    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;

    /// <summary>SW_MAXIMIZE：把窗口最大化（标准 API，只改窗口状态，不动样式）。</summary>
    private const int SwMaximize = 3;

    private const uint MonitorDefaultToNearest = 2;

    #endregion

    private static System.Windows.Threading.DispatcherTimer? _timer;
    private static int _lastStyle = int.MinValue;
    private static int _lastExStyle = int.MinValue;

    /// <summary>是否已经替玩家最大化过一次。只做一次，避免与玩家手动还原的操作对抗。</summary>
    private static bool _maximizedOnce;

    #region 启动前：关掉游戏的 fullscreen

    /// <summary>
    ///     确保游戏的 options.txt 中 fullscreen 为 false，使游戏以窗口化启动。<br/>
    ///     与「失焦自动暂停」的处理方式一致：只改这一个键，其余设置原样保留，
    ///     游戏退出时会把完整设置写回，不会破坏玩家配置。
    /// </summary>
    public static void EnsureWindowedMode(McInstance? instance)
    {
        if (instance is null) return;
        if (!Config.Tool.PreferWindowedForOverlay) return;

        try
        {
            var path = Path.Combine(instance.PathIndie, "options.txt");
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path);
            var changed = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("fullscreen:", StringComparison.OrdinalIgnoreCase)) continue;
                if (lines[i].TrimEnd().EndsWith("false", StringComparison.OrdinalIgnoreCase)) break;

                lines[i] = "fullscreen:false";
                changed = true;
                break;
            }

            if (!changed) return;

            File.WriteAllLines(path, lines);
            ModBase.Log($"[Fullscreen] 已把游戏设为窗口化启动（覆盖层需要）：{path}");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Fullscreen] 设置窗口化启动失败", ModBase.LogLevel.Developer);
        }
    }

    #endregion

    #region 运行中：判断真全屏 / 自动最大化

    /// <summary>
    ///     游戏是否处于「真全屏」—— 覆盖整个显示器<b>且没有边框</b>。<br/>
    ///     分支说明：单看「是否覆盖显示器」无法区分全屏与最大化（两者都铺满屏幕），
    ///     关键是<b>边框</b>：全屏窗口没有 WS_CAPTION / WS_THICKFRAME，
    ///     最大化窗口有。实测数据也印证：全屏 style=0x17000000（无边框），
    ///     最大化 style=0x17CF0000（有边框）。
    /// </summary>
    public static bool IsExclusiveFullscreen()
    {
        try
        {
            var hwnd = ModGameOverlay.PeekGameWindow();
            if (hwnd == IntPtr.Zero) return false;

            var style = GetWindowLong(hwnd, GwlStyle);
            var hasFrame = (style & (WsCaption | WsThickFrame)) != 0;
            if (hasFrame) return false;

            return CoversMonitor(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开始监视游戏窗口形态。</summary>
    public static void Start()
    {
        if (_timer is not null) return;

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>玩家换了实例或重新启动游戏时重置「已最大化」标记。</summary>
    public static void ResetMaximizedFlag() => _maximizedOnce = false;

    private static void Tick()
    {
        try
        {
            var hwnd = ModGameOverlay.PeekGameWindow();
            if (hwnd == IntPtr.Zero) return;

            var style = GetWindowLong(hwnd, GwlStyle);
            var exStyle = GetWindowLong(hwnd, 0 - 20);

            // 样式变化时留一条诊断日志，便于回溯窗口形态
            if (style != _lastStyle || exStyle != _lastExStyle)
            {
                _lastStyle = style;
                _lastExStyle = exStyle;
                var hasFrame = (style & (WsCaption | WsThickFrame)) != 0;
                var boundsText = GetWindowRect(hwnd, out var windowBounds) ? windowBounds.ToString() : "?";
                var coversMonitor = CoversMonitor(hwnd);
                ModBase.Log($"[Fullscreen] 窗口形态：style=0x{style:X8}, 有边框={hasFrame}, " +
                            $"窗口={boundsText}, 覆盖整屏={coversMonitor}, 全屏={(!hasFrame && coversMonitor)}",
                    ModBase.LogLevel.Debug);
            }

            // 自动最大化是**可选**的，默认不做 ——
            // 窗口形态该由玩家决定；启动器只负责在玩家按住全屏时给出说明。
            if (!Config.Tool.MaximizeGameOnLaunch) return;
            if (_maximizedOnce) return;

            var hasFrameNow = (style & (WsCaption | WsThickFrame)) != 0;
            if (!hasFrameNow) return;   // 正在真全屏，交给玩家自己按 F11
            if (IsZoomed(hwnd)) { _maximizedOnce = true; return; }   // 已经是最大化

            ShowWindow(hwnd, SwMaximize);
            _maximizedOnce = true;
            ModBase.Log("[Fullscreen] 已把游戏窗口最大化（代替全屏，使覆盖层可用）");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Fullscreen] 调整窗口形态失败", ModBase.LogLevel.Developer);
        }
    }

    /// <summary>窗口是否覆盖了它所在的整个显示器。</summary>
    private static bool CoversMonitor(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        if (!GetWindowRect(hwnd, out var window)) return false;

        // 精确比较，不留容差。<br/>
        // 分支说明：本套机制**故意**把窗口做成「比显示器小 1 像素」来避开独立翻转，
        // 而这里原先留了 2 像素容差，于是把自己的规避动作又判成了「覆盖整屏」，
        // 导致 IsExclusiveFullscreen 误报、把覆盖层挡在门外。
        const int tolerance = 0;
        return window.Left <= info.Monitor.Left + tolerance &&
               window.Top <= info.Monitor.Top + tolerance &&
               window.Right >= info.Monitor.Right - tolerance &&
               window.Bottom >= info.Monitor.Bottom - tolerance;
    }

    #endregion
}
