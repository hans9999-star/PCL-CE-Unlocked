namespace PCL.Core.App;

using System;
/// <summary>
/// 联机协议偏好
/// </summary>
public enum LinkProtocolPreference
{
    Tcp,
    Udp
}

/// <summary>
/// 文件下载使用的 HTTP 协议模式。
/// </summary>
public enum DownloadHttpMode
{
    Auto = 0,
    Http11 = 1,
    Http2 = 2
}

/// <summary>
/// 主题模式（亮/暗/系统）
/// </summary>
public enum ColorMode
{
    Light = 0,
    Dark = 1,
    System = 2
}

/// <summary>
/// 配色主题。<br/>
/// 分支说明：本分支恢复了原版 PCL 的全部主题（含隐藏主题与愚人节主题），
/// 每个主题对应一个色相值，由 CE 的 OKLCH 配色引擎按统一配方生成整套颜色，
/// 这样既保证观感统一，又不依赖原版的 Themes/{n}.png 背景图。
/// </summary>
public enum ColorTheme
{
    /// <summary>龙猫蓝（默认）</summary>
    CatBlue = 0,

    /// <summary>甜柠青</summary>
    SweetCyan = 1,

    /// <summary>小草绿</summary>
    GrassGreen = 2,

    /// <summary>菠萝黄</summary>
    PineappleYellow = 3,

    /// <summary>橡木棕</summary>
    OakBrown = 4,

    /// <summary>玄素黑</summary>
    PlainBlack = 5,

    /// <summary>铁杆粉</summary>
    IronPink = 6,

    /// <summary>神秘紫</summary>
    MysticPurple = 7,

    /// <summary>秋仪金</summary>
    AutumnGold = 8,

    /// <summary>活跃橙</summary>
    ActiveOrange = 9,

    /// <summary>跳票红</summary>
    DelayedRed = 10,

    /// <summary>极客蓝</summary>
    GeekBlue = 11,

    /// <summary>滑稽彩（随时变色）</summary>
    FunnyColor = 12,

    /// <summary>欧皇彩（随时变色）</summary>
    LuckyColor = 13,

    /// <summary>自定义</summary>
    Custom = 14
}

/// <summary>
/// 配色主题的辅助信息。
/// </summary>
public static class ColorThemeInfo
{
    /// <summary>
    /// 全部可选主题，按界面展示顺序排列（与原版设置页顺序一致）。
    /// </summary>
    public static readonly ColorTheme[] All =
    [
        ColorTheme.CatBlue,
        ColorTheme.SweetCyan,
        ColorTheme.GrassGreen,
        ColorTheme.PineappleYellow,
        ColorTheme.OakBrown,
        ColorTheme.PlainBlack,
        ColorTheme.FunnyColor,
        ColorTheme.IronPink,
        ColorTheme.MysticPurple,
        ColorTheme.LuckyColor,
        ColorTheme.AutumnGold,
        ColorTheme.ActiveOrange,
        ColorTheme.DelayedRed,
        ColorTheme.GeekBlue,
        ColorTheme.Custom
    ];

    /// <summary>
    /// 主题的本地化名称对应的资源键。
    /// </summary>
    public static string DisplayNameKey(this ColorTheme theme) => theme switch
    {
        ColorTheme.CatBlue => "Setup.Ui.Theme.Color.CatBlue",
        ColorTheme.SweetCyan => "Setup.Ui.Theme.Color.SweetCyan",
        ColorTheme.GrassGreen => "Setup.Ui.Theme.Color.GrassGreen",
        ColorTheme.PineappleYellow => "Setup.Ui.Theme.Color.PineappleYellow",
        ColorTheme.OakBrown => "Setup.Ui.Theme.Color.OakBrown",
        ColorTheme.PlainBlack => "Setup.Ui.Theme.Color.PlainBlack",
        ColorTheme.FunnyColor => "Setup.Ui.Theme.Color.FunnyColor",
        ColorTheme.IronPink => "Setup.Ui.Theme.Color.IronPink",
        ColorTheme.MysticPurple => "Setup.Ui.Theme.Color.MysticPurple",
        ColorTheme.LuckyColor => "Setup.Ui.Theme.Color.LuckyColor",
        ColorTheme.AutumnGold => "Setup.Ui.Theme.Color.AutumnGold",
        ColorTheme.ActiveOrange => "Setup.Ui.Theme.Color.ActiveOrange",
        ColorTheme.DelayedRed => "Setup.Ui.Theme.Color.DelayedRed",
        ColorTheme.GeekBlue => "Setup.Ui.Theme.Color.GeekBlue",
        ColorTheme.Custom => "Setup.Ui.Theme.Color.Custom",
        _ => "Setup.Ui.Theme.Color.CatBlue"
    };

    /// <summary>
    /// 主题是否为会随时间闪动的多彩主题。
    /// </summary>
    public static bool IsRainbow(this ColorTheme theme) =>
        theme is ColorTheme.FunnyColor or ColorTheme.LuckyColor;

    /// <summary>
    /// 在 <see cref="All" /> 中查找主题的下标。
    /// </summary>
    public static int IndexOf(ColorTheme theme)
    {
        var index = Array.IndexOf(All, theme);
        return index < 0 ? 0 : index;
    }

    /// <summary>
    /// 把界面上下标转换为主题。越界时返回默认主题。
    /// </summary>
    public static ColorTheme FromIndex(int index) =>
        index >= 0 && index < All.Length ? All[index] : ColorTheme.CatBlue;
}

/// <summary>
/// 更新通道
/// </summary>
public enum UpdateChannel
{
    Release = 0,
    Beta = 1,
    Dev = 2
}

/// <summary>
/// 游戏窗口大小模式
/// </summary>
public enum GameWindowSizeMode
{
    Fullscreen = 0,
    Default = 1,
    Launcher = 2,
    Custom = 3,
    Maximized = 4
}

/// <summary>
/// 游戏进程优先级
/// </summary>
public enum GameProcessPriority
{
    AboveNormal = 0,
    Normal = 1,
    BelowNormal = 2,
    High = 3,
    RealTime = 4
}

/// <summary>
/// 游戏启动后启动器可见性
/// </summary>
public enum LauncherVisibility
{
    ExitImmediately = 0,
    ObsoleteCaseDoNotUse = 1,
    HideAndExit = 2,
    HideAndReopen = 3,
    MinimizeAndReopen = 4,
    DoNothing = 5
}

/// <summary>
/// JVM 优先 IP 栈类型
/// </summary>
public enum JvmPreferredIpStack
{
    PreferV4 = 0,
    Default = 1,
    PreferV6 = 2
}

/// <summary>
/// 联机中继行为
/// </summary>
public enum LinkRelayBehavior
{
    Default = 0,
    ForceRelay = 1
}

/// <summary>
/// 启动器更新行为
/// </summary>
public enum LauncherAutoUpdateBehavior
{
    DownloadAndInstall = 0,
    DownloadAndAnnounce = 1,
    AnnounceOnly = 2,
    Disable = 3
}

public enum LauncherTitleType
{
    None = 0,
    Default = 1,
    Text = 2,
    Image = 3
}
