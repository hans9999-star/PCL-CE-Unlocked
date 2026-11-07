using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PCL.Core.Utils;

namespace PCL;

/// <summary>内容类型：mod（模组）或 plugin（插件）。</summary>
public enum ContentKind
{
    Mod,
    Plugin
}

/// <summary>本地内容文件。</summary>
public sealed class ServerContentFile
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required long Size { get; init; }
}

/// <summary>Modrinth 搜索结果。</summary>
public sealed class ModrinthHit
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required long Downloads { get; init; }
}

/// <summary>
///     服务端内容（mod / 插件）管理：本地文件增删启停 + 在线搜索下载。<br/>
///     分支说明：原版开服页只负责把服务端跑起来，没有任何内容管理。<br/>
///     在线源只用 Modrinth —— CurseForge 需要 API Key，本构建没有内置。
/// </summary>
public static class ModServerContent
{
    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string UserAgent = "PCL-CE-Unlocked/1.0.3 (github.com/hans9999-star/PCL-CE-Unlocked)";

    /// <summary>本地内容目录：mod 端为 mods/，插件端为 plugins/。</summary>
    public static string GetContentDir(ServerSoftware software, string version, ContentKind kind)
    {
        var root = ModServerHost.GetServerDir(software, version);
        return Path.Combine(root, kind == ContentKind.Plugin ? "plugins" : "mods");
    }

    /// <summary>该服务端类型下管理的是哪类内容。</summary>
    public static ContentKind KindOf(ServerSoftware software) =>
        software == ServerSoftware.Paper ? ContentKind.Plugin : ContentKind.Mod;

    /// <summary>Modrinth 的 loader 标识。</summary>
    public static string LoaderOf(ServerSoftware software) => software switch
    {
        ServerSoftware.Paper => "paper",
        ServerSoftware.Fabric => "fabric",
        ServerSoftware.Forge => "forge",
        ServerSoftware.NeoForge => "neoforge",
        _ => "minecraft"
    };

    #region 本地管理

    /// <summary>列出本地已安装内容（.jar 与 .jar.disabled 都算）。</summary>
    public static List<ServerContentFile> ListLocal(string dir)
    {
        var list = new List<ServerContentFile>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(path);
                var isDisabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                // 只收 jar（含被停用的 .jar.disabled）
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(new ServerContentFile
                {
                    Path = path,
                    Name = isDisabled ? name[..^".disabled".Length] : name,
                    Enabled = !isDisabled,
                    Size = new FileInfo(path).Length
                });
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerContent] 列出本地内容失败", ModBase.LogLevel.Hint);
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>启用/停用：改扩展名（.jar ↔ .jar.disabled）。</summary>
    public static bool SetEnabled(ServerContentFile file, bool enabled)
    {
        try
        {
            var target = enabled
                ? file.Path
                : file.Path + ".disabled";
            var source = enabled
                ? file.Path + ".disabled"
                : file.Path;
            if (!File.Exists(source)) return false;
            if (File.Exists(target)) File.Delete(target);
            File.Move(source, target);
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerContent] 切换启用状态失败", ModBase.LogLevel.Hint);
            return false;
        }
    }

    /// <summary>删除本地内容。</summary>
    public static bool Delete(ServerContentFile file)
    {
        try
        {
            if (File.Exists(file.Path)) File.Delete(file.Path);
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerContent] 删除内容失败", ModBase.LogLevel.Hint);
            return false;
        }
    }

    #endregion

    #region 在线（Modrinth）

    /// <summary>
    ///     在 Modrinth 上搜索内容，按「项目类型 + 加载器 + 游戏版本」过滤。
    /// </summary>
    public static async Task<List<ModrinthHit>> SearchAsync(
        string query, ContentKind kind, string loader, string gameVersion, int limit = 20)
    {
        var result = new List<ModrinthHit>();
        try
        {
            var type = kind == ContentKind.Plugin ? "plugin" : "mod";
            var facets = $"[[" + $"\"project_type:{type}\"],[\"categories:{loader}\"],[\"versions:{gameVersion}\"]]";
            var url = $"{ModrinthApi}/search?query={Uri.EscapeDataString(query)}&limit={limit}" +
                      $"&facets={Uri.EscapeDataString(facets)}";
            var json = await FetchJsonAsync(url).ConfigureAwait(false);
            if (json["hits"] is not JsonArray hits) return result;
            foreach (var h in hits)
            {
                var slug = h?["slug"]?.ToString();
                if (string.IsNullOrWhiteSpace(slug)) continue;
                result.Add(new ModrinthHit
                {
                    Slug = slug,
                    Title = h["title"]?.ToString() ?? slug,
                    Description = h["description"]?.ToString() ?? "",
                    Downloads = (long?)h["downloads"] ?? 0
                });
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerContent] Modrinth 搜索失败", ModBase.LogLevel.Hint);
        }

        return result;
    }

    /// <summary>
    ///     下载某个项目适配当前加载器与游戏版本的 jar 到内容目录。
    /// </summary>
    /// <returns>成功时返回文件名。</returns>
    public static async Task<string?> InstallAsync(
        string slug, string loader, string gameVersion, string destDir)
    {
        try
        {
            var url = $"{ModrinthApi}/project/{Uri.EscapeDataString(slug)}/version" +
                      $"?loaders={Uri.EscapeDataString($"[\"{loader}\"]")}" +
                      $"&game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}";
            var versions = await FetchJsonAsync(url).ConfigureAwait(false);
            if (versions is not JsonArray arr || arr.Count == 0) return null;

            var files = arr[0]?["files"];
            if (files is not JsonArray fileArr || fileArr.Count == 0) return null;

            // 优先取标记为 primary 的文件
            JsonNode? chosen = null;
            foreach (var f in fileArr)
                if (f?["primary"]?.GetValue<bool>() == true)
                {
                    chosen = f;
                    break;
                }

            chosen ??= fileArr[0];
            var fileUrl = chosen?["url"]?.ToString();
            var fileName = chosen?["filename"]?.ToString();
            if (string.IsNullOrWhiteSpace(fileUrl) || string.IsNullOrWhiteSpace(fileName)) return null;

            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, fileName);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            var bytes = await http.GetByteArrayAsync(fileUrl).ConfigureAwait(false);
            await File.WriteAllBytesAsync(dest, bytes).ConfigureAwait(false);
            ModBase.Log($"[ServerContent] 已安装 {fileName}（{bytes.Length / 1024} KB）");
            return fileName;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerContent] 下载内容失败", ModBase.LogLevel.Hint);
            return null;
        }
    }

    private static async Task<JsonNode> FetchJsonAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        var text = await http.GetStringAsync(url).ConfigureAwait(false);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"无法解析 {url}");
    }

    #endregion
}
