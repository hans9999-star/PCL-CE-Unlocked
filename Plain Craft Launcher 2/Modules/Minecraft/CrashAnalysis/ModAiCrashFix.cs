using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL;

/// <summary>
///     一条 AI 提出的修复操作。
/// </summary>
internal sealed class AiGameFix
{
    /// <summary>操作类型：delete_mod / set_property / write_file。</summary>
    public required string Op { get; init; }

    /// <summary>目标文件（相对实例目录或绝对路径）。</summary>
    public string? Path { get; init; }

    /// <summary>属性键（set_property 用，如 max-tick-time）。</summary>
    public string? Key { get; init; }

    /// <summary>新值（set_property / write_file 用）。</summary>
    public string? Value { get; init; }

    /// <summary>给用户看的理由。</summary>
    public string? Reason { get; init; }

    /// <summary>校验失败时的原因。</summary>
    public string? Error { get; set; }

    /// <summary>解析出来的绝对路径（执行时填充）。</summary>
    public string? FullPath { get; set; }
}

/// <summary>
///     从 AI 的分析回复里解析出可执行的修复操作，并负责执行。<br/>
///     分支说明：AI 只在回复里输出约定的 JSON 代码块，本类负责解析、校验、执行。
///     一律要求用户确认后才执行，且执行前自动备份。
/// </summary>
internal static partial class ModAiCrashFix
{
    /// <summary>允许被删除的文件扩展名（仅限模组/资源包这类可重下的内容）。</summary>
    private static readonly string[] DeletableExtensions = [".jar", ".jar.disabled", ".zip", ".disabled"];

    /// <summary>允许写入的文本类扩展名。</summary>
    private static readonly string[] WritableExtensions =
        [".txt", ".properties", ".cfg", ".toml", ".json", ".json5", ".yaml", ".yml", ".ini", ".conf"];

    #region 解析

    /// <summary>
    ///     从回复里抽取 JSON 代码块，解析成修复操作列表。<br/>
    ///     <paramref name="cleanText" /> 返回去掉 JSON 块之后的纯文本，用于展示。
    /// </summary>
    public static List<AiGameFix> Parse(string reply, McInstance? instance, out string cleanText)
    {
        cleanText = reply;
        var fixes = new List<AiGameFix>();

        try
        {
            var matches = JsonFenceRegex().Matches(reply);
            if (matches.Count == 0) return fixes;

            foreach (Match m in matches)
            {
                var json = TrailingCommaRegex().Replace(m.Groups["json"].Value.Trim(), "$1");

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(json);
                }
                catch
                {
                    ModBase.Log($"[AI] 修复指令 JSON 解析失败：{json[..Math.Min(120, json.Length)]}",
                        ModBase.LogLevel.Developer);
                    continue;
                }

                var items = node is JsonArray array ? array.ToList() : [node];
                foreach (var item in items)
                {
                    var fix = ToFix(item as JsonObject);
                    if (fix is null) continue;
                    Validate(fix, instance);
                    fixes.Add(fix);
                }

                cleanText = cleanText.Replace(m.Value, "").Trim();
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[AI] 解析修复指令失败", ModBase.LogLevel.Developer);
        }

        // 一次最多 5 条，避免 AI 大改
        if (fixes.Count > 5)
        {
            ModBase.Log($"[AI] 修复指令 {fixes.Count} 条，超过上限 5，已截断");
            fixes = fixes.Take(5).ToList();
        }

        return fixes;
    }

    private static AiGameFix? ToFix(JsonObject? obj)
    {
        if (obj is null) return null;
        var op = obj["op"]?.ToString()?.Trim().ToLowerInvariant();
        return op switch
        {
            "delete_mod" => new AiGameFix
            {
                Op = op,
                Path = obj["path"]?.ToString(),
                Reason = obj["reason"]?.ToString()
            },
            "set_property" => new AiGameFix
            {
                Op = op,
                Path = obj["path"]?.ToString(),
                Key = obj["key"]?.ToString(),
                Value = obj["value"]?.ToString(),
                Reason = obj["reason"]?.ToString()
            },
            "write_file" => new AiGameFix
            {
                Op = op,
                Path = obj["path"]?.ToString(),
                Value = obj["value"]?.ToString(),
                Reason = obj["reason"]?.ToString()
            },
            _ => null
        };
    }

    /// <summary>
    ///     校验并解析目标路径。不合格的写入 <see cref="AiGameFix.Error" />。
    /// </summary>
    private static void Validate(AiGameFix fix, McInstance? instance)
    {
        if (instance is null)
        {
            fix.Error = "当前没有关联的游戏实例，无法执行修复";
            return;
        }

        string instanceDir;
        try
        {
            instanceDir = Path.GetFullPath(instance.PathIndie);
        }
        catch (Exception ex)
        {
            fix.Error = "无法获取实例目录：" + ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(fix.Path))
        {
            fix.Error = "缺少目标路径";
            return;
        }

        if (fix.Path.Contains("..", StringComparison.Ordinal))
        {
            fix.Error = "路径中含 ..，已拒绝";
            return;
        }

        string full;
        try
        {
            full = Path.IsPathRooted(fix.Path)
                ? Path.GetFullPath(fix.Path)
                : Path.GetFullPath(Path.Combine(instanceDir, fix.Path));
        }
        catch (Exception ex)
        {
            fix.Error = "路径无效：" + ex.Message;
            return;
        }

        // 必须落在实例目录内
        var root = instanceDir.TrimEnd('\\') + "\\";
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            fix.Error = "目标不在当前实例目录内，已拒绝";
            return;
        }

