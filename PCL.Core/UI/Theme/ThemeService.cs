using System;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 配色模式更改事件。
/// </summary>
/// <param name="isDarkMode">当前是否为深色模式</param>
/// <param name="theme">当前配色主题</param>
public delegate void ColorModeChangedEvent(bool isDarkMode, ColorTheme theme);

/// <summary>
/// 配色主题更改事件。
/// </summary>
/// <param name="theme">当前配色主题</param>
public delegate void ColorThemeChangedEvent(ColorTheme theme);

[LifecycleScope("theme", "主题", false)]
[LifecycleService(LifecycleState.WindowCreating)]
public sealed partial class ThemeService
{
    [AnyConfigItem<ToneProfileConfig>("UiToneProfiles", ConfigSource.Local)]
    public static partial ToneProfileConfig ToneProfiles { get; set; }

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorModeConfigChanged => new(
        scope: Config.Preference.Theme.ColorModeConfig,
        trigger: ConfigEvent.Update,
        handler: _ => RefreshColorMode()
    );

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorThemeConfigChanged => new(
        scope: [Config.Preference.Theme.DarkColorConfig, Config.Preference.Theme.LightColorConfig],
        trigger: ConfigEvent.Update,
        handler: e =>
        {
            // ignore no change or non-current color theme change
            if (e.OldValue == e.Value) return;
            if (IsDarkMode) { if (e.Item == Config.Preference.Theme.LightColorConfig) return; }
            else { if (e.Item == Config.Preference.Theme.DarkColorConfig) return; }
            // trigger color refresh
            if (Lifecycle.CurrentState > LifecycleState.Loading)
            {
                Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(() =>
                {
                    ApplyColorResources();
                    ColorThemeChanged?.Invoke(CurrentTheme);
                    _AprilFoolLogic();
                });
            }
        }
    );

    [LifecycleStart]
    private static void _Start()
    {
        IsDarkMode = _IsDarkMode();
        _LogStatus();
        _RefreshAll();
    }

    private static void _LogStatus()
    {
        Context.Debug($"当前状态: {(IsDarkMode ? "Dark" : "Light")}, {CurrentTheme}");
    }

    private static bool _IsDarkMode() => Config.Preference.Theme.ColorMode switch
    {
        ColorMode.Light => false,
        ColorMode.Dark => true,
        ColorMode.System => SystemThemeHelper.IsSystemInDarkMode(),
        _ => false
    };

    /// <summary>
    /// 当前是否为深色主题。
    /// </summary>
    public static bool IsDarkMode { get; private set; }

    /// <summary>
    /// 配色模式更改事件。
    /// </summary>
    public static event ColorModeChangedEvent? ColorModeChanged;

    /// <summary>
    /// 配色主题更改事件。
    /// </summary>
    public static event ColorThemeChangedEvent? ColorThemeChanged;

    private static void _RefreshAll()
    {
        ApplyGrayResources();
        ApplyColorResources();
        ColorModeChanged?.Invoke(IsDarkMode, CurrentTheme);
        _AprilFoolLogic();
    }

    private static void _AprilFoolLogic()
    {
        // for HMCL theme on April Fools' Day
        if (Basics.IsAprilFool) Config.Preference.WindowTitleTypeConfig.TriggerEvent(ConfigEvent.Changed, null);
    }

    /// <summary>
    /// 「千万别点」对全局配色造成的影响。<br/>
    /// <list type="bullet">
    ///     <item><description><see cref="ThemeCorruption.None" />：正常。</description></item>
    ///     <item><description><see cref="ThemeCorruption.Blinding" />：眼瞎白，亮度被拉到极限。</description></item>
    ///     <item><description><see cref="ThemeCorruption.Random" />：真·滑稽彩，每 50ms 随机换一次配色。</description></item>
    /// </list>
    /// </summary>
    public enum ThemeCorruption
    {
        None = 0,
        Blinding = 1,
        Random = 2
    }

    /// <summary>
    /// 当前「千万别点」造成的配色异常状态。一旦触发会一直生效，直到重启启动器。
    /// </summary>
    public static ThemeCorruption Corruption { get; set; } = ThemeCorruption.None;

    /// <summary>
    /// 多彩主题当前的色相。由主时钟推进。
    /// </summary>
    public static int RainbowHue { get; private set; } = 0;

    /// <summary>
    /// 推进多彩主题的色相。若当前主题不是多彩主题，则不做任何事。
    /// </summary>
    /// <returns>是否发生了推进，即是否需要重绘配色。</returns>
    public static bool PulseRainbow(int step = 4)
    {
        if (!CurrentTheme.IsRainbow()) return false;
        RainbowHue = (RainbowHue + step) % 360;
        ApplyColorResources();
        return true;
    }

