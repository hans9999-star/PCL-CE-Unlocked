using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     一个启动器主题：亮/暗两套配色 + 标题图 + 背景图。<br/>
///     分支说明：原版自定义主题只有一套色相/饱和度/明度，且无法保存分享；
///     这里把「配色 + 两张图」打包成一个可保存、可导出、可导入的实体。
/// </summary>
public sealed class LauncherTheme
{
    public required string Name { get; init; }
    public required string Dir { get; init; }

    public int LightHue { get; init; } = 180;
    public int LightSat { get; init; } = 80;
    public int LightLightness { get; init; } = 20;

    public int DarkHue { get; init; } = 180;
    public int DarkSat { get; init; } = 80;
    public int DarkLightness { get; init; } = 20;

    /// <summary>标题图绝对路径，可空。</summary>
    public string? TitleImagePath { get; init; }

    /// <summary>背景图绝对路径，可空。</summary>
    public string? BackgroundImagePath { get; init; }
}

/// <summary>
///     主题文件的读写：一个主题 = 一个目录，内含 theme.json 与可选的图片。<br/>
///     目录：{启动器目录}PCL/Theme/{主题名}/
/// </summary>
public static class ModThemeFile
{
    private const string JsonName = "theme.json";
    private const string TitleName = "title";
    private const string BackgroundName = "background";

    /// <summary>主题根目录。</summary>
    public static string ThemeRoot => Path.Combine(ModBase.exePath, "PCL", "Theme");

    #region 读取

    /// <summary>扫描主题根目录，解析全部主题。坏主题跳过并记日志，不抛异常。</summary>
    public static List<LauncherTheme> ListThemes()
    {
        var list = new List<LauncherTheme>();
        try
        {
            if (!Directory.Exists(ThemeRoot)) return list;
            foreach (var dir in Directory.EnumerateDirectories(ThemeRoot))
            {
                var parsed = ParseTheme(dir);
                if (parsed is not null) list.Add(parsed);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Theme] 扫描主题目录失败", ModBase.LogLevel.Hint);
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static LauncherTheme? ParseTheme(string dir)
    {
        var jsonPath = Path.Combine(dir, JsonName);
        if (!File.Exists(jsonPath)) return null;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)) is not JsonObject obj) return null;

