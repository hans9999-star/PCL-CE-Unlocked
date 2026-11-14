using System.Diagnostics;
using System.Management;
using System.Windows.Threading;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     识别并接管已经在运行的游戏进程。<br/>
///     分支说明：<see cref="ModLaunch.mcLaunchProcess" /> 只在本启动器亲自启动游戏时才被赋值，
///     所以「关掉启动器再打开」之后，那个还在跑的游戏就认不出来了 ——
///     游戏内覆盖层、Shift+Tab 热键、停止游戏按钮全部失效。<br/>
///     这里通过 WMI 读取 java 进程的命令行反查：<br/>
///     - 用 <c>net.minecraft.client.main.Main</c> 把 java 进程筛成「是 Minecraft」
///       （Fabric / Forge 会把它放在 <c>-DFabricMcEmu=</c> 之类的参数里，同样能命中）；<br/>
///     - 再用实例目录判断到底是哪一个实例，并据此切换启动器的选中项。
/// </summary>
internal static class ModGameAdopt
{
    /// <summary>Minecraft 客户端主类的标志串，用于把 java 进程筛成「是 Minecraft」。</summary>
    private const string MinecraftMarker = "net.minecraft.client.main.Main";

    private static DateTime _lastAttempt = DateTime.MinValue;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private static DispatcherTimer? _watchTimer;
    private static bool _lastRunningState;

    /// <summary>
    ///     已有可用进程时直接返回；否则尝试识别并接管。<br/>
    ///     带节流，避免覆盖层每次刷新都去查一遍 WMI（那个查询不算便宜）。
    /// </summary>
    public static bool TryAdopt(McInstance? instance, bool force = false)
    {
        // 已经认识这个进程就不用再查
        try
        {
            if (ModLaunch.mcLaunchProcess is { HasExited: false }) return true;
        }
        catch
        {
            // 进程对象可能已失效，继续走识别流程
        }

        if (!force && DateTime.Now - _lastAttempt < RetryInterval) return false;
        _lastAttempt = DateTime.Now;

        try
        {
            var candidates = QueryMinecraftProcesses();
            if (candidates.Count == 0) return false;

            var match = PickBest(candidates, instance);
            if (match is null) return false;

            var process = Process.GetProcessById(match.Value.Pid);
            if (process.HasExited) return false;

            ModLaunch.mcLaunchProcess = process;
            ModBase.Log($"[Overlay] 已接管正在运行的游戏进程 (PID {match.Value.Pid})");

            // 顺手认出「是哪个实例」，必要时把启动器的选中项也切过去
            AdoptInstanceFromCommandLine(match.Value.CommandLine);

            StartWatching();
            _lastRunningState = true;
            NotifyLaunchUi();
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 识别正在运行的游戏失败", ModBase.LogLevel.Developer);
            return false;
        }
    }

    /// <summary>清除节流，供「立即重试」的场景使用。</summary>
    public static void ResetThrottle() => _lastAttempt = DateTime.MinValue;

    private readonly record struct Candidate(int Pid, string CommandLine);

    #region 识别

