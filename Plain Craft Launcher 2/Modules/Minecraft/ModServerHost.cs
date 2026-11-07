using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Minecraft;
using PCL.Core.Utils;

namespace PCL;

/// <summary>
///     便携开服：为原版实例下载官方服务端，并在启动器内直接运行与交互。<br/>
///     分支说明：原版 PCL CE 只提供「下载服务端」，产出一个 Java 路径写死的 .bat，
///     不会生成 eula.txt / server.properties，也没有控制台。本模块把这些缺口补齐。
/// </summary>
public static class ModServerHost
{
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

    #region 路径

    /// <summary>实例目录对应的服务端 jar 路径。</summary>
    public static string GetJarPath(string instanceDir)
    {
        var id = new DirectoryInfo(instanceDir.TrimEnd('\\', '/')).Name;
        return Path.Combine(instanceDir, id + "-server.jar");
    }

    /// <summary>
    ///     从实例的版本 JSON 里解析官方服务端下载信息。
    /// </summary>
    /// <returns>下载地址；该版本没有官方服务端时返回 null。</returns>
    public static string? GetServerJarUrl(string instanceDir, out string sha1, out long size)
    {
        sha1 = "";
        size = -1;
        var id = new DirectoryInfo(instanceDir.TrimEnd('\\', '/')).Name;
        var jsonPath = Path.Combine(instanceDir, id + ".json");
        if (!File.Exists(jsonPath)) return null;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)) is not JsonObject json)
                return null;
            if (json["downloads"]?["server"] is not JsonObject server)
                return null;
            sha1 = server["sha1"]?.ToString() ?? "";
            size = (long?)server["size"] ?? -1;
            return server["url"]?.ToString();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 解析服务端下载信息失败", ModBase.LogLevel.Hint);
            return null;
        }
    }

    /// <summary>
    ///     挑选一个可用的 Java 可执行文件：优先用 PCL 已扫描到并启用的 Java，
    ///     否则退回 PATH 里的 java。
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
    ///     写入 eula.txt 并补齐 server.properties 的关键项。<br/>
    ///     已有的其它配置项会保留，只覆盖本函数负责的几项。
    /// </summary>
    public static void PrepareFiles(string serverDir, int port, bool onlineMode, int maxPlayers, string motd)
    {
        Directory.CreateDirectory(serverDir);

        // EULA：不改这一步服务端会在启动时直接退出
        File.WriteAllText(Path.Combine(serverDir, "eula.txt"),
            "#By changing the setting below to TRUE you are indicating your agreement to our EULA " +
            "(https://aka.ms/MinecraftEULA).\r\neula=true\r\n", Encoding.UTF8);

        // server.properties：只覆盖我们负责的项
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

    /// <summary>向控制台输出一行文本。</summary>
    public static void Log(string line) => OutputReceived?.Invoke(line + Environment.NewLine);

    /// <summary>
    ///     下载服务端 jar，并把进度输出到控制台。
    /// </summary>
    public static async Task<bool> DownloadJarAsync(string url, string jarPath, long size, string sha1)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
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
            ModBase.Log(ex, "[ServerHost] 下载服务端失败", ModBase.LogLevel.Hint);
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
    }

    #region 启停

    /// <summary>
    ///     启动服务端进程。输出会通过 <see cref="OutputReceived" /> 事件逐行抛出。
    /// </summary>
    /// <returns>启动失败时返回 false。</returns>
    public static bool Start(string serverDir, string jarPath, int memoryMb)
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
        psi.ArgumentList.Add($"-Xmx{memoryMb}M");
        psi.ArgumentList.Add($"-Xms{Math.Min(memoryMb, Math.Max(512, memoryMb / 2))}M");
        psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jarPath);
        psi.ArgumentList.Add("nogui");

        try
        {
            ModBase.Log($"[ServerHost] 启动服务端：{javaPath} {string.Join(' ', psi.ArgumentList)}");
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) OutputReceived?.Invoke(e.Data + Environment.NewLine);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) OutputReceived?.Invoke(e.Data + Environment.NewLine);
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
                OutputReceived?.Invoke("> " + command + Environment.NewLine);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[ServerHost] 发送命令失败", ModBase.LogLevel.Developer);
            }
        }
    }

    /// <summary>请求停止服务端（发送 stop 命令；超时后强制结束）。</summary>
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
