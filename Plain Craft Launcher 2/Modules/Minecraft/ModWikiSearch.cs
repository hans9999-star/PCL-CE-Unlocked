using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using PCL.Core.Minecraft;

namespace PCL;

/// <summary>一条 wiki 搜索结果。</summary>
internal sealed class WikiSearchHit
{
    /// <summary>条目标题。</summary>
    public required string Title { get; init; }

    /// <summary>纯文本摘要（已去掉 HTML 标签）。</summary>
    public required string Snippet { get; init; }

    /// <summary>条目完整网址。</summary>
    public required string Url { get; init; }
}

/// <summary>正文块的类型。</summary>
internal enum WikiBlockKind
{
    Title,
    Heading2,
    Heading3,
    Paragraph,
    Bullet,
    Numbered
}

/// <summary>一条正文块。</summary>
internal sealed class WikiBlock
{
    public required WikiBlockKind Kind { get; init; }
    public required string Text { get; init; }
}

/// <summary>
///     Minecraft Wiki 查询。<br/>
///     分支说明：搜索走 opensearch 风格的 list=search；正文走 action=parse 取 HTML，
///     再由内部的轻量解析器拆成「标题/段落/列表」等结构化块 —— 这样能在
///     不引入 WebView2、不用浏览器的情况下显示较完整的正文。<br/>
///     站点前缀由 <see cref="McFormatter.GetWikiBaseUrl" /> 按界面语言决定。
/// </summary>
internal static partial class ModWikiSearch
{
    /// <summary>Minecraft Wiki 会拒绝没有 User-Agent 的请求（403），必须显式带上。</summary>
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static ModWikiSearch()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "PCL-Unbound/1.0 (Minecraft launcher; wiki search)");
    }

    /// <summary>当前语言对应的 wiki 首页。</summary>
    public static string BaseUrl => McFormatter.GetWikiBaseUrl();

    #region 搜索

    /// <summary>搜索 wiki 条目。</summary>
    public static async Task<List<WikiSearchHit>> SearchAsync(string keyword, int limit = 10,
        CancellationToken token = default)
    {
        var hits = new List<WikiSearchHit>();
        if (string.IsNullOrWhiteSpace(keyword)) return hits;

        var url = $"{BaseUrl}/api.php?action=query&list=search" +
                  $"&srsearch={Uri.EscapeDataString(keyword.Trim())}" +
                  $"&srlimit={Math.Clamp(limit, 1, 50)}&format=json&utf8=1";

        try
        {
            var body = await Http.GetStringAsync(url, token).ConfigureAwait(false);
            var json = JsonNode.Parse(body) as JsonObject;
            var search = json?["query"]?["search"] as JsonArray;
            if (search is null) return hits;

            foreach (var item in search)
            {
                var title = item?["title"]?.ToString();
                if (string.IsNullOrWhiteSpace(title)) continue;

                hits.Add(new WikiSearchHit
                {
                    Title = title,
                    Snippet = StripHtml(item?["snippet"]?.ToString() ?? ""),
                    Url = $"{BaseUrl}/w/{Uri.EscapeDataString(title.Replace(' ', '_'))}"
                });
            }

            ModBase.Log($"[Wiki] 搜索「{keyword}」得到 {hits.Count} 条结果");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Wiki] 搜索失败", ModBase.LogLevel.Developer);
            throw;
        }

        return hits;
    }

    #endregion

    #region 正文

    /// <summary>
    ///     取条目正文并解析成结构化块。<br/>
    ///     用 action=parse 而不是 extracts —— 后者只能给纯文本，标题层级与列表结构全丢。
    /// </summary>
    /// <param name="title">条目标题。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="maxBlocks">最多返回多少块，防止超长条目卡住界面。</param>
    public static async Task<List<WikiBlock>> GetArticleAsync(string title, CancellationToken token = default,
        int maxBlocks = 400)
    {
        var blocks = new List<WikiBlock>();
        if (string.IsNullOrWhiteSpace(title)) return blocks;

        // redirects=1 必须有：像「附魔」这类页是重定向，不加会拿到空正文
        var url = $"{BaseUrl}/api.php?action=parse&page={Uri.EscapeDataString(title)}" +
                  "&prop=text&format=json&formatversion=2&redirects=1&disableeditsection=1" +
                  "&disablelimitreport=1";

        try
        {
            var body = await Http.GetStringAsync(url, token).ConfigureAwait(false);
            var json = JsonNode.Parse(body) as JsonObject;

            if (json?["error"] is JsonObject error)
            {
                ModBase.Log($"[Wiki] 取正文失败：{error["info"]}", ModBase.LogLevel.Developer);
                return blocks;
            }

            var html = json?["parse"]?["text"]?.ToString();
            var realTitle = json?["parse"]?["title"]?.ToString() ?? title;
            if (string.IsNullOrWhiteSpace(html)) return blocks;

            blocks.Add(new WikiBlock { Kind = WikiBlockKind.Title, Text = realTitle });
            blocks.AddRange(ParseHtml(html, maxBlocks));

            ModBase.Log($"[Wiki] 「{realTitle}」解析出 {blocks.Count} 个内容块");
            return blocks;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Wiki] 获取正文异常：{title}", ModBase.LogLevel.Developer);
            return blocks;
        }
    }

    /// <summary>
    ///     把 wiki 的 HTML 拆成结构化块。<br/>
    ///     刻意做得很轻：只保留标题、段落、列表三类，图片与复杂表格丢弃 ——
    ///     它们在 WPF 里重绘代价极高，而纯文本阅读已经能覆盖绝大多数查询需求。
    /// </summary>
    private static List<WikiBlock> ParseHtml(string html, int maxBlocks)
    {
        var blocks = new List<WikiBlock>();

        // 1) 先剔除整块无用内容：样式、脚本、脚注上标、导航框、信息框、目录
        var cleaned = html;
        foreach (var pattern in new[]
                 {
                     @"<style[^>]*>[\s\S]*?</style>",
                     @"<script[^>]*>[\s\S]*?</script>",
                     @"<sup[^>]*class=""[^""]*reference[^""]*""[^>]*>[\s\S]*?</sup>",
                     @"<table[^>]*class=""[^""]*(navbox|infobox|ambox|metadata)[^""]*""[^>]*>[\s\S]*?</table>",
                     @"<div[^>]*class=""[^""]*(navbox|toc|hatnote|metadata|mw-editsection)[^""]*""[^>]*>[\s\S]*?</div>",
                     @"<span[^>]*class=""mw-editsection""[^>]*>[\s\S]*?</span>"
                 })
        {
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, pattern, "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // 2) 按顺序抓取标题 / 段落 / 列表项
        //    用单个正则一次扫描，保证块的出现顺序与原文一致
        var blockRegex = new System.Text.RegularExpressions.Regex(
            @"<h2[^>]*>([\s\S]*?)</h2>" +
            @"|<h3[^>]*>([\s\S]*?)</h3>" +
            @"|<p[^>]*>([\s\S]*?)</p>" +
            @"|<li[^>]*>([\s\S]*?)</li>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in blockRegex.Matches(cleaned))
        {
            if (blocks.Count >= maxBlocks) break;

            WikiBlockKind kind;
            string raw;

            if (match.Groups[1].Success)
            {
                kind = WikiBlockKind.Heading2;
                raw = match.Groups[1].Value;
            }
            else if (match.Groups[2].Success)
            {
                kind = WikiBlockKind.Heading3;
                raw = match.Groups[2].Value;
            }
            else if (match.Groups[3].Success)
            {
                kind = WikiBlockKind.Paragraph;
                raw = match.Groups[3].Value;
            }
            else
            {
                kind = WikiBlockKind.Bullet;
                raw = match.Groups[4].Value;
            }

            var text = StripHtml(raw);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // 标题里常带 [编辑] 尾巴，去掉
            text = text.Replace("[编辑]", "").Replace("[編輯]", "").Trim();
            if (text.Length == 0) continue;

            blocks.Add(new WikiBlock { Kind = kind, Text = text });
        }

        return blocks;
    }

    #endregion

    /// <summary>去掉 HTML 标签并还原实体。</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = TagRegex().Replace(html, "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex TagRegex();
}
