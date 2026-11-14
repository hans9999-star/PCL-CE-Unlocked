using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PCL.Core.Minecraft;
using PCL.Core.Utils;

namespace PCL;

/// <summary>已创建的服务端。</summary>
public sealed class ServerEntry
{
    public required ServerSoftware Software { get; init; }
    public required string Version { get; init; }
    public required string Dir { get; init; }
    public required string Display { get; init; }
    public required bool Installed { get; init; }
}

/// <summary>服务端类型。</summary>
public enum ServerSoftware
{
    /// <summary>Mojang 官方原版服务端。</summary>
    Vanilla,

    /// <summary>Paper（插件端，支持 Bukkit/Spigot 插件）。</summary>
    Paper,

    /// <summary>Fabric（模组端，服务端 jar 由 Fabric Meta 直接提供）。</summary>
    Fabric,

    /// <summary>Forge（模组端，需先执行官方安装器）。</summary>
    Forge,

    /// <summary>NeoForge（模组端，需先执行官方安装器）。</summary>
    NeoForge
}

/// <summary>
///     便携开服：下载并运行 Minecraft 服务端，并在启动器内直接交互。<br/>
///     分支说明：服务端类型与版本由本模块自行选择，<b>不依赖当前实例</b>，
///     因此文件放在独立目录而不是实例目录里。
/// </summary>
public static class ModServerHost
{
    private const string MojangManifest = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
    private const string PaperProject = "https://fill.papermc.io/v3/projects/paper";
    private const string FabricMeta = "https://meta.fabricmc.net/v2";
    private const string ForgePromotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    // NeoForge 官方 maven 在国内基本不可达（实测超时），改用 bmclapi 镜像
    private const string NeoForgeMirror = "https://bmclapi2.bangbang93.com";
    private static Process? _process;
    private static readonly object _lock = new();

    /// <summary>当前是否正在运行服务端。</summary>
    public static bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>服务端输出一行文本时触发（已带换行）。</summary>
    public static event Action<string>? OutputReceived;

    /// <summary>服务端进程退出时触发。</summary>
    public static event Action? Exited;

    /// <summary>
    ///     最近的输出历史。<br/>
    ///     分支说明：事件只能通知「此刻正在订阅的人」，而游戏内覆盖层是随时打开的 ——
    ///     打开时服务端可能已经跑了一阵，只靠事件会看到一片空白。
    ///     所以在这里留一份环形缓冲，任何消费者都能补看最近的输出。
    /// </summary>
    private static readonly Queue<string> History = new();

    /// <summary>历史缓冲上限：约够看几百行，不至于无限增长。</summary>
    private const int HistoryLimit = 400;

    /// <summary>取最近的输出行（不含换行符）。</summary>
    public static IReadOnlyList<string> GetRecentOutput()
    {
        lock (History)
        {
            return History.ToList();
        }
    }

    /// <summary>清空输出历史（服务端启动时调用）。</summary>
    public static void ClearHistory()
    {
        lock (History)
        {
            History.Clear();
        }
    }

    /// <summary>
    ///     输出一行文本：先记入历史，再通知订阅者。<br/>
    ///     所有输出都必须走这里，否则覆盖层会漏掉内容。
    /// </summary>
    private static void Emit(string line)
    {
        lock (History)
        {
            History.Enqueue(line);
            while (History.Count > HistoryLimit) History.Dequeue();
        }

        OutputReceived?.Invoke(line + Environment.NewLine);
    }

    /// <summary>向控制台输出一行文本。</summary>
    public static void Log(string line) => Emit(line);

    /// <summary>
    ///     常用服务端指令。<br/>
    ///     放在这里是为了让联机页与游戏内覆盖层共用同一份定义，避免两处各写一份而漂移。
    /// </summary>
    public static readonly (string Label, string Command, string Tip)[] QuickCommands =
    [
        ("保存世界", "save-all", "立即把所有世界数据写入磁盘"),
        ("关闭自动保存", "save-off", "临时关闭自动保存，配合手动备份使用"),
        ("开启自动保存", "save-on", "恢复自动保存"),
        ("列出玩家", "list", "显示当前在线玩家"),
        ("重载白名单", "whitelist reload", "从 whitelist.json 重新读取白名单"),
        ("白天", "time set day", "把时间设为白天"),
        ("晴天", "weather clear", "清除下雨/雷暴"),
        ("保存并关服", "stop", "安全关闭服务器（会先保存世界）")
    ];

