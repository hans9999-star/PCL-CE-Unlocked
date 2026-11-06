using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;

namespace PCL.Core.App;

// ReSharper disable InconsistentNaming
public static class Secrets
{
    /// <summary>
    /// 微软 OAuth 的 Client ID
    /// </summary>
    public static string MSOAuthClientId { get; } = EnvironmentInterop.GetSecret("MS_CLIENT_ID", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// CurseForge API 的 Client ID
    /// </summary>
    public static string CurseForgeAPIKey { get; } = EnvironmentInterop.GetSecret("CURSEFORGE_API_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 遥测密钥
    /// </summary>
    public static string TelemetryKey { get; } = EnvironmentInterop.GetSecret("TELEMETRY_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientId { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_ID", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientSecret { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_SECRET", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 联机根服务器。<br/>
    /// 分支说明：官方通过私有的 LINK_SERVER_ROOT 构建密钥注入，第三方构建拿不到，
    /// 会导致联机完全不可用。这里填入公开可访问的 PCL CE 大厅根服务器作为默认值
    /// （已验证 announce.json 与 cache.ini 均可用）。若构建时注入了密钥则以密钥为准。
    /// </summary>
    public static string[] LinkServers { get; } = EnvironmentInterop
        .GetSecret("LINK_SERVER_ROOT", readEnvDebugOnly: true)
        .ReplaceNullOrEmpty("https://s3.pysio.online/pcl2-ce")
        .Split("|");

    /// <summary>
    /// 当前版本的 Git 提交 SHA
    /// </summary>
    public static string CommitHash { get; } = EnvironmentInterop.GetSecret("GITHUB_SHA", readEnvDebugOnly: true).ReplaceNullOrEmpty();
}
