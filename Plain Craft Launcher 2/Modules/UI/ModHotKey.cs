using System.Runtime.InteropServices;

namespace PCL;

/// <summary>
///     全局热键注册。<br/>
///     分支说明：项目原本没有热键能力，这里用 RegisterHotKey 向系统注册，
///     消息由 <c>FormMain.WndProc</c> 拦截后转给回调。<br/>
///     之所以要「全局」而不是窗口内热键：玩家在游戏窗口里按键时，
///     启动器窗口是失焦的，只有系统级热键才能收到。
/// </summary>
internal static class ModHotKey
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>WM_HOTKEY 消息号。</summary>
    public const int WmHotkey = 0x0312;

    // 修饰键
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private static readonly Dictionary<int, Action> Handlers = new();
    private static IntPtr _hostHandle;

    /// <summary>
    ///     在一个宿主窗口上注册热键。<paramref name="id" /> 必须唯一。
    /// </summary>
    /// <param name="hostHandle">接收 WM_HOTKEY 的窗口句柄。</param>
    /// <param name="id">热键 ID（1..0xBFFF）。</param>
    /// <param name="modifiers">修饰键组合。</param>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <param name="action">按下时的回调。</param>
    public static bool Register(IntPtr hostHandle, int id, uint modifiers, uint virtualKey, Action action)
    {
        try
        {
            _hostHandle = hostHandle;

            // 已存在同名 ID 时先注销，避免重复注册失败
            if (Handlers.ContainsKey(id)) UnregisterHotKey(hostHandle, id);

            if (!RegisterHotKey(hostHandle, id, modifiers, virtualKey))
            {
                ModBase.Log($"[HotKey] 注册失败（ID {id}，可能是快捷键冲突）", ModBase.LogLevel.Hint);
                return false;
            }

            Handlers[id] = action;
            ModBase.Log($"[HotKey] 已注册 ID {id}：mods={modifiers}, vk=0x{virtualKey:X2}");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[HotKey] 注册热键异常", ModBase.LogLevel.Developer);
            return false;
        }
    }

    /// <summary>注销指定热键。</summary>
    public static void Unregister(int id)
    {
        try
        {
            Handlers.Remove(id);
            if (_hostHandle != IntPtr.Zero) UnregisterHotKey(_hostHandle, id);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[HotKey] 注销热键异常", ModBase.LogLevel.Developer);
        }
    }

    /// <summary>注销全部热键。</summary>
    public static void UnregisterAll()
    {
        foreach (var id in Handlers.Keys.ToList()) Unregister(id);
        Handlers.Clear();
    }

    /// <summary>
    ///     由 WndProc 调用：处理 WM_HOTKEY。
    /// </summary>
    /// <returns>是否为热键消息（已处理）。</returns>
    public static bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WmHotkey) return false;

        var id = wParam.ToInt32();
        if (!Handlers.TryGetValue(id, out var action)) return false;

        // 诊断：把 WM_HOTKEY 的 lParam 解出来，确认实际按下的是什么组合
        try
        {
            var raw = lParam.ToInt64();
            var mods = (int)(raw & 0xFFFF);
            var vk = (int)((raw >> 16) & 0xFFFF);
            ModBase.Log($"[HotKey] 触发 {id}：mods={mods}, vk=0x{vk:X2}", ModBase.LogLevel.Debug);
        }
        catch
        {
            // 诊断失败不影响功能
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[HotKey] 热键回调异常", ModBase.LogLevel.Developer);
        }

        return true;
    }
}