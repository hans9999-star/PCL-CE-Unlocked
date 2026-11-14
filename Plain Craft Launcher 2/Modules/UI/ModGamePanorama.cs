using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCL;

/// <summary>
///     按当前游玩版本加载游戏主菜单的全景背景，并构造成可旋转的立方体天空盒。<br/>
///     分支说明：全景图随版本变化，所以不能构建时打包，必须运行时解析。<br/>
///     两条来源路径（1.13 是分水岭）：<br/>
///     - <b>1.13 及以后</b>：贴图进了资源索引，读 assets/indexes/{id}.json 取哈希，
///       再从 assets/objects/{hash前两位}/{hash} 读原图。<br/>
///     - <b>1.12 及以前</b>：贴图还在版本 jar 里，索引里只有声音与语言文件，
///       需要直接读 {PathInstance}{Name}.jar 内的条目，解压到缓存目录再加载。<br/>
///     面序经地缘像素匹配确认：0→1→2→3 环形相邻、右缘接左缘；4 为顶、5 为底。
/// </summary>
internal static class ModGamePanorama
{
    /// <summary>全景图路径片段。</summary>
    private const string PanoramaEntrySuffix = "gui/title/background/panorama_";

    /// <summary>立方体面数。少一张就不足以拼成天空盒。</summary>
    private const int FaceCount = 6;

    private static string? _cachedIndexName;
    private static List<string>? _cachedPaths;

    #region 路径解析

    /// <summary>取当前实例对应版本的全景图文件路径（需要 6 张）。</summary>
    private static IReadOnlyList<string> GetPanoramaPaths()
    {
        try
        {
            var instance = ResolveInstance();
            if (instance is null) return [];

            var indexName = ModAssets.McAssetsGetIndexName(instance);
            if (string.IsNullOrWhiteSpace(indexName)) return [];

            if (_cachedIndexName == indexName && _cachedPaths is not null) return _cachedPaths;

            var paths = TryFromAssetIndex(indexName);
            if (paths.Count < FaceCount)
            {
                ModBase.Log($"[Overlay] 版本 {indexName} 的索引里全景图不足 {FaceCount} 张，改从版本 jar 中查找",
                    ModBase.LogLevel.Debug);
                paths = TryFromVersionJar(instance, indexName);
            }

            if (paths.Count < FaceCount)
            {
                ModBase.Log($"[Overlay] 版本 {indexName} 没有完整的全景图，回退内置背景", ModBase.LogLevel.Debug);
                return [];
            }

            _cachedIndexName = indexName;
            _cachedPaths = paths;
            ModBase.Log($"[Overlay] 版本 {indexName} 的全景图：{paths.Count} 张");
            return paths;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 解析全景图失败", ModBase.LogLevel.Developer);
            return [];
        }
    }

    /// <summary>从资源索引解析（1.13 及以后）。</summary>
    private static List<string> TryFromAssetIndex(string indexName)
    {
        var paths = new List<string>();
        try
        {
            var indexFile = Path.Combine(ModFolder.mcFolderSelected, "assets", "indexes", indexName + ".json");
            if (!File.Exists(indexFile)) return paths;

            var json = JsonNode.Parse(File.ReadAllText(indexFile)) as JsonObject;
            var objects = json?["objects"] as JsonObject;
            if (objects is null) return paths;

            var objectsRoot = Path.Combine(ModFolder.mcFolderSelected, "assets", "objects");
            for (var i = 0; i < FaceCount; i++)
            {
                var hash = objects[$"minecraft/textures/gui/title/background/panorama_{i}.png"]?["hash"]?.ToString();
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;

                var file = Path.Combine(objectsRoot, hash[..2], hash);
                if (File.Exists(file)) paths.Add(file);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 从资源索引解析全景图失败", ModBase.LogLevel.Developer);
        }

        return paths;
    }

    /// <summary>从版本 jar 解析（1.12 及以前），解压到缓存目录后返回文件路径。</summary>
    private static List<string> TryFromVersionJar(McInstance instance, string indexName)
    {
        var paths = new List<string>();
        try
        {
            foreach (var jar in CandidateJars(instance))
            {
                if (!File.Exists(jar)) continue;

                using var zip = ZipFile.OpenRead(jar);
                var found = new List<(int Index, ZipArchiveEntry Entry)>();
                for (var i = 0; i < FaceCount; i++)
                {
                    var suffix = PanoramaEntrySuffix + i + ".png";
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                    if (entry is not null) found.Add((i, entry));
                }

                if (found.Count < FaceCount) continue;

                var cacheDir = Path.Combine(ModBase.exePath, "PCL", "PanoramaCache", indexName);
                Directory.CreateDirectory(cacheDir);

                foreach (var (index, entry) in found)
                {
                    var outPath = Path.Combine(cacheDir, $"pano_{index}.png");
                    if (!File.Exists(outPath) || new FileInfo(outPath).Length != entry.Length)
                        entry.ExtractToFile(outPath, true);
                    paths.Add(outPath);
                }

                ModBase.Log($"[Overlay] 从 {Path.GetFileName(jar)} 解压出 {paths.Count} 张全景图");
                break;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 从版本 jar 解析全景图失败", ModBase.LogLevel.Developer);
        }

        return paths;
    }

    /// <summary>版本 jar 候选路径：先本体，再跟随继承链。</summary>
    private static IEnumerable<string> CandidateJars(McInstance instance)
    {
        yield return Path.Combine(instance.PathInstance, instance.Name + ".jar");

        var inherit = instance.InheritInstanceName;
        if (string.IsNullOrWhiteSpace(inherit)) yield break;

        yield return Path.Combine(instance.PathInstance, inherit + ".jar");
        yield return Path.Combine(ModFolder.mcFolderSelected, "versions", inherit, inherit + ".jar");
    }

    #endregion

    /// <summary>
    ///     随机挑一张全景图并解码。<br/>
    ///     分支说明：只显示一张静态图，不做立方体旋转 —— 旋转版本已按要求移除。
    ///     解码放在后台线程，避免阻塞覆盖层出现。
    /// </summary>
    /// <returns>解码失败或没有全景图时返回 null。</returns>
    public static async Task<BitmapSource?> LoadRandomAsync()
    {
        var paths = GetPanoramaPaths();
        if (paths.Count == 0) return null;

        var path = paths[Random.Shared.Next(paths.Count)];

        try
        {
            return await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 立即解码，避免文件被占用
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = 1920;
                bitmap.EndInit();
                bitmap.Freeze();   // 冻结后即可跨线程赋值给 Image
                return (BitmapSource)bitmap;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 解码全景图失败", ModBase.LogLevel.Developer);
            return null;
        }
    }
    /// <summary>清空缓存（换实例后调用）。</summary>
    public static void Invalidate()
    {
        _cachedIndexName = null;
        _cachedPaths = null;
    }

    /// <summary>取当前实例：优先正在游玩的，其次选中的。</summary>
    private static McInstance? ResolveInstance()
    {
        try
        {
            var selected = ModInstanceList.McMcInstanceSelected;
            if (selected is not null) return selected;
        }
        catch
        {
            // 忽略
        }

        try
        {
            return PageInstanceLeft.McInstance;
        }
        catch
        {
            return null;
        }
    }
}
