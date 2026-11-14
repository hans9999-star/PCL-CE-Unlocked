using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     启动器单实例限制。<br/>
///     分支说明：本分支原先为了「删除所有限制」移除了单实例锁，允许多开。
///     但多开会让「识别并接管正在运行的游戏」变得没有意义 —— 两个启动器都会去抢同一个
///     游戏进程，热键与覆盖层也会互相打架。因此这里把限制加回来。<br/>
///     锁按**程序所在路径**区分：不同目录下的两份启动器互不影响，只有同一份被重复启动才拦截。
/// </summary>
internal static class ModSingleInstance
{
    private static Mutex? _mutex;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    /// <summary>
    ///     尝试取得单实例所有权。<br/>
    ///     若已有实例在运行，会把它的窗口激活，然后返回 false —— 调用方应直接退出本进程。
    /// </summary>
    /// <returns>true 表示可以继续启动；false 表示应退出（已把控制权交给既有实例）。</returns>
    public static bool TryAcquire()
    {
        try
        {
            if (!Config.Tool.SingleInstance) return true;

            _mutex = new Mutex(true, BuildMutexName(), out var createdNew);
            if (createdNew) return true;

            // 已有实例：把它请到前台，本进程随即退出
            var activated = ActivateExistingLauncher();
            ModBase.Log(activated
                ? "[SingleInstance] 已有实例在运行，已激活其窗口并退出本次启动"
                : "[SingleInstance] 已有实例在运行，但未能激活其窗口；仍退出本次启动",
                ModBase.LogLevel.Debug);
            return false;
        }
        catch (AbandonedMutexException)
        {
            // 上一个进程异常退出（没释放锁），此时锁归我们所有，正常继续
            ModBase.Log("[SingleInstance] 上一个实例异常退出，已接管单实例锁");
            return true;
        }
        catch (Exception ex)
        {
            // 任何异常都不该拦着用户启动，放行
            ModBase.Log(ex, "[SingleInstance] 单实例检查失败，按允许多开处理", ModBase.LogLevel.Debug);
            return true;
        }
    }

    /// <summary>锁名带上程序路径的哈希，使不同安装位置互不干扰。</summary>
    private static string BuildMutexName()
    {
        var path = Environment.ProcessPath ?? ModBase.exePath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return @"Local\PCL-Unbound-Single-" + Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    ///     找到另一个启动器进程的主窗口并置前。<br/>
    ///     分支说明：不按窗口标题找，而是按**进程名**找 —— 标题可以被用户自定义，
    ///     而且多语言环境下也不稳定；进程名就是当前 exe 的名字，最可靠。
    /// </summary>
    private static bool ActivateExistingLauncher()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            var name = self.ProcessName;
            var selfId = self.Id;

            var best = IntPtr.Zero;

            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.Id == selfId) continue;

                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;

                    // 优先激活带窗口的那个；找到一个就够
                    best = handle;
                    break;
                }
                catch
                {
                    // 读取别的进程可能因权限失败，跳过
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (best == IntPtr.Zero) return false;

            // 最小化的窗口要先还原，否则置前后仍是收起的
            ShowWindow(best, SwRestore);
            return SetForegroundWindow(best);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[SingleInstance] 激活既有窗口失败", ModBase.LogLevel.Developer);
            return false;
        }
    }
}