            var name = obj["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(dir);

            var light = obj["light"] as JsonObject;
            var dark = obj["dark"] as JsonObject;

            string? Resolve(string? fileName)
            {
                if (string.IsNullOrWhiteSpace(fileName)) return null;
                var full = Path.Combine(dir, fileName);
                return File.Exists(full) ? full : null;
            }

            return new LauncherTheme
            {
                Name = name!,
                Dir = dir,
                LightHue = (int?)light?["hue"] ?? 180,
                LightSat = (int?)light?["sat"] ?? 80,
                LightLightness = (int?)light?["lightness"] ?? 20,
                DarkHue = (int?)dark?["hue"] ?? 180,
                DarkSat = (int?)dark?["sat"] ?? 80,
                DarkLightness = (int?)dark?["lightness"] ?? 20,
                TitleImagePath = Resolve(obj["titleImage"]?.ToString()),
                BackgroundImagePath = Resolve(obj["backgroundImage"]?.ToString())
            };
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Theme] 主题解析失败，已跳过：{dir}", ModBase.LogLevel.Hint);
            return null;
        }
    }

    #endregion

    #region 写入

    /// <summary>
    ///     把当前设置保存成一个新主题。两张图会被复制进主题目录，保证主题可分享。
    /// </summary>
    public static LauncherTheme? SaveCurrentAsTheme(string name, string? titleImage, string? backgroundImage)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var safe = Sanitize(name);
        if (string.IsNullOrWhiteSpace(safe)) return null;

        try
        {
            var dir = UniqueDir(safe);
            Directory.CreateDirectory(dir);

            var titleFile = CopyInto(dir, titleImage, TitleName);
            var bgFile = CopyInto(dir, backgroundImage, BackgroundName);

            var theme = Config.Preference.Theme;
            var obj = new JsonObject
            {
                ["version"] = 1,
                ["name"] = name,
                ["light"] = new JsonObject
                {
                    ["hue"] = theme.LightHue,
                    ["sat"] = theme.LightSat,
                    ["lightness"] = theme.LightLightness
                },
                ["dark"] = new JsonObject
                {
                    ["hue"] = theme.DarkHue,
                    ["sat"] = theme.DarkSat,
                    ["lightness"] = theme.DarkLightness
                },
                ["titleImage"] = titleFile,
                ["backgroundImage"] = bgFile
            };

            File.WriteAllText(Path.Combine(dir, JsonName), obj.ToJsonString(), Encoding.UTF8);
            ModBase.Log($"[Theme] 已保存主题：{name} -> {dir}");
            return ParseTheme(dir);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Theme] 保存主题失败：{name}", ModBase.LogLevel.Hint);
            return null;
        }
    }

    /// <summary>把主题套用到当前设置。</summary>
    public static bool ApplyTheme(LauncherTheme theme)
    {
        try
        {
            var cfg = Config.Preference.Theme;
            cfg.LightHue = theme.LightHue;
            cfg.LightSat = theme.LightSat;
            cfg.LightLightness = theme.LightLightness;
            cfg.DarkHue = theme.DarkHue;
            cfg.DarkSat = theme.DarkSat;
            cfg.DarkLightness = theme.DarkLightness;
            cfg.CustomTitleImage = theme.TitleImagePath ?? "";
            cfg.CustomBackgroundImage = theme.BackgroundImagePath ?? "";
            cfg.ActiveTheme = theme.Name;

            ThemeManager.ThemeRefresh();
            ModBase.Log($"[Theme] 已套用主题：{theme.Name}");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Theme] 套用主题失败：{theme.Name}", ModBase.LogLevel.Hint);
            return false;
        }
    }

    /// <summary>删除主题目录。</summary>
    public static bool DeleteTheme(LauncherTheme theme)
    {
        try
        {
            if (Directory.Exists(theme.Dir)) Directory.Delete(theme.Dir, true);
            if (Config.Preference.Theme.ActiveTheme == theme.Name) ClearActiveTheme();
            ModBase.Log($"[Theme] 已删除主题：{theme.Name}");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Theme] 删除主题失败：{theme.Name}", ModBase.LogLevel.Hint);
            return false;
        }
    }

    /// <summary>清空「当前主题」标记与两张自定义图，配色值保留。</summary>
    public static void ClearActiveTheme()
    {
        try
        {
            var cfg = Config.Preference.Theme;
            cfg.ActiveTheme = "";
            cfg.CustomTitleImage = "";
            cfg.CustomBackgroundImage = "";
            ThemeManager.ThemeRefresh();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Theme] 清除当前主题失败", ModBase.LogLevel.Hint);
        }
    }

    #endregion

    #region 导入导出

    /// <summary>把整个主题目录打包成 zip，便于分享。</summary>
    public static bool ExportTheme(LauncherTheme theme, string zipPath)
    {
        try
        {
            if (!Directory.Exists(theme.Dir)) return false;
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(theme.Dir, zipPath, CompressionLevel.Optimal, false);
            ModBase.Log($"[Theme] 已导出主题：{theme.Name} -> {zipPath}");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Theme] 导出主题失败：{theme.Name}", ModBase.LogLevel.Hint);
            return false;
        }
    }

    /// <summary>从 zip 导入主题，重名自动加后缀。</summary>
    public static bool ImportTheme(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath)) return false;
            Directory.CreateDirectory(ThemeRoot);

            // 以 zip 文件名作为主题目录名
            var baseName = Sanitize(Path.GetFileNameWithoutExtension(zipPath));
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Theme";
            var dir = UniqueDir(baseName);

            ZipFile.ExtractToDirectory(zipPath, dir, true);

            // 兼容压缩包里还套了一层目录的情况
            if (!File.Exists(Path.Combine(dir, JsonName)))
            {
                var nested = Directory.EnumerateDirectories(dir)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, JsonName)));
                if (nested is null)
                {
                    Directory.Delete(dir, true);
                    ModBase.Log($"[Theme] 压缩包里没有 {JsonName}：{zipPath}", ModBase.LogLevel.Hint);
                    return false;
                }

                foreach (var file in Directory.EnumerateFiles(nested))
                    File.Move(file, Path.Combine(dir, Path.GetFileName(file)), true);
                foreach (var sub in Directory.EnumerateDirectories(nested))
                    Directory.Move(sub, Path.Combine(dir, Path.GetFileName(sub)));
                Directory.Delete(nested, true);
            }

            ModBase.Log($"[Theme] 已导入主题：{zipPath} -> {dir}");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Theme] 导入主题失败：{zipPath}", ModBase.LogLevel.Hint);
            return false;
        }
    }

    #endregion

    #region 工具

    /// <summary>去掉文件名里的非法字符。</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.');
    }

    /// <summary>取一个不冲突的目录名。</summary>
    private static string UniqueDir(string baseName)
    {
        var dir = Path.Combine(ThemeRoot, baseName);
        var index = 2;
        while (Directory.Exists(dir))
            dir = Path.Combine(ThemeRoot, $"{baseName}-{index++}");
        return dir;
    }

    /// <summary>把外部图片复制进主题目录，返回相对文件名；源不存在时返回 null。</summary>
    private static string? CopyInto(string dir, string? source, string baseName)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return null;
        var ext = Path.GetExtension(source);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        var fileName = baseName + ext;
        File.Copy(source, Path.Combine(dir, fileName), true);
        return fileName;
    }

    #endregion
}
