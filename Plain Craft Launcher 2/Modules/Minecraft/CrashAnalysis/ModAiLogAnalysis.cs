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
        "你是 Minecraft 崩溃日志分析专家。输入是一份崩溃日志（可能包含游戏日志、调试日志、" +
        "crash-report、hs_err）。请用简体中文回答。\n\n" +
        "【输出格式】（严格遵守，分两部分）\n\n" +
        "第一部分：文字分析。依次说明：\n" +
        "  1) 一两句话点明最可能的崩溃原因；\n" +
        "  2) 原样引用日志里支持该判断的关键行；\n" +
        "  3) 按优先级列出处理步骤。\n" +
        "不要编造日志里没有的内容。正文里不要使用 Markdown 标题符号（#）。\n\n" +
        "第二部分：【必须输出】一个 JSON 代码块，用 ```json 和 ``` 包裹，列出可自动执行的修复操作。\n" +
        "这是本任务的必需输出，不是可选项 —— 只要你的分析里提到了任何「删掉某文件」「改配置里某个值」" +
        "「替换某文件内容」之类的动作，就必须把它转成对应 JSON 条目写进代码块。\n" +
        "如果日志信息确实不足以定位原因，代码块写空数组 []，并在文字部分说明还需要哪些信息。\n\n" +
        "可用操作（path 一律使用相对于实例目录的路径，例如 mods/xxx.jar、options.txt）：\n\n" +
        "  delete_mod  删除模组/资源包文件（仅限 .jar / .zip / .jar.disabled）\n" +
        "    {\"op\":\"delete_mod\",\"path\":\"mods/xxx.jar\",\"reason\":\"与 yyy 不兼容\"}\n\n" +
        "  set_property  修改某个配置文件里指定键的值（该键不存在则追加到文件末尾）\n" +
        "    {\"op\":\"set_property\",\"path\":\"options.txt\",\"key\":\"max-tick-time\",\"value\":\"-1\",\"reason\":\"...\"}\n\n" +
        "  write_file  覆盖写入整个文本配置文件（value 必须给出完整内容，不是补丁片段）\n" +
        "    {\"op\":\"write_file\",\"path\":\"config/xxx.json\",\"value\":\"完整文件内容\",\"reason\":\"...\"}\n\n" +
        "规则：\n" +
        "  - 一个代码块，内容是 JSON 数组，最多 5 条。\n" +
        "  - 只能操作 .jar/.zip/.jar.disabled（删除）与文本配置类扩展名（.txt/.properties/.cfg/.toml/.json/.yaml/.yml/.ini/.conf）。\n" +
        "  - 只能操作当前实例目录内的文件。\n" +
        "  - 启动器会弹窗让用户确认后才执行，并在执行前自动备份原文件，所以你可以放心给出操作。\n\n" +
        "## 示例输出 ##\n" +
        "崩溃原因是 sodium 与 optifine 同时存在导致渲染管线冲突。日志中的关键行：\n" +
        "  java.lang.NoSuchMethodError: net.minecraft.client.renderer.chunk.RenderChunk.setOrigin\n" +
        "处理步骤：删除其中一个优化模组，二者功能重叠不能共存。\n\n" +
        "```json\n" +
        "[{\"op\":\"delete_mod\",\"path\":\"mods/sodium-0.5.11.jar\",\"reason\":\"与 OptiFine 渲染管线冲突\"}]\n" +
        "```";

    private static string Env(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string Endpoint => Env("PCL_AI_ENDPOINT", Config.Preference.Ai.Endpoint).TrimEnd('/');

    private static string Model => Env("PCL_AI_MODEL", Config.Preference.Ai.Model);

    /// <summary>当前生效的模型名，供界面判断配置是否完整。</summary>
    public static string CurrentModel => Model;

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
    public static async Task<string> AnalyzeAsync(string logText, McInstance? instance = null, CancellationToken token = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("AI 分析未启用或未配置地址");
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("AI 分析未配置模型");

        var log = logText ?? "";
        if (log.Length > MaxLogChars) log = "……（前文已省略）……\n" + log[^MaxLogChars..];
        if (string.IsNullOrWhiteSpace(log)) throw new InvalidOperationException("没有可分析的日志内容");

        // 分支说明：把实例目录告诉模型，它才能给出可执行的相对路径。
        var instanceInfo = instance is null
            ? "（本次没有关联游戏实例，无法提出文件修改建议，只做文字分析。）"
            : $"当前游戏实例：{instance.Name}" + "\n" +
              $"实例目录：{instance.PathIndie}" + "\n" +
              "建议里的 path 请使用相对于实例目录的路径。";
        var systemContent = SystemPrompt + "\n\n【环境】\n" + instanceInfo;

        var payload = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0.3,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemContent },
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

    /// <summary>
    ///     测试端点连通性：请求 {Endpoint}/models。
    /// </summary>
    /// <returns>是否可用，以及给用户看的提示文案。</returns>
    public static async Task<(bool Ok, string Message)> TestAsync()
    {
        if (!IsAvailable)
            return (false, PCL.Core.App.Localization.Lang.Text("Setup.Misc.Ai.TestNotConfigured"));
        if (string.IsNullOrWhiteSpace(Model))
            return (false, PCL.Core.App.Localization.Lang.Text("Setup.Misc.Ai.TestNoModel"));

        var url = Endpoint + "/models";
        try
        {
            var timeout = Math.Clamp(Config.Preference.Ai.TimeoutSeconds, 10, 600);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(timeout, 30)) };
            if (!string.IsNullOrWhiteSpace(ApiKey))
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);

            using var response = await http.GetAsync(url).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (true, PCL.Core.App.Localization.Lang.Text("Setup.Misc.Ai.TestOk", Model));

            if ((int)response.StatusCode == 401)
                return (false, PCL.Core.App.Localization.Lang.Text("Setup.Misc.Ai.TestUnauthorized"));

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (false, PCL.Core.App.Localization.Lang.Text("Setup.Misc.Ai.TestFailed",
                $"HTTP {(int)response.StatusCode}：" + Trim(body)));
        }
        catch (Exception ex)
        {
            return (false, PCL.Core.App.Localization.Lang.Text("Setup.Misc.Ai.TestFailed", ex.Message));
        }
    }

    private static string Trim(string value) =>
        value.Length <= 400 ? value : value[..400] + "……";
}