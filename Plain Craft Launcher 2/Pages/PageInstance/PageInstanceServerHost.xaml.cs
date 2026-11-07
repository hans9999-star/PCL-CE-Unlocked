using System.IO;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     便携开服页面：为当前实例一键准备并运行原版服务端，并提供内置控制台。
/// </summary>
public partial class PageInstanceServerHost : MyPageRight
{
    private bool _subscribed;

    public PageInstanceServerHost()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        // 页面进入时刷新状态（例如从别的实例切过来）
        PageEnter += () => Reload();
    }

    private static McInstance? Instance => PageInstanceLeft.McInstance;

    public void Reload()
    {
        if (!_subscribed)
        {
            ModServerHost.OutputReceived += AppendOutput;
            ModServerHost.Exited += OnServerExited;
            _subscribed = true;
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var inst = Instance;
        if (inst is null)
        {
            LabStatus.Text = Lang.Text("Instance.ServerHost.Status.NoInstance");
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = false;
            return;
        }

        var running = ModServerHost.IsRunning;
        var jarExists = File.Exists(ModServerHost.GetJarPath(inst.PathInstance));
        LabStatus.Text = running
            ? Lang.Text("Instance.ServerHost.Status.Running")
            : jarExists
                ? Lang.Text("Instance.ServerHost.Status.Ready")
                : Lang.Text("Instance.ServerHost.Status.NoJar");
        BtnStart.IsEnabled = !running;
        BtnStop.IsEnabled = running;
    }

    private void AppendOutput(string text)
    {
        ModBase.RunInUi(() =>
        {
            TextConsole.AppendText(text);
            TextConsole.ScrollToEnd();
        });
    }

    private void OnServerExited()
    {
        ModBase.RunInUi(RefreshStatus);
    }

    private async void BtnStart_Click(object sender, MouseButtonEventArgs e)
    {
        var inst = Instance;
        if (inst is null) return;

        var dir = inst.PathInstance;
        var jarPath = ModServerHost.GetJarPath(dir);

        // 1) 没有服务端就先下载
        if (!File.Exists(jarPath))
        {
            var url = ModServerHost.GetServerJarUrl(dir, out var sha1, out var size);
            if (string.IsNullOrWhiteSpace(url))
            {
                HintService.Hint(Lang.Text("Instance.ServerHost.Error.NoServerDownload", inst.Name), HintType.Error);
                return;
            }

            BtnStart.IsEnabled = false;
            LabStatus.Text = Lang.Text("Instance.ServerHost.Status.Downloading");
            ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.DownloadingStart", inst.Name));

            var ok = await ModServerHost.DownloadJarAsync(url, jarPath, size, sha1).ConfigureAwait(true);
            if (!ok)
            {
                HintService.Hint(Lang.Text("Instance.ServerHost.Error.DownloadFailed"), HintType.Error);
                RefreshStatus();
                return;
            }

            ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.Downloaded"));
        }

        // 2) 解析配置
        var port = int.TryParse(TextPort.Text?.Trim(), out var p) && p is > 0 and < 65536 ? p : 25565;
        var maxPlayers = int.TryParse(TextMaxPlayers.Text?.Trim(), out var m) && m > 0 ? m : 20;
        var memory = int.TryParse(TextMemory.Text?.Trim(), out var mem) && mem >= 512 ? mem : 2048;
        var onlineMode = CheckOnlineMode.Checked != false;

        // 3) 写 eula.txt 与 server.properties
        try
        {
            ModServerHost.PrepareFiles(dir, port, onlineMode, maxPlayers, "PCL CE Unlocked Server");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 准备服务端文件失败", ModBase.LogLevel.Hint);
            HintService.Hint(Lang.Text("Instance.ServerHost.Error.PrepareFailed"), HintType.Error);
            RefreshStatus();
            return;
        }

        // 4) 启动
        ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.Starting",
            ModServerHost.GetJavaPath(), memory, port));
        if (!ModServerHost.Start(dir, jarPath, memory))
        {
            HintService.Hint(Lang.Text("Instance.ServerHost.Error.StartFailed"), HintType.Error);
            RefreshStatus();
            return;
        }

        RefreshStatus();
    }

    private async void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (!ModServerHost.IsRunning) return;
        BtnStop.IsEnabled = false;
        ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.Stopping"));
        await ModServerHost.StopAsync().ConfigureAwait(true);
        RefreshStatus();
    }

    private void BtnSend_Click(object sender, MouseButtonEventArgs e)
    {
        SendCurrentCommand();
    }

    private void TextCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SendCurrentCommand();
        e.Handled = true;
    }

    private void SendCurrentCommand()
    {
        var command = TextCommand.Text?.Trim();
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!ModServerHost.IsRunning)
        {
            HintService.Hint(Lang.Text("Instance.ServerHost.Error.NotRunning"));
            return;
        }

        ModServerHost.SendCommand(command);
        TextCommand.Text = string.Empty;
    }
}