    /// <summary>用 WMI 查所有 java 进程的命令行。</summary>
    private static List<Candidate> QueryMinecraftProcesses()
    {
        var result = new List<Candidate>();

        try
        {
            // 只查 java/javaw，别去遍历全部进程 —— 读命令行是相对昂贵的操作
            const string query =
                "SELECT ProcessId, CommandLine FROM Win32_Process " +
                "WHERE Name = 'javaw.exe' OR Name = 'java.exe'";

            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", query);
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var commandLine = item["CommandLine"]?.ToString();
                    if (string.IsNullOrWhiteSpace(commandLine)) continue;

                    // 只认 Minecraft，避免把 IDE、其它 Java 程序当成游戏
                    if (!commandLine.Contains(MinecraftMarker, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pidRaw = item["ProcessId"];
                    if (pidRaw is null) continue;

                    result.Add(new Candidate(Convert.ToInt32(pidRaw), commandLine));
                }
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 查询 java 进程失败", ModBase.LogLevel.Developer);
        }

        return result;
    }

    /// <summary>
    ///     从候选里挑出最可能是「当前实例」的那一个。<br/>
    ///     优先命令行里出现实例目录的；只有一个候选时也可以接受。
    /// </summary>
    private static Candidate? PickBest(List<Candidate> candidates, McInstance? instance)
    {
        if (instance is not null)
        {
            var markers = new[] { instance.PathInstance, instance.PathIndie }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var candidate in candidates)
            foreach (var marker in markers)
                if (candidate.CommandLine.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return candidate;

            if (!string.IsNullOrWhiteSpace(instance.Name))
                foreach (var candidate in candidates)
                    if (candidate.CommandLine.Contains(instance.Name, StringComparison.OrdinalIgnoreCase))
                        return candidate;
        }

        // 只有一个 Minecraft 进程在跑，那就必然是它
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    ///     从命令行反查是哪个实例，必要时切换启动器的选中项。<br/>
    ///     分支说明：接管进程还不够 —— 界面上的实例名、覆盖层用的全景图等都跟着
    ///     「当前选中实例」走，不切过去的话用户看到的还是另一个版本。
    /// </summary>
    private static void AdoptInstanceFromCommandLine(string commandLine)
    {
        try
        {
            var running = FindInstanceInCommandLine(commandLine);
            if (running is null) return;

            if (ReferenceEquals(running, ModInstanceList.McMcInstanceSelected)) return;

            ModInstanceList.McMcInstanceSelected = running;
            States.Game.SelectedInstance = running.Name;
            ModBase.Log($"[Overlay] 正在运行的实例是 {running.Name}，已切换启动器的选中项");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 切换选中实例失败", ModBase.LogLevel.Developer);
        }
    }

    /// <summary>
    ///     在实例列表里找出命令行指向的那一个。<br/>
    ///     按路径长度倒序匹配，避免前缀误判 —— 例如 <c>versions\1.21.11</c> 是
    ///     <c>versions\1.21.11-Fabric</c> 的前缀，先匹配长的才不会认错。
    /// </summary>
    private static McInstance? FindInstanceInCommandLine(string commandLine)
    {
        return ModInstanceList.mcInstanceList
            .SelectMany(pair => pair.Value)
            .Where(i => !string.IsNullOrWhiteSpace(i.PathInstance))
            .Where(i => commandLine.Contains(i.PathInstance, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.PathInstance.Length)
            .FirstOrDefault();
    }

    #endregion

    #region 运行状态监听

    /// <summary>
    ///     开始盯着游戏进程。<br/>
    ///     分支说明：启动按钮需要在「游戏跑起来 / 退出」时切换文案，
    ///     而接管来的进程没有启动器自己的生命周期事件可依赖，只能轮询。
    ///     只在确实接管到进程后才启动，进程退出即自行停止，不会长期空转。
    /// </summary>
    private static void StartWatching()
    {
        if (_watchTimer is not null) return;

        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _watchTimer.Tick += (_, _) => CheckProcessAlive();
        _watchTimer.Start();
    }

    private static void StopWatching()
    {
        _watchTimer?.Stop();
        _watchTimer = null;
    }

    private static void CheckProcessAlive()
    {
        var alive = false;
        try
        {
            alive = ModLaunch.mcLaunchProcess is { HasExited: false };
        }
        catch
        {
            alive = false;
        }

        if (alive == _lastRunningState) return;

        _lastRunningState = alive;

        if (!alive)
        {
            ModBase.Log("[Overlay] 接管到的游戏已退出");
            ModLaunch.mcLaunchProcess = null;
            StopWatching();
        }

        NotifyLaunchUi();
    }

    #endregion

    /// <summary>让启动页重新评估按钮状态（「启动游戏」↔「结束游戏」）。</summary>
    private static void NotifyLaunchUi()
    {
        try
        {
            ModBase.RunInUi(() => ModMain.frmLaunchLeft?.RefreshButtonsUI());
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 刷新启动按钮失败", ModBase.LogLevel.Developer);
        }
    }
}
