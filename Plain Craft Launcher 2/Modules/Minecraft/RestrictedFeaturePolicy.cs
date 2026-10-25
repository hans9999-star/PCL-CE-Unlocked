using PCL.Core.App.Region;

namespace PCL;

/// <summary>
///     受限功能策略。<br/>
///     分支说明：本分支移除了原社区版基于国家/地区的功能封锁
///     （Minecraft 下载、补全、加载器安装、整合包安装、修复与重置等），
///     因此 <see cref="Check" /> 恒为通过，<see cref="ThrowIfRestricted" /> 永不抛出。
/// </summary>
internal static class RestrictedFeaturePolicy
{
    /// <summary>
    ///     当前是否存在受限功能。本分支恒为 <see langword="false" />。
    /// </summary>
    internal static bool IsRestricted => false;

    /// <summary>
    ///     检查某个功能是否可用。本分支恒为可用。
    /// </summary>
    /// <param name="feature">功能标识，仅用于日志。</param>
    /// <param name="showHint">是否在受限时弹出提示。本分支恒为可用，故该参数不再生效。</param>
    internal static bool Check(string feature, bool showHint = true) => true;

    /// <summary>
    ///     若功能受限则抛出取消异常。本分支恒为可用，故永不抛出。
    /// </summary>
    /// <param name="feature">功能标识，仅用于日志。</param>
    internal static void ThrowIfRestricted(string feature)
    {
    }

    /// <summary>
    ///     评估当前区域信号。保留该入口以便日志与诊断使用。
    /// </summary>
    internal static RegionAssessment Assess() => RegionPolicy.Evaluate(RegionPolicy.ReadWindowsSignals());
}
