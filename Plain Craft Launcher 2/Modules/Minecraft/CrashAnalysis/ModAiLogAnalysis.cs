using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     分支说明：把崩溃日志交给 OpenAI 兼容的接口再分析一次。<br/>
///     只在启动器自带的分析给不出结论时才用得上——内置规则是查表式的，
///     遇到没收录过的崩溃就只剩「未知错误」，这时候模型更容易看出线索。
/// </summary>
internal static class ModAiLogAnalysis
{
    /// <summary>日志太长会把上下文撑爆，只取尾部这么多字符（崩溃原因几乎总在末尾）。</summary>
    private const int MaxLogChars = 60000;

    private const string SystemPrompt =
        "你是 Minecraft 崩溃日志分析专家。用户会给你一份崩溃日志（可能包含游戏日志、" +
        "调试日志、crash-report 与 hs_err）。请：\n" +
        "1. 先用一两句话说明最可能的崩溃原因；\n" +
        "2. 指出日志里支持这个判断的关键行（原样引用）；\n" +
        "3. 给出可操作的处理步骤，按优先级排列；\n" +
        "4. 如果日志信息不足以判断，明确说不确定，并指出还需要哪些信息。\n" +
        "不要编造日志里没有的内容。用简体中文回答，不要用 Markdown 标题符号。";

    private static string Env(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string Endpoint => Env("PCL_AI_ENDPOINT", Config.Preference.Ai.Endpoint).TrimEnd('/');

    private static string Model => Env("PCL_AI_MODEL", Config.Preference.Ai.Model);

    private static string ApiKey => Env("PCL_AI_KEY", Config.Preference.Ai.ApiKey);

    /// <summary>
    ///     是否无条件显示「让 AI 分析」。默认关；可用 PCL_AI_ALWAYS=1 打开，用于验证。
    /// </summary>
    public static bool AlwaysShow =>
        Config.Preference.Ai.AlwaysShow || Env("PCL_AI_ALWAYS", "") == "1";

    /// <summary>是否已启用且有可用地址。</summary>
    public static bool IsAvailable =>
        Config.Preference.Ai.Enabled && !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>
    ///     把日志发给 AI，返回其回答文本。
    /// </summary>
    /// <exception cref="InvalidOperationException">未配置或返回值无法解析。</exception>
    public static async Task<string> AnalyzeAsync(string logText, CancellationToken token = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("AI 分析未启用或未配置地址");

        var log = logText ?? "";
        if (log.Length > MaxLogChars) log = "……（前文已省略）……\n" + log[^MaxLogChars..];
        if (string.IsNullOrWhiteSpace(log)) throw new InvalidOperationException("没有可分析的日志内容");

        var payload = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0.3,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = log }
            }
        };

        var timeout = Math.Clamp(Config.Preference.Ai.TimeoutSeconds, 10, 600);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);

        ModBase.Log($"[AI] 请求 {Endpoint}/chat/completions, 模型 {Model}, 日志 {log.Length} 字符");
        var startedAt = DateTime.Now;
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(Endpoint + "/chat/completions", content, token)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        ModBase.Log($"[AI] 响应 HTTP {(int)response.StatusCode}, {(int)(DateTime.Now - startedAt).TotalMilliseconds} ms, {body.Length} 字符");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{Trim(body)}");

        var json = JsonNode.Parse(body) as JsonObject;
        var text = json?["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("返回值里没有 choices[0].message.content：" + Trim(body));

        return text.Trim();
    }

    private static string Trim(string value) =>
        value.Length <= 400 ? value : value[..400] + "……";
}