    /// <summary>
    /// 「真·滑稽彩」状态下的配色重绘。由主时钟每 50ms 调用一次。
    /// </summary>
    /// <returns>是否发生了重绘。</returns>
    public static bool PulseCorruption()
    {
        if (Corruption != ThemeCorruption.Random) return false;
        ApplyColorResources();
        return true;
    }

    /// <summary>
    /// 刷新配色模式，若检测到当前配色模式有实际更改，则会触发主题刷新。
    /// </summary>
    public static void RefreshColorMode()
    {
        var isDarkMode = _IsDarkMode();
        if (IsDarkMode == isDarkMode) return;
        Context.Info("正在更改配色模式");
        IsDarkMode = isDarkMode;
        _LogStatus();
        if (Lifecycle.CurrentState > LifecycleState.Loading)
        {
            Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(_RefreshAll);
        }
    }

    /// <summary>
    /// 当前使用的色彩属性。
    /// </summary>
    public static ToneProfile CurrentTone => IsDarkMode ? ToneProfiles.Dark : ToneProfiles.Light;

    /// <summary>
    /// 当前使用的主题色。
    /// </summary>
    public static ColorTheme CurrentTheme
    {
        get
        {
            var theme = Config.Preference.Theme;
            return IsDarkMode ? theme.DarkColor : theme.LightColor;
        }
        set
        {
            var theme = Config.Preference.Theme;
            var config = IsDarkMode ? theme.DarkColorConfig : theme.LightColorConfig;
            config.SetValue(value);
        }
    }

    /// <summary>
    /// 获取当前色彩主题对应的各种参数。<br/>
    /// 分支说明：每个主题对应一组 OKLCH 色相 / 明度偏移 / 色度偏移，
    /// 由 CE 的配色引擎统一生成整套 ColorObject。龙猫蓝的数值与 CE 原版完全一致。
    /// 「千万别点」的两种异常配色优先级最高，会临时覆盖正常配色。
    /// </summary>
    public static (int Hue, double LightAdjust, double ChromaAdjust) GetCurrentThemeArgs()
    {
        switch (Corruption)
        {
            case ThemeCorruption.Blinding:
                // 眼瞎白：与原版一致，把亮度拉到极限
                return (255, 1d, -0.2);
            case ThemeCorruption.Random:
                // 真·滑稽彩：每次刷新随机
                return (Random.Shared.Next(0, 360), Random.Shared.Next(-20, 21) / 20d,
                    Random.Shared.Next(40, 71) / 50d - 1d);
        }

        var theme = CurrentTheme;

        // 多彩主题：色相随时间循环
        if (theme.IsRainbow()) return (RainbowHue, 0.05, 0.35);

        // 自定义配色
        if (theme is ColorTheme.Custom) return _GetCustomThemeArgs();

        return theme switch
        {
            // 龙猫蓝：CE 原版数值，保持不变
            ColorTheme.CatBlue => (255, 0, -0.2),
            ColorTheme.SweetCyan => (185, 0.02, 0),
            ColorTheme.GrassGreen => (145, -0.02, 0.05),
            ColorTheme.PineappleYellow => (95, 0.05, 0.05),
            ColorTheme.OakBrown => (35, -0.12, -0.05),
            ColorTheme.PlainBlack => (255, -0.62, -0.95),
            ColorTheme.IronPink => (350, 0.02, 0.05),
            ColorTheme.MysticPurple => (305, -0.04, 0),
            ColorTheme.AutumnGold => (70, 0, 0.05),
            ColorTheme.ActiveOrange => (42, 0.03, 0.1),
            ColorTheme.DelayedRed => (15, -0.02, 0.05),
            ColorTheme.GeekBlue => (245, -0.12, -0.15),
            _ => (255, 0, -0.2)
        };
    }

    private static (int Hue, double LightAdjust, double ChromaAdjust) _GetCustomThemeArgs()
    {
        var theme = Config.Preference.Theme;
        var hue = ((theme.WindowHue % 360) + 360) % 360;
        // 饱和度 50 为中性，0 完全去色，100 完全饱和
        var chroma = theme.WindowSat / 50d - 1d;
        // 明度 20 为中性
        var light = (theme.WindowLight - 20) / 20d;
        return (hue, Math.Clamp(light, -1d, 1d), Math.Clamp(chroma, -1d, 1d));
    }

