using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCL;

/// <summary>
///     系统与游戏进程的实时资源占用采样。<br/>
///     分支说明：<br/>
///     - CPU / 内存占用用 Win32 的 GetSystemTimes 与 GlobalMemoryStatusEx 直接取，<br/>
///       不依赖 PerformanceCounter（那玩意在某些精简系统上缺计数器会抛异常）。<br/>
///     - 游戏进程的占用用 Process 本身的数据，更贴近玩家关心的「游戏吃了多少」。<br/>
///     - <b>FPS 无法获取</b>：启动器是外部进程，游戏不暴露帧率接口，<br/>
///       要拿 FPS 必须往游戏里注入 DLL 挂渲染钩子，与本功能「不注入」的前提冲突。
/// </summary>
internal static partial class ModSystemMonitor
{
    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    #endregion

    private static ulong _lastIdle;
    private static ulong _lastKernel;
    private static ulong _lastUser;
    private static bool _hasLastSample;

    /// <summary>系统总 CPU 占用率（0~100）。首次调用返回 0（需要两次采样才能算差值）。</summary>
    public static double GetCpuUsage()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0d;

            var idleTicks = idle.ToUInt64();
            var kernelTicks = kernel.ToUInt64();
            var userTicks = user.ToUInt64();

            if (!_hasLastSample)
            {
                _lastIdle = idleTicks;
                _lastKernel = kernelTicks;
                _lastUser = userTicks;
                _hasLastSample = true;
                return 0d;
            }

            var idleDelta = idleTicks - _lastIdle;
            var kernelDelta = kernelTicks - _lastKernel;
            var userDelta = userTicks - _lastUser;

            _lastIdle = idleTicks;
            _lastKernel = kernelTicks;
            _lastUser = userTicks;

            // kernel 时间已包含 idle，所以总量是 kernel + user
            var total = kernelDelta + userDelta;
            if (total == 0) return 0d;

            var usage = (1d - idleDelta / (double)total) * 100d;
            return Math.Clamp(usage, 0d, 100d);
        }
        catch
        {
            return 0d;
        }
    }

    /// <summary>物理内存占用百分比（0~100）。</summary>
    public static double GetMemoryUsagePercent()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return 0d;
            return Math.Clamp(status.MemoryLoad, 0d, 100d);
        }
        catch
        {
            return 0d;
        }
    }

    /// <summary>已用物理内存（MB）与总量（MB）。</summary>
    public static (double UsedMb, double TotalMb) GetMemoryInfo()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return (0d, 0d);

            var totalMb = status.TotalPhys / 1024d / 1024d;
            var usedMb = (status.TotalPhys - status.AvailPhys) / 1024d / 1024d;
            return (usedMb, totalMb);
        }
        catch
        {
            return (0d, 0d);
        }
    }

    /// <summary>游戏进程的内存占用（MB）；游戏未运行时返回 0。</summary>
    public static double GetGameMemoryMb()
    {
        try
        {
            var process = ModLaunch.mcLaunchProcess;
            if (process is null || process.HasExited) return 0d;
            return process.WorkingSet64 / 1024d / 1024d;
        }
        catch
        {
            return 0d;
        }
    }
}
