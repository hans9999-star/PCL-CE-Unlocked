using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCL;

/// <summary>
///     游戏内叠加窗口的宿主逻辑。<br/>
///     分支说明：这里走的是「外部置顶窗口」方案 —— 不往游戏进程注入任何代码，
///     而是创建一个无边框置顶窗口贴在游戏窗口的客户区上。<br/>
///     限制：独占全屏（DirectX exclusive fullscreen）下无法显示，
///     因为那种模式下桌面合成被绕过。窗口化与「无边框窗口化」都正常。
/// </summary>
internal static class ModGameOverlay
{
    #region Win32

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>SW_RESTORE：还原被最小化/最大化的窗口。</summary>
    private const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    #endregion

    private static Window? _window;
    private static GameOverlayPanel? _panel;
    private static System.Windows.Threading.DispatcherTimer? _tracker;

    /// <summary>叠加窗口是否已创建。</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>
    ///     只读地取游戏窗口句柄，<b>不触发识别接管</b>。<br/>
    ///     分支说明：供诊断类模块使用 —— 之前诊断模块调用 GetGameWindow()，
    ///     而那个方法在进程缺失时会触发 WMI 识别流程，既浪费又消耗节流额度。
    /// </summary>
    public static IntPtr PeekGameWindow()
    {
        try
        {
            var process = ModLaunch.mcLaunchProcess;
            if (SafeHasExited(process)) return IntPtr.Zero;

            var handle = FindGameWindow(process!.Id);
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            if (!IsWindowVisible(handle) || IsIconic(handle)) return IntPtr.Zero;
            return handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>游戏窗口的可用状态。</summary>
    public enum GameWindowState
    {
        /// <summary>游戏没在跑，或窗口句柄拿不到。</summary>
        NotRunning,

        /// <summary>游戏在跑，但窗口被最小化了 —— 覆盖层没法贴上去。</summary>
        Minimized,

        /// <summary>游戏在跑且窗口正常显示，可以叠加。</summary>
        Ready
    }

    /// <summary>
    ///     判断游戏窗口当前处于哪种状态。<br/>
    ///     分支说明：原先只有「能取到句柄」与「取不到」两种结果，
    ///     于是「游戏被最小化」也会被报成「游戏没有在运行」，误导用户。
    ///     拆成三种状态后，调用方才能给出准确提示或先把窗口还原回来。
    /// </summary>
    public static GameWindowState GetGameWindowState(bool force = false)
    {
        try
        {
            var process = ModLaunch.mcLaunchProcess;

            if (SafeHasExited(process))
            {
                ModBase.Log($"[Overlay] 状态判定：进程对象{(process is null ? "为 null" : "已退出")}，开始识别",
                    ModBase.LogLevel.Debug);

                // 节流是为了避免频繁的 WMI 查询；但用户主动按热键时值得强制查一次
                ModGameAdopt.TryAdopt(ModInstanceList.McMcInstanceSelected, force);
                process = ModLaunch.mcLaunchProcess;
            }

            if (SafeHasExited(process))
            {
                ModBase.Log("[Overlay] 状态判定：识别后仍无可用进程 → 未运行", ModBase.LogLevel.Debug);
                return GameWindowState.NotRunning;
            }

            var handle = FindGameWindow(process!.Id);

            if (handle == IntPtr.Zero)
            {
                ModBase.Log("[Overlay] 状态判定：实时查找未找到游戏窗口 → 未运行", ModBase.LogLevel.Debug);
                return GameWindowState.NotRunning;
            }

            if (!IsWindowVisible(handle))
            {
                ModBase.Log($"[Overlay] 状态判定：句柄 {handle} 不可见 → 未运行", ModBase.LogLevel.Debug);
                return GameWindowState.NotRunning;
            }

            var icon = IsIconic(handle);
            ModBase.Log($"[Overlay] 状态判定：句柄 {handle}，最小化={icon} → {(icon ? "已最小化" : "就绪")}",
                ModBase.LogLevel.Debug);
            return icon ? GameWindowState.Minimized : GameWindowState.Ready;
        }
        catch (Exception ex)
        {
            // 关键：以前这里只记 Developer 级别，异常被淹没了
            ModBase.Log(ex, "[Overlay] 判断游戏窗口状态异常，按未运行处理", ModBase.LogLevel.Hint);
            return GameWindowState.NotRunning;
        }
    }

    /// <summary>安全地判断进程是否已退出（进程对象失效时不抛异常）。</summary>
    private static bool SafeHasExited(Process? process)
    {
        try
        {
            return process is null || process.HasExited;
        }
        catch
        {
            // HasExited 可能因进程对象已被释放而抛异常，此时视为已退出
            return true;
        }
    }

    /// <summary>还原被最小化的游戏窗口并让它回到前台。</summary>
    public static bool RestoreGameWindow()
    {
        try
        {
            var process = ModLaunch.mcLaunchProcess;
            if (SafeHasExited(process)) return false;

            // 同样要实时查找：缓存的句柄可能已经失效
            var handle = FindGameWindow(process!.Id);
            if (handle == IntPtr.Zero) return false;

            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 还原游戏窗口失败", ModBase.LogLevel.Developer);
            return false;
        }
    }

    /// <summary>
    ///     实时查找游戏窗口句柄。<br/>
    ///     分支说明（踩过的坑）：<b>不能</b>用 <c>Process.MainWindowHandle</c> ——
    ///     它会缓存首次取到的句柄（内部只在为 0 时查找一次）。而 Minecraft 的 LWJGL
    ///     窗口在启动过程中会被重建，旧句柄随即失效。缓存住死句柄后
    ///     <c>IsWindow</c> 永远返回 false，表现就是「游戏明明在跑，却说没在运行」。<br/>
    ///     所以这里自己用 EnumWindows 实时枚举，并且每次都校验缓存句柄是否仍属于该进程。
    /// </summary>
    private static IntPtr FindGameWindow(int processId)
    {
        // 缓存句柄仍有效（同一进程且窗口还在）就直接复用，避免频繁枚举
        if (_cachedGameHandle != IntPtr.Zero &&
            GetWindowThreadProcessId(_cachedGameHandle, out var cachedOwner) != 0 &&
            cachedOwner == processId &&
            IsWindow(_cachedGameHandle))
            return _cachedGameHandle;

        var found = IntPtr.Zero;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (GetWindowThreadProcessId(hwnd, out var owner) == 0) return true;
                if (owner != processId) return true;
                if (!IsWindowVisible(hwnd)) return true;
                // 有属主的是辅助窗口（工具条、提示框之类），不是游戏主窗口
                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;

                var title = new System.Text.StringBuilder(512);
                if (GetWindowText(hwnd, title, title.Capacity) <= 0) return true;

                found = hwnd;
                return false; // 找到即停止枚举
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 枚举游戏窗口失败", ModBase.LogLevel.Developer);
        }

        _cachedGameHandle = found;
        return found;
    }

    /// <summary>按当前状态取游戏窗口句柄；游戏未运行或已最小化时返回 <see cref="IntPtr.Zero" />。</summary>
    public static IntPtr GetGameWindow()
    {
        try
        {
            var process = ModLaunch.mcLaunchProcess;

            // 分支说明：进程可能是本启动器「接管」来的，也可能因为启动器中途重启而丢失。
            // 这里在缺失时补一次识别（内部带 5 秒节流，不会拖慢热键响应）。
            // 用 SafeHasExited：进程对象失效时 HasExited 会抛异常，
            // 若在此处抛出就会跳到 catch 直接返回 Zero，连重新识别的机会都没有
            if (SafeHasExited(process))
            {
                ModGameAdopt.TryAdopt(ModInstanceList.McMcInstanceSelected);
                process = ModLaunch.mcLaunchProcess;
            }

            if (SafeHasExited(process)) return IntPtr.Zero;

            // 实时查找，不用会缓存的 Process.MainWindowHandle
            var handle = FindGameWindow(process.Id);
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            if (!IsWindowVisible(handle) || IsIconic(handle)) return IntPtr.Zero;
            return handle;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 获取游戏窗口失败", ModBase.LogLevel.Developer);
            return IntPtr.Zero;
        }
    }

    /// <summary>游戏窗口当前是否在前台。</summary>
    public static bool IsGameForeground()
    {
        var handle = GetGameWindow();
        if (handle == IntPtr.Zero) return false;
        return GetForegroundWindow() == handle;
    }

    /// <summary>叠加窗口自己是否是前台窗口（用户正在面板里输入时）。</summary>
    public static bool IsOverlayForeground()
    {
        if (_window is null) return false;
        try
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero) return false;
            return GetForegroundWindow() == handle;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     打开叠加窗口。内容由 <paramref name="buildContent" /> 提供，
    ///     便于以后把 Wiki、控制台、聊天等不同面板塞进来。
    /// </summary>
    public static void Open(Func<FrameworkElement> buildContent)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var handle = GetGameWindow();
        if (handle == IntPtr.Zero)
        {
            HintService.Hint("游戏未运行或已最小化，无法打开叠加窗口", HintType.Error);
            return;
        }

        try
        {
            var content = buildContent();
            _panel = content as GameOverlayPanel;

            // 分支说明：必须保持分层窗口（AllowsTransparency = true）。
            // 曾经为了在游戏全屏（独立翻转）时可见而改成非分层窗口，结果闯了大祸：
            // 非分层窗口会促使系统退出独立翻转，而游戏随后又恢复独立翻转，
            // 两者来回拉锯 —— 表现为「画面在游戏与覆盖层之间不停闪，几秒后游戏被最小化」。
            // 分层窗口是稳定的，代价只是游戏走独立翻转时覆盖层不可见（已知限制）。
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                // 分支说明：不要设 Owner。Owner 会把叠加窗口绑到启动器的窗口层级上，
                // 导致它跟着启动器一起显示 —— 明明游戏没开，面板却叠在启动器界面上。
                // 层级由 ApplyOverlayStyles 里的 WndProc 自行控制。
                Content = content
            };

            // 这里改用 WndProc 自己保证置顶层级，避免被游戏盖住。
            _window.SourceInitialized += (_, _) => ApplyOverlayStyles(_window);

            _window.Closed += (_, _) =>
            {
                _tracker?.Stop();
                _tracker = null;
                _window = null;
                _panel = null;
                ModBase.Log("[Overlay] 叠加窗口已关闭");
            };

            _window.Show();

            // 播放入场动画：全屏变暗 + 面板浮现
            _panel?.PlayIntroIfNeeded();
            // 分支说明：必须主动激活窗口，否则即使去掉了 NOACTIVATE，
            // 窗口也可能因为「游戏始终在前台」而拿不到键盘焦点，输入框照样打不出字。
            // 主动激活：游戏会释放鼠标抓取，玩家才能把光标移到覆盖层上操作
            ActivateOverlay();
            FollowGameWindow();

            // 跟踪游戏窗口位置：玩家可能拖动窗口或改变分辨率
            _tracker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _tracker.Tick += (_, _) => FollowGameWindow();
            _tracker.Start();

            ModBase.Log("[Overlay] 叠加窗口已打开");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 打开叠加窗口失败", ModBase.LogLevel.Hint);
            Close();
        }
    }

    /// <summary>关闭叠加窗口。</summary>
    public static void Close()
    {
        _tracker?.Stop();
        _tracker = null;

        try
        {
            _window?.Close();
        }
        catch
        {
            // 忽略关闭异常
        }

        _window = null;
    }

    /// <summary>
    ///     把叠加窗口切到前台并让它拿到键盘焦点。<br/>
    ///     分支说明：仅靠 Window.Activate() 在「游戏始终是最前台窗口」的情况下
    ///     会失败（Windows 的前台锁），所以这里用 SetForegroundWindow 兜底。
    /// </summary>
    /// <summary>
    ///     确保游戏的「失焦自动暂停」处于关闭状态。<br/>
    ///     分支说明：覆盖层打字时需要接管键盘焦点，而 Minecraft 默认在失焦时弹暂停菜单，
    ///     会把玩家打断。启动游戏前把这个选项关掉即可 —— 这是游戏自带设置，
    ///     不需要注入或钩子，也没有兼容性风险。<br/>
    ///     副作用：Alt+Tab 切出去后游戏会继续运行（怪物照常活动），故留了配置开关。
    /// </summary>
    public static void EnsurePauseOnLostFocusDisabled(McInstance? instance)
    {
        if (instance is null) return;
        // 分支说明：这是实例级开关，每个版本可以单独决定要不要关掉失焦暂停
        if (!Config.Instance.OverlayNoPause[instance.PathInstance]) return;

        try
        {
            var path = Path.Combine(instance.PathIndie, "options.txt");

            // 分支说明：实例第一次启动时 options.txt 还不存在。
            // 之前这里直接返回，导致「勾了开关但第一次启动仍会暂停」——
            // 说好的行为没生效，观感上就是个 bug。
            // 现在改为：目录在就写入一份最小内容（只含这一项），
            // Minecraft 读取时会为缺失的键补默认值，退出时再把完整设置写回，
            // 因此不会破坏玩家的其它设置。
            if (!File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    // 游戏目录都还没建立，说明实例确实从未运行过，不强行造目录
                    ModBase.Log("[Overlay] 游戏目录尚不存在，跳过失焦暂停设置", ModBase.LogLevel.Debug);
                    return;
                }

                File.WriteAllLines(path, ["pauseOnLostFocus:false"]);
                ModBase.Log($"[Overlay] 已创建 options.txt 并关闭失焦自动暂停：{path}");
                return;
            }

            var lines = File.ReadAllLines(path);
            var changed = false;
            var found = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("pauseOnLostFocus:", StringComparison.OrdinalIgnoreCase)) continue;

                found = true;
                if (lines[i].TrimEnd().EndsWith("false", StringComparison.OrdinalIgnoreCase)) break;

                lines[i] = "pauseOnLostFocus:false";
                changed = true;
                break;
            }

            // 老版本没有这个键，追加也不会有副作用（游戏会忽略不认识的选项）
            if (!found)
            {
                Array.Resize(ref lines, lines.Length + 1);
                lines[^1] = "pauseOnLostFocus:false";
                changed = true;
            }

            if (!changed) return;

            File.WriteAllLines(path, lines);
            ModBase.Log($"[Overlay] 已关闭游戏的失焦自动暂停：{path}");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 关闭失焦自动暂停失败", ModBase.LogLevel.Developer);
        }
    }


    /// <summary>
    ///     把游戏的「失焦自动暂停」改回开启。<br/>
    ///     分支说明：用户在实例设置里关掉覆盖层免暂停后调用 —— 该开关的副作用是
    ///     Alt+Tab 切出去游戏仍在运行，所以关闭时必须把游戏设置恢复原样，不能留下。
    /// </summary>
    public static void RestorePauseOnLostFocus(McInstance? instance)
    {
        if (instance is null) return;

        try
        {
            var path = Path.Combine(instance.PathIndie, "options.txt");
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path);
            var changed = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("pauseOnLostFocus:", StringComparison.OrdinalIgnoreCase)) continue;
                if (lines[i].TrimEnd().EndsWith("true", StringComparison.OrdinalIgnoreCase)) break;

                lines[i] = "pauseOnLostFocus:true";
                changed = true;
                break;
            }

            if (!changed) return;

            File.WriteAllLines(path, lines);
            ModBase.Log($"[Overlay] 已恢复游戏的失焦自动暂停：{path}");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 恢复失焦自动暂停失败", ModBase.LogLevel.Developer);
        }
    }
    public static void ActivateOverlay()
    {
        if (_window is null) return;
        try
        {
            _window.Activate();

            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);

            // 把焦点交给内容里的第一个输入框
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_window?.Content is FrameworkElement root)
                {
                    var box = FindFocusable(root);
                    if (box is not null)
                    {
                        box.Focus();
                        System.Windows.Input.Keyboard.Focus(box);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 激活窗口失败", ModBase.LogLevel.Developer);
        }
    }

    /// <summary>在视觉树里找第一个可聚焦的文本框。</summary>
    private static UIElement? FindFocusable(DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBox box) return box;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindFocusable(child);
            if (found is not null) return found;
        }

        return null;
    }

    private static void FollowGameWindow()
    {
        if (_window is null) return;

        var handle = GetGameWindow();
        if (handle == IntPtr.Zero)
        {
            // 游戏最小化或已退出：叠加窗口也收起来，别飘在桌面上
            if (_window.IsVisible) _window.Hide();
            return;
        }

        // 分支说明：面板自己拿到焦点时（用户正在输入），前台窗口会变成面板，
        // 这时不能因为「游戏不在前台」就把面板藏起来 —— 否则一打字就消失。
        if (!IsGameForeground() && !IsOverlayForeground())
        {
            if (_window.IsVisible)
            {
                // 隐藏本身不产生其它日志，这里留一条便于排查「面板莫名消失」
                ModBase.Log("[Overlay] 前台既不是游戏也不是面板，隐藏叠加窗口", ModBase.LogLevel.Debug);
                _window.Hide();
            }

            return;
        }

        try
        {
            if (!GetClientRect(handle, out var rect)) return;

            var origin = new Point();
            if (!ClientToScreen(handle, ref origin)) return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            // WPF 用的是设备无关单位，需要按 DPI 换算
            var source = (HwndSource?)PresentationSource.FromVisual(_window);
            var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
            var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1d;
            if (scaleX <= 0) scaleX = 1d;
            if (scaleY <= 0) scaleY = 1d;

            _window.Left = origin.X / scaleX;
            _window.Top = origin.Y / scaleY;
            _window.Width = width / scaleX;
            _window.Height = height / scaleY;

            if (!_window.IsVisible)
            {
                _window.Show();

                // Show 之后重新应用一次样式，确保置顶层级生效
                ApplyOverlayStyles(_window);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 跟随游戏窗口失败", ModBase.LogLevel.Developer);
        }
    }

    /// <summary>设置叠加窗口的基本样式：不进 Alt+Tab、不占任务栏。</summary>
    private static void ApplyOverlayStyles(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var exStyle = GetWindowLong(handle, GwlExstyle);

            // 分支说明：这里【不能】用 WS_EX_NOACTIVATE。
            // 一旦加上，窗口就不会成为前台，游戏便继续认为自己有焦点并抓着鼠标
            // （Minecraft 游戏内会 SetCapture 并把光标不断拉回中心），
            // 结果是鼠标被锁在准星位置，玩家根本没法把光标移到覆盖层上点按钮。
            // 因此覆盖层正常激活、正常接管鼠标与键盘；
            // 「不弹暂停菜单」改由启动时关闭游戏的 pauseOnLostFocus 来保证。
            exStyle |= WsExToolwindow;
            exStyle &= ~WsExNoactivate;

            if (_clickThrough)
                exStyle |= WsExTransparent;

            SetWindowLong(handle, GwlExstyle, exStyle);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 设置窗口样式失败", ModBase.LogLevel.Developer);
        }
    }
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;

    private static bool _clickThrough;

    /// <summary>设置是否让鼠标事件穿透到游戏（内容不可交互时用）。</summary>
    public static void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        if (_window is null) return;
        try
        {
            var handle = new WindowInteropHelper(_window).Handle;
            var exStyle = GetWindowLong(handle, GwlExstyle);
            exStyle = enabled ? exStyle | WsExTransparent : exStyle & ~WsExTransparent;
            SetWindowLong(handle, GwlExstyle, exStyle);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 切换鼠标穿透失败", ModBase.LogLevel.Developer);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);

    /// <summary>GetWindow 的 GW_OWNER：用于排除「有属主」的辅助窗口。</summary>
    private const uint GwOwner = 4;

    /// <summary>上一次找到的游戏窗口句柄，仅作加速用，每次都会校验它是否仍有效。</summary>
    private static IntPtr _cachedGameHandle;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);
}