        fix.FullPath = full;

        var ext = Path.GetExtension(full).ToLowerInvariant();
        switch (fix.Op)
        {
            case "delete_mod":
                if (!DeletableExtensions.Contains(ext) && !full.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    fix.Error = $"不允许删除 {ext} 类型的文件";
                else if (!File.Exists(full))
                    fix.Error = "文件不存在";
                break;

            case "write_file":
                if (!WritableExtensions.Contains(ext))
                    fix.Error = $"不允许写入 {ext} 类型的文件";
                break;

            case "set_property":
                if (string.IsNullOrWhiteSpace(fix.Key)) fix.Error = "缺少属性键";
                else if (!WritableExtensions.Contains(ext) && !full.EndsWith("server.properties", StringComparison.OrdinalIgnoreCase))
                    fix.Error = $"不允许修改 {ext} 类型的文件";
                break;
        }
    }

    #endregion

    #region 执行

    /// <summary>执行一条修复。返回错误说明，成功返回 null。</summary>
    public static string? Execute(AiGameFix fix)
    {
        if (fix.Error is not null) return fix.Error;
        if (fix.FullPath is null) return "路径未解析";

        try
        {
            switch (fix.Op)
            {
                case "delete_mod":
                    Backup(fix.FullPath);
                    File.Delete(fix.FullPath);
                    ModBase.Log($"[AI] 已删除（已备份）：{fix.FullPath}");
                    return null;

                case "write_file":
                    Backup(fix.FullPath);
                    var dir = Path.GetDirectoryName(fix.FullPath);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(fix.FullPath, fix.Value ?? "", new UTF8Encoding(false));
                    ModBase.Log($"[AI] 已写入：{fix.FullPath}");
                    return null;

                case "set_property":
                    return SetProperty(fix.FullPath, fix.Key!, fix.Value ?? "");

                default:
                    return "不支持的操作";
            }
        }
        catch (Exception ex)
        {
            return $"执行失败：{ex.Message}";
        }
    }

    /// <summary>
    ///     修改 properties / conf 里某个键的值；键不存在则追加到末尾。
    /// </summary>
    private static string? SetProperty(string fullPath, string key, string value)
    {
        var exists = File.Exists(fullPath);
        var lines = exists ? File.ReadAllLines(fullPath).ToList() : [];
        Backup(fullPath);

        var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*[:=]", RegexOptions.IgnoreCase);
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!pattern.IsMatch(lines[i])) continue;
            // properties 用 =，其他用 :，沿用原来的分隔符
            var sep = lines[i].Contains('=') ? "=" : ":";
            lines[i] = $"{key}{sep}{value}";
            replaced = true;
            break;
        }

        if (!replaced) lines.Add($"{key}={value}");

        File.WriteAllLines(fullPath, lines, new UTF8Encoding(false));
        ModBase.Log($"[AI] 已修改属性 {key}={value}（{fullPath}）");
        return null;
    }

    /// <summary>把原文件复制到 PCL/AiBackup/{时间戳}/，保留最近 20 份。</summary>
    private static void Backup(string fullPath)
    {
        if (!File.Exists(fullPath)) return;
        try
        {
            var root = Path.Combine(ModBase.exePath, "PCL", "AiBackup");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var target = Path.Combine(root, stamp, Path.GetFileName(fullPath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(fullPath, target, true);

            foreach (var d in new DirectoryInfo(root).GetDirectories()
                         .OrderByDescending(x => x.CreationTime).Skip(20))
                try
                {
                    d.Delete(true);
                }
                catch
                {
                    // 清理失败不影响主流程
                }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[AI] 备份失败", ModBase.LogLevel.Developer);
        }
    }

    #endregion

    /// <summary>把修复列表转成给用户看的描述文字。</summary>
    public static string Describe(IReadOnlyList<AiGameFix> fixes)
    {
        var sb = new StringBuilder();
        foreach (var fix in fixes)
        {
            var name = fix.FullPath is null ? fix.Path : Path.GetFileName(fix.FullPath);
            sb.Append(fix.Op switch
            {
                "delete_mod" => $"删除 {name}",
                "set_property" => $"修改 {name}：{fix.Key} = {fix.Value}",
                "write_file" => $"写入 {name}",
                _ => $"{fix.Op} {name}"
            });

            if (!string.IsNullOrWhiteSpace(fix.Reason)) sb.Append($"（{fix.Reason}）");
            if (fix.Error is not null) sb.Append($"  ⚠ {fix.Error}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"```(?:json)?\s*(?<json>[\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex JsonFenceRegex();

    [GeneratedRegex(@",(\s*[\]}])")]
    private static partial Regex TrailingCommaRegex();
}
