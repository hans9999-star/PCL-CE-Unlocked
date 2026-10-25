using System.IO;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     基于 GitHub Releases 的更新源。<br/>
///     读取 <c>https://api.github.com/repos/{owner}/{repo}/releases/latest</c>，
///     从 Release 的 Assets 中寻找 exe 并下载。
///     适用于自行发布 Release 的第三方改版。
/// </summary>
public class UpdatesGitHubModel : IUpdateSource
{
    private readonly string _apiUrl;

    private GitHubRelease? _cache;

    /// <param name="owner">GitHub 用户名</param>
    /// <param name="repo">仓库名</param>
    /// <param name="name">更新源显示名</param>
    public UpdatesGitHubModel(string owner, string repo, string name = "GitHub")
    {
        _apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        SourceName = name;
    }

    public string SourceName { get; set; }

    public bool IsAvailable() => !string.IsNullOrWhiteSpace(_apiUrl);

    public bool RefreshCache()
    {
        var json = Requester.FetchString(_apiUrl, RequestParam.WithRetry);
        var release = ModBase.GetJson(json).ToObject<GitHubRelease>();
        if (release is null) throw new NullReferenceException("无法解析 GitHub Release 信息！");
        _cache = release;
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var release = _EnsureCache();
        var asset = _FindAsset(release);
        if (asset is null) throw new NullReferenceException("该 Release 中没有可用的启动器文件！");

        var versionName = (release.TagName ?? "").TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(versionName)) versionName = release.Name ?? "0.0.0";

        return new VersionDataModel
        {
            VersionName = versionName,
            // GitHub 不提供数字版本号，用标签里的数字部分或固定值兜底
            VersionCode = _ParseVersionCode(versionName),
            Sha256 = null, // Release 未提供摘要，交由下载后自行校验
            Source = SourceName,
            Changelog = release.Body
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latest = GetLatestVersion(channel, arch);
        return currentVersion >= SemVer.Parse(latest.VersionName);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        // GitHub 更新源不提供公告，返回空列表
        return new VersionAnnouncementDataModel { Content = new List<VersionAnnouncementContentModel>() };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var release = _EnsureCache();
        var asset = _FindAsset(release);
        if (asset is null) throw new NullReferenceException("该 Release 中没有可用的启动器文件！");

        var loaders = new List<ModLoader.LoaderBase>();
        var tempPath = $@"{ModBase.pathTemp}Cache\Update\Download\{asset.Id}.bin";

        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(
            Lang.Text("Update.Task.GetVersionInfo"), load =>
            {
                load.output = new List<DownloadFile>
                {
                    new(new[] { asset.BrowserDownloadUrl }, tempPath)
                };
            }));

        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), new List<DownloadFile>()));

        loaders.Add(new ModLoader.LoaderTask<string, int>(
            Lang.Text("Update.Task.ApplyFile"), _ =>
            {
                // 分支说明：下载目标就是正在运行的 exe，无法直接覆盖，
                // 因此先落到同目录下的 .new 文件，由 UpdateRestart 在退出前替换。
                var pending = output + ".new";
                File.Copy(tempPath, pending, true);
                ModBase.Log($"[Update] 新版本已下载到 {pending}，等待重启替换");
            }));

        return loaders;
    }

    private GitHubRelease _EnsureCache()
    {
        if (_cache is null) RefreshCache();
        return _cache!;
    }

    /// <summary>
    ///     从 Release 的 Assets 中挑选 exe。
    /// </summary>
    private static GitHubAsset? _FindAsset(GitHubRelease release)
    {
        var assets = release.Assets;
        if (assets is null || assets.Count == 0) return null;
        return assets.FirstOrDefault(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
               ?? assets.FirstOrDefault();
    }

    /// <summary>
    ///     把 "1.2.3" 这种标签解析成数字版本号，失败时返回 0。
    /// </summary>
    private static int _ParseVersionCode(string versionName)
    {
        var digits = new string(versionName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits.Length > 9 ? digits[..9] : digits, out var code) ? code : 0;
    }

    #region GitHub API 模型

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("body")] public string Body { get; set; }

        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("id")] public long Id { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("size")] public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; }
    }

    #endregion
}