    #region 路径

    /// <summary>
    ///     服务端目录：与实例无关，按「类型-版本」独立存放，便于同一个服务端反复启动。
    /// </summary>
    public static string GetServerDir(ServerSoftware software, string version)
    {
        var name = software switch
        {
            ServerSoftware.Paper => "Paper",
            ServerSoftware.Fabric => "Fabric",
            ServerSoftware.Forge => "Forge",
            ServerSoftware.NeoForge => "NeoForge",
            _ => "Vanilla"
        };
        // Forge 版本串形如 "1.21.1|52.1.20"，竖线不能做目录名
        var safe = version.Replace('|', '-');
        return Path.Combine(ModBase.exePath, "PCL", "Server", $"{name}-{safe}");
    }

    /// <summary>服务端标记文件名：记录该目录属于哪种类型/版本，便于枚举时准确还原。</summary>
    private const string ServerMarkFile = "pcl-server.json";

    /// <summary>写入服务端标记（每次开服都会刷新）。</summary>
    public static void WriteServerMark(string serverDir, ServerSoftware software, string version)
    {
        try
        {
            File.WriteAllText(Path.Combine(serverDir, ServerMarkFile),
                new JsonObject { ["software"] = software.ToString(), ["version"] = version }.ToJsonString(),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 写入服务端标记失败", ModBase.LogLevel.Developer);
        }
    }

    /// <summary>
    ///     枚举本机已创建的服务端（扫描服务端根目录），供开服页做「服务端选择」。
    /// </summary>
    public static List<ServerEntry> GetCreatedServers()
    {
        var list = new List<ServerEntry>();
        try
        {
            var root = Path.Combine(ModBase.exePath, "PCL", "Server");
            if (!Directory.Exists(root)) return list;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var folder = Path.GetFileName(dir);
                ServerSoftware software;
                string version;

                var mark = Path.Combine(dir, ServerMarkFile);
                if (File.Exists(mark))
                {
                    try
                    {
                        if (JsonNode.Parse(File.ReadAllText(mark, Encoding.UTF8)) is not JsonObject obj) continue;
                        if (!Enum.TryParse(obj["software"]?.ToString(), out software)) continue;
                        version = obj["version"]?.ToString() ?? "";
                    }
                    catch
                    {
                        continue;
                    }
                }
                else
                {
                    // 兜底：旧目录没有标记文件，从目录名反解
                    var m = Regex.Match(folder, @"^(Vanilla|Paper|Fabric|Forge|NeoForge)-(.+)$");
                    if (!m.Success || !Enum.TryParse(m.Groups[1].Value, out software)) continue;
                    version = m.Groups[2].Value;
                    if (software == ServerSoftware.Forge)
                    {
                        // 目录里竖线被替换成了短横，Forge 需要还原成 "mc|loader"
                        var fm = Regex.Match(version, @"^(.+)-(\d+\.\d+\.\d+.*)$");
                        if (fm.Success) version = fm.Groups[1].Value + "|" + fm.Groups[2].Value;
                    }
                }

                if (string.IsNullOrWhiteSpace(version)) continue;

                var installed = software is ServerSoftware.Forge or ServerSoftware.NeoForge
                    ? FindArgsFile(dir) is not null
                    : File.Exists(GetJarPath(software, version));

                list.Add(new ServerEntry
                {
                    Software = software,
                    Version = version,
                    Dir = dir,
                    Display = DescribeServer(software, version),
                    Installed = installed
                });
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 枚举服务端失败", ModBase.LogLevel.Hint);
        }

        list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>服务端的显示名，如「1.21.1 · Fabric」。</summary>
    public static string DescribeServer(ServerSoftware software, string version)
    {
        var name = software switch
        {
            ServerSoftware.Paper => "Paper",
            ServerSoftware.Fabric => "Fabric",
            ServerSoftware.Forge => "Forge",
            ServerSoftware.NeoForge => "NeoForge",
            _ => "Vanilla"
        };

        if (software == ServerSoftware.Forge)
        {
            var p = version.Split('|');
            if (p.Length == 2) return $"{p[0]} · Forge {p[1]}";
        }

        if (software == ServerSoftware.NeoForge)
        {
            var p = version.Split('.');
            if (p.Length >= 2 && int.TryParse(p[0], out var major) && int.TryParse(p[1], out var minor))
                return major >= 26
                    ? $"{major}.{minor} · NeoForge {version}"
                    : $"1.{major}.{minor} · NeoForge {version}";
        }

        return $"{version} · {name}";
    }

    /// <summary>服务端 jar 路径。</summary>
    public static string GetJarPath(ServerSoftware software, string version) =>
        Path.Combine(GetServerDir(software, version), "server.jar");

    #endregion

    #region 版本与下载地址

    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<JsonNode> FetchJsonAsync(string url)
    {
        using var http = NewHttp();
        var text = await http.GetStringAsync(url).ConfigureAwait(false);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"无法解析 {url}");
    }

    /// <summary>
    ///     获取可选的服务端版本列表（新版本在前）。<br/>
    ///     原版取 Mojang 版本清单里的正式版；Paper 取 fill API 的全部版本。
    /// </summary>
    public static async Task<List<string>> GetVersionsAsync(ServerSoftware software)
    {
        var list = new List<string>();
        if (software == ServerSoftware.Paper)
        {
            var json = await FetchJsonAsync(PaperProject).ConfigureAwait(false);
            if (json["versions"] is not JsonObject families) return list;
            // 结构为 { 主版本: [完整版本...] }，按主版本从新到旧展开
            foreach (var family in families)
            {
                if (family.Value is not JsonArray arr) continue;
                foreach (var v in arr)
                {
                    var s = v?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }

            return list;
        }

        if (software == ServerSoftware.Forge)
        {
            // promos 键形如 "1.21.1-recommended" / "1.21.1-latest"，优先推荐版
            var promos = await FetchJsonAsync(ForgePromotions).ConfigureAwait(false);
            if (promos["promos"] is not JsonObject obj) return list;
            var recommended = new List<string>();
            var latest = new List<string>();
            foreach (var kv in obj)
            {
                var key = kv.Key;
                var val = kv.Value?.ToString();
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (key.EndsWith("-recommended", StringComparison.Ordinal))
                    recommended.Add($"{key[..^"-recommended".Length]}|{val}");
                else if (key.EndsWith("-latest", StringComparison.Ordinal))
                    latest.Add($"{key[..^"-latest".Length]}|{val}");
            }

            // 推荐版在前，只有推荐版缺失时才补充 latest
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in recommended)
            {
                var mc = item.Split('|')[0];
                if (seen.Add(mc)) list.Add(item);
            }

            foreach (var item in latest)
            {
                var mc = item.Split('|')[0];
                if (seen.Add(mc)) list.Add(item);
            }

            return list;
        }

        if (software == ServerSoftware.NeoForge)
        {
            var dir = await FetchJsonAsync($"{NeoForgeMirror}/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge")
                .ConfigureAwait(false);
            if (dir["files"] is not JsonArray files) return list;
            var all = new List<string>();
            foreach (var x in files)
            {
                if (x?["type"]?.ToString() != "DIRECTORY") continue;
                var name = x["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) all.Add(name);
            }

            all.Reverse(); // maven 目录是升序，倒过来让新版本在前
            return all;
        }

        if (software == ServerSoftware.Fabric)
        {
            var games = await FetchJsonAsync($"{FabricMeta}/versions/game").ConfigureAwait(false);
            if (games is not JsonArray gameArr) return list;
            foreach (var g in gameArr)
            {
                // 只列正式版，避免刷屏
                if (g?["stable"]?.GetValue<bool>() != true) continue;
                var id = g["version"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
            }

            return list;
        }

        var manifest = await FetchJsonAsync(MojangManifest).ConfigureAwait(false);
        if (manifest["versions"] is not JsonArray versions) return list;
        foreach (var v in versions)
        {
            if (v?["type"]?.ToString() != "release") continue;
            var id = v["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
        }

        return list;
    }

    /// <summary>
    ///     解析某个版本服务端的下载地址。
    /// </summary>
    /// <returns>下载候选地址（含镜像，按顺序尝试）、SHA256（可能为空）、文件大小（未知为 -1）。</returns>
    public static async Task<(List<string> Urls, string Sha256, long Size)?> ResolveDownloadAsync(
        ServerSoftware software, string version)
    {
        if (software == ServerSoftware.Paper)
        {
            var builds = await FetchJsonAsync($"{PaperProject}/versions/{version}/builds").ConfigureAwait(false);
            if (builds is not JsonArray arr || arr.Count == 0) return null;

            // 优先取最新的 STABLE 构建，没有则取最后一个
            JsonNode? chosen = null;
            foreach (var b in arr)
                if (b?["channel"]?.ToString() == "STABLE") chosen = b;
            chosen ??= arr[^1];

            var dl = chosen?["downloads"]?["server:default"];
            var url = dl?["url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url)) return null;
            return (new List<string> { url }, dl?["checksums"]?["sha256"]?.ToString() ?? "",
                (long?)dl?["size"] ?? -1);
        }


        if (software == ServerSoftware.Forge)
        {
            // version 形如 "1.21.1|52.1.20"
            var parts = version.Split('|');
            if (parts.Length != 2) return null;
            var installer = $"{ForgeMaven}/{parts[0]}-{parts[1]}/forge-{parts[0]}-{parts[1]}-installer.jar";
            return (new List<string> { installer }, "", -1);
        }

        if (software == ServerSoftware.NeoForge)
        {
            var installer =
                $"{NeoForgeMirror}/maven/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar";
            return (new List<string> { installer }, "", -1);
        }

        if (software == ServerSoftware.Fabric)
        {
            // Fabric Meta 直接提供可运行的服务端 jar：自动取最新的稳定 loader 与 installer
            var loaders = await FetchJsonAsync($"{FabricMeta}/versions/loader/{version}").ConfigureAwait(false);
            if (loaders is not JsonArray loaderArr || loaderArr.Count == 0) return null;
            string? loaderVer = null;
            foreach (var l in loaderArr)
                if (l?["loader"]?["stable"]?.GetValue<bool>() == true)
                {
                    loaderVer = l["loader"]?["version"]?.ToString();
                    break;
                }

            loaderVer ??= loaderArr[0]?["loader"]?["version"]?.ToString();
            if (string.IsNullOrWhiteSpace(loaderVer)) return null;

            var installers = await FetchJsonAsync($"{FabricMeta}/versions/installer").ConfigureAwait(false);
            if (installers is not JsonArray instArr || instArr.Count == 0) return null;
            string? instVer = null;
            foreach (var i in instArr)
                if (i?["stable"]?.GetValue<bool>() == true)
                {
                    instVer = i["version"]?.ToString();
                    break;
                }

            instVer ??= instArr[0]?["version"]?.ToString();
            if (string.IsNullOrWhiteSpace(instVer)) return null;

            var fabricUrl = $"{FabricMeta}/versions/loader/{version}/{loaderVer}/{instVer}/server/jar";
            ModBase.Log($"[ServerHost] Fabric 服务端：{version} + loader {loaderVer} + installer {instVer}");
            return (new List<string> { fabricUrl }, "", -1);
        }

        // 原版：从版本清单找到该版本的 JSON，再读 downloads.server
        var manifest = await FetchJsonAsync(MojangManifest).ConfigureAwait(false);
        if (manifest["versions"] is not JsonArray versions) return null;
        string? metaUrl = null;
        foreach (var v in versions)
            if (v?["id"]?.ToString() == version)
            {
                metaUrl = v["url"]?.ToString();
                break;
            }

        if (string.IsNullOrWhiteSpace(metaUrl)) return null;
        var meta = await FetchJsonAsync(metaUrl).ConfigureAwait(false);
        var server = meta["downloads"]?["server"];
        var jarUrl = server?["url"]?.ToString();
        if (string.IsNullOrWhiteSpace(jarUrl)) return null;

        // 复用 PCL 的镜像逻辑（官方源 + bmclapi 等）
        var urls = new List<string>();
        try
        {
            urls.AddRange(ModDownload.DlSourceLauncherOrMetaGet(jarUrl));
        }
        catch
        {
            // 镜像解析失败时退回原始地址
        }

        if (urls.Count == 0) urls.Add(jarUrl);
        return (urls, server?["sha256"]?.ToString() ?? server?["sha1"]?.ToString() ?? "",
            (long?)server?["size"] ?? -1);
    }

    /// <summary>
    ///     按顺序尝试候选地址下载服务端 jar，并把进度输出到控制台。
    /// </summary>
    public static async Task<bool> DownloadJarAsync(List<string> urls, string jarPath, long size)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 创建服务端目录失败", ModBase.LogLevel.Hint);
            return false;
        }

        foreach (var url in urls)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var resp = await http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? size;
                await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var dst = new FileStream(jarPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long read = 0;
                var lastPct = -10;
                int n;
                while ((n = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                    read += n;
                    if (total <= 0) continue;
                    var pct = (int)(read * 100 / total);
                    if (pct < lastPct + 10) continue;
                    lastPct = pct;
                    Log($"[Download] {pct}%  ({read / 1048576} MB / {total / 1048576} MB)");
                }

                return true;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[ServerHost] 从 {url} 下载失败，尝试下一个源", ModBase.LogLevel.Hint);
            }
        }

        try
        {
            if (File.Exists(jarPath)) File.Delete(jarPath);
        }
        catch
        {
            // ignored
        }

        return false;
    }

    #endregion

    #region Java

    /// <summary>
    ///     挑选一个可用的 Java：优先用 PCL 已扫描到并启用的 Java，否则退回 PATH 里的 java。
    /// </summary>
    public static string GetJavaPath()
    {
        try
        {
            var list = JavaService.JavaManager?.GetSortedJavaList();
            var entry = list?.FirstOrDefault(j => j.IsEnabled && File.Exists(j.Installation.JavaExePath))
                        ?? list?.FirstOrDefault(j => File.Exists(j.Installation.JavaExePath));
            if (entry is not null) return entry.Installation.JavaExePath;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 获取 Java 列表失败，回退到 PATH", ModBase.LogLevel.Developer);
        }

        return "java";
    }

    #endregion

    #region 准备文件

    /// <summary>
    ///     写入 eula.txt 并补齐 server.properties 的关键项（已有其它项会保留）。
    /// </summary>
    public static void PrepareFiles(string serverDir, int port, bool onlineMode, int maxPlayers, string motd)
    {
        Directory.CreateDirectory(serverDir);

        File.WriteAllText(Path.Combine(serverDir, "eula.txt"),
            "#By changing the setting below to TRUE you are indicating your agreement to our EULA " +
            "(https://aka.ms/MinecraftEULA).\r\neula=true\r\n", Encoding.UTF8);

        var propsPath = Path.Combine(serverDir, "server.properties");
        var lines = new List<string>();
        if (File.Exists(propsPath))
            lines.AddRange(File.ReadAllLines(propsPath, Encoding.UTF8)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')));

        void Set(string key, string value)
        {
            var idx = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            var line = key + "=" + value;
            if (idx >= 0) lines[idx] = line;
            else lines.Add(line);
        }

        Set("server-port", port.ToString());
        Set("online-mode", onlineMode ? "true" : "false");
        Set("max-players", maxPlayers.ToString());
        Set("motd", motd);
        if (!lines.Any(l => l.StartsWith("level-name=", StringComparison.OrdinalIgnoreCase)))
            Set("level-name", "world");
        if (!lines.Any(l => l.StartsWith("gamemode=", StringComparison.OrdinalIgnoreCase)))
            Set("gamemode", "survival");

        File.WriteAllLines(propsPath, lines, Encoding.UTF8);
        ModBase.Log($"[ServerHost] 已写入 eula.txt 与 server.properties（端口 {port}，正版验证 {(onlineMode ? "开" : "关")}）");
    }

    #endregion

    #region 启停

    /// <summary>
    ///     启动服务端进程。输出会通过 <see cref="OutputReceived" /> 事件逐行抛出。
    /// </summary>
    /// <summary>
    ///     直接以 -jar 方式启动服务端（原版 / Paper / Fabric）。
    /// </summary>
    public static bool Start(string serverDir, string jarPath, int memoryMb)
    {
        // 新的一局，丢掉上一次的输出历史
        ClearHistory();

        var args = new List<string>
        {
            $"-Xmx{memoryMb}M",
            $"-Xms{Math.Min(memoryMb, Math.Max(512, memoryMb / 2))}M",
            "-Dfile.encoding=UTF-8",
            "-jar",
            jarPath,
            "nogui"
        };
        return StartProcess(serverDir, args);
    }

    /// <summary>
    ///     按 Forge / NeoForge 生成的 run.bat 方式启动：
    ///     <c>java @user_jvm_args.txt @&lt;win_args.txt&gt; nogui</c>。<br/>
    ///     分支说明：win_args.txt 里的 <c>-jar forge-xxx-shim.jar</c> 是相对服务器根目录的路径，
    ///     所以工作目录必须是服务器根目录，且参数文件要以相对路径传入。
    /// </summary>
    public static bool StartWithArgsFile(string serverDir, string argsRelativePath, int memoryMb)
    {
        // 新的一局，丢掉上一次的输出历史
        ClearHistory();

        try
        {
            // 覆写 user_jvm_args.txt 写入内存设置
            File.WriteAllText(Path.Combine(serverDir, "user_jvm_args.txt"),
                "# Generated by Plain Craft Launcher Unbound Edition\r\n" +
                $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                $"-Xmx{memoryMb}M\r\n" +
                $"-Xms{Math.Min(memoryMb, Math.Max(512, memoryMb / 2))}M\r\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 写入 user_jvm_args.txt 失败", ModBase.LogLevel.Hint);
            return false;
        }

        var args = new List<string>
        {
            "@user_jvm_args.txt",
            "@" + argsRelativePath.Replace('\\', '/'),
            "nogui"
        };
        return StartProcess(serverDir, args);
    }

    /// <summary>
    ///     执行 Forge / NeoForge 安装器（--installServer）。会下载大量依赖，可能耗时数分钟。
    /// </summary>
    public static async Task<bool> RunInstallerAsync(string serverDir, string installerPath)
    {
        var java = GetJavaPath();
        var psi = new ProcessStartInfo(java)
        {
            // 安装器按「工作目录」决定安装位置，这里必须显式设置，否则会装到启动器目录去
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installServer");

        try
        {
            ModBase.Log($"[ServerHost] 执行安装器：{java} -jar {installerPath} --installServer");
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) Emit(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) Emit(e.Data);
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            ModBase.Log($"[ServerHost] 安装器退出码：{proc.ExitCode}");
            if (proc.ExitCode != 0)
            {
                Emit($"[ServerHost] 安装器退出码 {proc.ExitCode}");
                return false;
            }

            // 安装完成后安装器与日志已无用
            try
            {
                if (File.Exists(installerPath)) File.Delete(installerPath);
                var log = installerPath + ".log";
                if (File.Exists(log)) File.Delete(log);
            }
            catch
            {
                // ignored
            }

            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 执行安装器失败", ModBase.LogLevel.Hint);
            return false;
        }
    }

    /// <summary>
    ///     在安装结果里找 Forge / NeoForge 生成的启动参数文件（win_args.txt），
    ///     返回相对服务器根目录的路径。
    /// </summary>
    public static string? FindArgsFile(string serverDir)
    {
        try
        {
            var libDir = Path.Combine(serverDir, "libraries", "net");
            if (!Directory.Exists(libDir)) return null;
            foreach (var vendor in new[] { "minecraftforge", "neoforged" })
            {
                var root = Path.Combine(libDir, vendor);
                if (!Directory.Exists(root)) continue;
                var hit = Directory.EnumerateFiles(root, "win_args.txt", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length)
                    .FirstOrDefault();
                if (hit is null) continue;
                return Path.GetRelativePath(serverDir, hit);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 查找启动参数文件失败", ModBase.LogLevel.Hint);
        }

        return null;
    }

    /// <summary>通用进程启动。</summary>
    private static bool StartProcess(string serverDir, IEnumerable<string> args)
    {
        if (IsRunning) return false;

        var javaPath = GetJavaPath();
        var psi = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            ModBase.Log($"[ServerHost] 启动服务端：{javaPath} {string.Join(' ', psi.ArgumentList)}");
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) Emit(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) Emit(e.Data);
            };
            proc.Exited += (_, _) =>
            {
                lock (_lock) _process = null;
                ModBase.Log("[ServerHost] 服务端进程已退出");
                Exited?.Invoke();
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_lock) _process = proc;
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 启动服务端失败", ModBase.LogLevel.Hint);
            lock (_lock) _process = null;
            return false;
        }
    }

    /// <summary>
    /// <summary>向服务端控制台发送一条命令。</summary>
    public static void SendCommand(string command)
    {
        lock (_lock)
        {
            if (_process is null || _process.HasExited) return;
            try
            {
                _process.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
                Emit("> " + command);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[ServerHost] 发送命令失败", ModBase.LogLevel.Developer);
            }
        }
    }

    /// <summary>请求停止服务端（发送 stop；超时后强制结束）。</summary>
    public static async Task StopAsync(int timeoutMs = 15000)
    {
        if (!IsRunning) return;
        SendCommand("stop");

        for (var waited = 0; waited < timeoutMs; waited += 200)
        {
            await Task.Delay(200);
            if (!IsRunning) return;
        }

        ModBase.Log("[ServerHost] 服务端未在超时内退出，强制结束", ModBase.LogLevel.Hint);
        lock (_lock)
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(true);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[ServerHost] 强制结束服务端失败");
            }
        }
    }

    #endregion
}