    private static double _AdjustLinear(double value, double adjustment)
    {
        if (adjustment == 0) return value;
        // 确保输入在合理范围内
        value = Math.Clamp(value, 0.0, 1.0);
        adjustment = Math.Clamp(adjustment, -1.0, 1.0);
        // 非对称线性插值
        return adjustment switch
        {
            > 0 => value + (1.0 - value) * adjustment,
            _ => value + value * adjustment
        };
    }

    private static CatColorResource[] _CalculateGrays(ToneProfile tone) => [
        LabColor.FromLch(tone.L1).ToCatColor("Gray1"),
        LabColor.FromLch(tone.L2).ToCatColor("Gray2"),
        LabColor.FromLch(tone.L3).ToCatColor("Gray3"),
        LabColor.FromLch(tone.L4).ToCatColor("Gray4"),
        LabColor.FromLch(tone.L5).ToCatColor("Gray5"),
        LabColor.FromLch(tone.L6).ToCatColor("Gray6"),
        LabColor.FromLch(tone.L7).ToCatColor("Gray7"),
        LabColor.FromLch(tone.L8).ToCatColor("Gray8"),
        LabColor.FromLch(tone.LWhite, alpha:tone.AHalfWhite).ToCatColor("HalfWhite", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ASemiWhite).ToCatColor("SemiWhite", false),
        LabColor.FromLch(tone.LWhite).ToCatColor("White", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ATransparent).ToCatColor("Transparent", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.ABackground).ToCatColor("TransparentBackground", false),
        LabColor.FromLch(tone.LBackground).ToCatColor("Background", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.AToolTip).ToCatColor("ToolTip", false),
        LabColor.FromLch(tone.L7, 0.25, 30, tone.AHalfTransparent).ToCatColor("RedBack", false),
        LabColor.FromLch(tone.LForeground).ToCatColor("Memory", false),
    ];

    private static CatColorResource[] _CalculateColors(ToneProfile tone, (int hue, double lightAdj, double chromaAdj) args) => [
        LabColor.FromLch(_AdjustLinear(tone.L1, args.lightAdj * 0.1), _AdjustLinear(tone.C1, args.chromaAdj * 0.25), args.hue).ToCatColor("1"),
        LabColor.FromLch(_AdjustLinear(tone.L2, args.lightAdj), _AdjustLinear(tone.C2, args.chromaAdj), args.hue).ToCatColor("2"),
        LabColor.FromLch(_AdjustLinear(tone.L3, args.lightAdj), _AdjustLinear(tone.C3, args.chromaAdj), args.hue).ToCatColor("3"),
        LabColor.FromLch(_AdjustLinear(tone.L4, args.lightAdj), _AdjustLinear(tone.C4, args.chromaAdj), args.hue).ToCatColor("4"),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("5"),
        LabColor.FromLch(_AdjustLinear(tone.L6, args.lightAdj), _AdjustLinear(tone.C6, args.chromaAdj), args.hue).ToCatColor("6"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue).ToCatColor("7"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue).ToCatColor("8"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue, tone.ASemiTransparent).ToCatColor("SemiTransparent", false),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("Bg0"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue, tone.ASemiWhite).ToCatColor("Bg1"),
    ];

    private static CatColorResource[] LightGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Light); set; } = null!;

    private static CatColorResource[] DarkGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Dark); set; } = null!;

    /// <summary>
    /// 清除灰度配色的计算缓存。
    /// </summary>
    public static void InvalidateGrayCache()
    {
        LightGrayCache = null!;
        DarkGrayCache = null!;
    }

    /// <summary>
    /// 应用灰度配色到 WPF 资源字典。
    /// </summary>
    public static void ApplyGrayResources()
    {
        var cache = IsDarkMode ? DarkGrayCache : LightGrayCache;
        foreach (var c in cache) c.Apply();
    }

    /// <summary>
    /// 上次应用配色时的参数签名，用于跳过重复计算。
    /// </summary>
    private static (int Hue, double Light, double Chroma, bool Dark) _appliedColorSignature =
        (int.MinValue, 0, 0, false);

    /// <summary>
    /// 应用彩色配色到 WPF 资源字典。<br/>
    /// 参数未变化时直接跳过：_CalculateColors 会做 11 次 OKLCH 换算，
    /// 启动阶段若被多次调用会明显拖慢滑入动画。
    /// </summary>
    public static void ApplyColorResources()
    {
        var args = GetCurrentThemeArgs();
        var signature = (args.Hue, args.LightAdjust, args.ChromaAdjust, IsDarkMode);
        if (signature == _appliedColorSignature) return;
        _appliedColorSignature = signature;

        var colors = _CalculateColors(CurrentTone, args);
        foreach (var c in colors) c.Apply();
    }
}
