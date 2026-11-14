using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     游戏内叠加面板（Steam 风格全屏覆盖层）。<br/>
///     布局：贴图背景 + 左上时间信息 + 右上返回游戏 + 中部内容 + 底部功能图标。<br/>
///     分支说明：FPS 无法显示 —— 启动器是外部进程，游戏不暴露帧率，
///     要拿 FPS 必须往游戏注入 DLL 挂渲染钩子，与本功能「不注入」的前提冲突。
/// </summary>
public partial class GameOverlayPanel
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _articleCts;
    private DispatcherTimer? _statsTimer;
    private DateTime _sessionStart;

    private string? _openTitle;
    private bool _introPlayed;
    private bool _closing;
    private bool _noteDirty;
    private bool _consoleAttached;
    private int _activeTab;
    private bool _showingSearch = true;

    private static string NotePath => Path.Combine(ModBase.exePath, "PCL", "OverlayNote.txt");

    public GameOverlayPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _sessionStart = DateTime.Now;
            LoadBackground();
            LoadVersionPanoramaAsync();
            LoadNote();
            StartStats();
        };
        Unloaded += (_, _) => StopStats();
    }

    #region 背景

    /// <summary>加载背景与游戏 logo。</summary>
    private void LoadBackground()
    {
        try
        {
            // 第一层：方块贴图兜底，立即可见
            ImgBack.Source = LoadResourceBitmap("Images/Overlay/overlay_bg.png");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 加载背景图失败", ModBase.LogLevel.Developer);
        }

        try
        {
            // 标题用游戏本体里的 logo 贴图
            ImgLogo.Source = LoadResourceBitmap("Images/Overlay/mc_logo.png");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 加载 logo 失败", ModBase.LogLevel.Developer);
            // 兜底：回退成文字标题
            ImgLogo.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    ///     异步加载当前游玩版本的官方全景图，加载完成后淡入盖住兜底层。<br/>
    ///     分支说明：全景图按版本从游戏资源解析（见 ModGamePanorama），
    ///     解码放后台线程；拿不到就保持方块贴图兜底，不影响使用。
    /// </summary>
    private async void LoadVersionPanoramaAsync()
    {
        try
        {
            var bitmap = await ModGamePanorama.LoadRandomAsync().ConfigureAwait(true);
            if (bitmap is null || _closing) return;

            ImgPano.Source = bitmap;
            ModAnimation.AniStart(
                ModAnimation.AaOpacity(ImgPano, 0.9d - ImgPano.Opacity, 400, 0,
                    new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                "Overlay Pano Fade");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 加载版本全景图失败", ModBase.LogLevel.Developer);
        }
    }
    /// <summary>从程序集资源里加载图片。</summary>
    private static BitmapImage? LoadResourceBitmap(string relativePath)
    {
        var uri = new Uri($"pack://application:,,,/Plain Craft Launcher 2;component/{relativePath}");
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
    #endregion

    #region 面板开关与动画

    private void BtnBackToGame_Click(object sender, MouseButtonEventArgs e) => HidePanel();

    private void BtnClose_Click(object sender, MouseButtonEventArgs e) => HidePanel();

    /// <summary>退出游戏：确认后结束游戏进程。</summary>
    private void BtnQuitGame_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.MyMsgBox(
                Lang.Text("Main.Overlay.QuitGameConfirm"),
                Lang.Text("Main.Overlay.Title"),
                Lang.Text("Common.Action.Confirm"),
                Lang.Text("Common.Action.Cancel"),
                isWarn: true) != 1)
            return;

        try
        {
            var process = ModLaunch.mcLaunchProcess;
            if (process is { HasExited: false })
            {
                ModBase.Log("[Overlay] 用户从覆盖层退出游戏");
                process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 结束游戏失败", ModBase.LogLevel.Hint);
        }

        HidePanel();
    }

    /// <summary>打开面板：全屏层淡入 + 内容上滑。</summary>
    private void ShowPanel()
    {
        var slide = PanMain.RenderTransform as TranslateTransform ?? new TranslateTransform();
        PanMain.RenderTransform = slide;
        slide.Y = 16d;

        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(PanDim, 0.5d - PanDim.Opacity, 160, 0,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaOpacity(PanMain, 1d - PanMain.Opacity, 200, 30,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaDouble(i => slide.Y += (double)i, -16d, 220, 30,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
        }, OverlayAnimKey);

        TextSearch.Focus();
    }

    /// <summary>关闭面板：播完淡出动画再真正关窗。</summary>
    private void HidePanel()
    {
        if (_closing) return;
        _closing = true;

        ModBase.Log("[Overlay] HidePanel 被调用", ModBase.LogLevel.Debug);

        SaveNoteIfDirty();
        StopStats();

        var slide = PanMain.RenderTransform as TranslateTransform ?? new TranslateTransform();
        PanMain.RenderTransform = slide;

        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(PanDim, -PanDim.Opacity, 130, 0,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaOpacity(PanMain, -PanMain.Opacity, 130, 0,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaDouble(i => slide.Y += (double)i, 16d, 150, 0,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(new System.Threading.ThreadStart(() =>
            {
                ModGameOverlay.Close();
            }), 160)
        }, OverlayAnimKey);
    }

    /// <summary>首次显示时播放入场动画。</summary>
    internal void PlayIntroIfNeeded()
    {
        if (_introPlayed) return;
        _introPlayed = true;

        PanDim.Opacity = 0d;
        PanMain.Opacity = 0d;

        var slide = PanMain.RenderTransform as TranslateTransform ?? new TranslateTransform();
        PanMain.RenderTransform = slide;
        slide.Y = 16d;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ModAnimation.AniStart(new[]
            {
                ModAnimation.AaOpacity(PanDim, 0.5d, 180, 0,
                    new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaOpacity(PanMain, 1d, 220, 50,
                    new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaDouble(i => slide.Y += (double)i, -16d, 240, 50,
                    new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
            }, OverlayAnimKey);

            TextSearch.Focus();
        }), DispatcherPriority.Render);
    }

    private const string OverlayAnimKey = "Overlay Panel";

    #endregion

    #region 页签


    /// <summary>切换页签：0=查询 1=笔记 2=控制台 3=快捷操作。</summary>
    private void SwitchTab(int index)
    {
        _activeTab = index;

        BtnTabSearch.ColorType = index == 0 ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal;
        BtnTabNote.ColorType = index == 1 ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal;
        BtnTabConsole.ColorType = index == 2 ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal;
        BtnTabMacro.ColorType = index == 3 ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal;

        BtnIcoInfo.Opacity = index == 0 ? 1d : 0.45d;
        BtnIcoNote.Opacity = index == 1 ? 1d : 0.45d;
        BtnIcoConsole.Opacity = index == 2 ? 1d : 0.45d;
        BtnIcoMacro.Opacity = index == 3 ? 1d : 0.45d;

        PanSearchTab.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanNoteTab.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanConsoleTab.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanMacroTab.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        // 控制台只在可见时订阅输出，避免后台空转
        if (index == 2) AttachConsole();
        else DetachConsole();

        if (index == 3 && PanMacroButtons.Children.Count == 0) BuildMacroButtons();

        switch (index)
        {
            case 0: TextSearch.Focus(); break;
            case 1: TextNote.Focus(); break;
            case 2: TextCommand.Focus(); break;
            case 3: TextChatCommand.Focus(); break;
        }
    }

    private void BtnTabSearch_Click(object sender, MouseButtonEventArgs e) => SwitchTab(0);

    private void BtnTabNote_Click(object sender, MouseButtonEventArgs e) => SwitchTab(1);

    private void BtnTabConsole_Click(object sender, MouseButtonEventArgs e) => SwitchTab(2);

    private void BtnTabMacro_Click(object sender, MouseButtonEventArgs e) => SwitchTab(3);

    /// <summary>底部图标：切到查询页签。</summary>
    private void BtnIcoSearch_Click(object sender, EventArgs e) => SwitchTab(0);

    /// <summary>底部图标：切到笔记页签。</summary>
    private void BtnIcoNote_Click(object sender, EventArgs e) => SwitchTab(1);

    /// <summary>底部图标：切到控制台页签。</summary>
    private void BtnIcoConsole_Click(object sender, EventArgs e) => SwitchTab(2);

    /// <summary>底部图标：切到快捷操作页签。</summary>
    private void BtnIcoMacro_Click(object sender, EventArgs e) => SwitchTab(3);

    #endregion
    #region 快捷操作（鼠标/键盘宏）

    /// <summary>
    ///     生成宏按钮。<br/>
    ///     分支说明：宏必须先把面板关掉再发送 —— SendInput 只会送达前台窗口，
    ///     面板开着时游戏不是前台，按键会被面板自己吃掉。
    /// </summary>
    private void BuildMacroButtons()
    {
        foreach (var (labelKey, tipKey, action) in MacroDefinitions)
        {
            var button = new MyButton
            {
                Text = Lang.Text(labelKey),
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 6, 12, 6),
                MinWidth = 88,
                ColorType = MyButton.ColorState.Normal,
                ToolTip = Lang.Text(tipKey)
            };

            var captured = action;
            button.Click += (_, _) => ExecuteMacro(captured);
            PanMacroButtons.Children.Add(button);
        }
    }

    /// <summary>执行宏：先关面板，等焦点回到游戏，再发送输入。</summary>
    private void ExecuteMacro(Func<Task> action)
    {
        HidePanel();
        _ = ModGameMacro.RunOnGameAsync(action);
    }

    private void TextChatCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendChatCommand();
    }

    private void BtnSendChat_Click(object sender, MouseButtonEventArgs e) => SendChatCommand();

    /// <summary>
    ///     把输入框里的内容作为聊天指令发到游戏：按 T 打开聊天框、输入、回车。<br/>
    ///     输入内容会原样发送，所以「/gamemode creative」这类指令要带斜杠。
    /// </summary>
    private void SendChatCommand()
    {
        var text = TextChatCommand.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            HintService.Hint(Lang.Text("Main.Overlay.Macro.NeedCommand"));
            return;
        }

        ExecuteMacro(async () =>
        {
            await ModGameMacro.TapKeyAsync(ModGameMacro.VkT).ConfigureAwait(false);
            await Task.Delay(160).ConfigureAwait(false);
            await ModGameMacro.TypeTextAsync(text).ConfigureAwait(false);
            await Task.Delay(80).ConfigureAwait(false);
            await ModGameMacro.TapKeyAsync(ModGameMacro.VkEnter).ConfigureAwait(false);
        });
    }

    /// <summary>
    ///     内置宏。<br/>
    ///     分支说明：这里只放「游戏里本来就要按的键」与少量连点，
    ///     不做脚本化自动操作 —— 那超出「快捷操作」的范围，也容易被滥用。
    /// </summary>
    private static readonly (string LabelKey, string TipKey, Func<Task> Action)[] MacroDefinitions =
    [
        ("Main.Overlay.Macro.Debug", "Main.Overlay.Macro.Debug.Tip", () => ModGameMacro.TapKeyAsync(ModGameMacro.VkF3)),
        ("Main.Overlay.Macro.Screenshot", "Main.Overlay.Macro.Screenshot.Tip", () => ModGameMacro.TapKeyAsync(ModGameMacro.VkF2)),
        ("Main.Overlay.Macro.Perspective", "Main.Overlay.Macro.Perspective.Tip", () => ModGameMacro.TapKeyAsync(ModGameMacro.VkF5)),
        ("Main.Overlay.Macro.Fullscreen", "Main.Overlay.Macro.Fullscreen.Tip", () => ModGameMacro.TapKeyAsync(ModGameMacro.VkF11)),
        ("Main.Overlay.Macro.Inventory", "Main.Overlay.Macro.Inventory.Tip", () => ModGameMacro.TapKeyAsync(ModGameMacro.VkE)),
        ("Main.Overlay.Macro.Drop", "Main.Overlay.Macro.Drop.Tip", () => ModGameMacro.TapKeyAsync(ModGameMacro.VkQ)),
        ("Main.Overlay.Macro.Crouch", "Main.Overlay.Macro.Crouch.Tip", () => ModGameMacro.TapComboAsync([ModGameMacro.VkShift], ModGameMacro.VkSpace)),
        ("Main.Overlay.Macro.ClickTen", "Main.Overlay.Macro.ClickTen.Tip", () => ModGameMacro.ClickRepeatedlyAsync(10, 80)),
        ("Main.Overlay.Macro.RightClick", "Main.Overlay.Macro.RightClick.Tip", () => ModGameMacro.ClickMouseAsync(ModGameMacro.MouseButton.Right)),
        ("Main.Overlay.Macro.ScrollUp", "Main.Overlay.Macro.ScrollUp.Tip", () => { ModGameMacro.ScrollWheel(3); return Task.CompletedTask; }),
        ("Main.Overlay.Macro.ScrollDown", "Main.Overlay.Macro.ScrollDown.Tip", () => { ModGameMacro.ScrollWheel(-3); return Task.CompletedTask; })
    ];

    #endregion

    #region 服务端控制台

    /// <summary>
    ///     挂上服务端输出。<br/>
    ///     分支说明：订阅前先用 GetRecentOutput 补看历史 —— 覆盖层是随时打开的，
    ///     服务端可能已经跑了一阵，只靠事件会看到一片空白。
    /// </summary>
    private void AttachConsole()
    {
        if (!_consoleAttached)
        {
            ModServerHost.OutputReceived += OnServerOutput;
            ModServerHost.Exited += OnServerExited;
            _consoleAttached = true;
        }

        TextConsole.Clear();
        foreach (var line in ModServerHost.GetRecentOutput())
            TextConsole.AppendText(line + Environment.NewLine);

        if (PanOverlayCommands.Children.Count == 0) BuildQuickCommands();
        RefreshConsoleState();
        ScrollConsoleToEnd();
    }

    private void DetachConsole()
    {
        if (!_consoleAttached) return;
        ModServerHost.OutputReceived -= OnServerOutput;
        ModServerHost.Exited -= OnServerExited;
        _consoleAttached = false;
    }

    private void OnServerOutput(string text) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TextConsole.AppendText(text);
            ScrollConsoleToEnd();
        }));

    private void OnServerExited() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TextConsole.AppendText(Lang.Text("Main.Overlay.ConsoleExited") + Environment.NewLine);
            RefreshConsoleState();
        }));

    /// <summary>根据服务端是否在运行，切换提示与输入行的可用性。</summary>
    private void RefreshConsoleState()
    {
        var running = ModServerHost.IsRunning;
        LabConsoleIdle.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        TextCommand.IsEnabled = running;
        PanOverlayCommands.IsEnabled = running;
    }

    private void ScrollConsoleToEnd() => PanConsoleScroll.ScrollToEnd();

    /// <summary>生成快捷命令按钮，与联机页共用同一份定义。</summary>
    private void BuildQuickCommands()
    {
        foreach (var (label, command, tip) in ModServerHost.QuickCommands)
        {
            var button = new MyButton
            {
                Text = label,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 74,
                ToolTip = $"{tip}\n/{command}"
            };

            var captured = command;
            button.Click += (_, _) =>
            {
                if (!ModServerHost.IsRunning)
                {
                    HintService.Hint(Lang.Text("Main.Overlay.ConsoleIdle"));
                    return;
                }

                // 「保存并关服」是破坏性操作，先确认
                if (captured == "stop" &&
                    ModMain.MyMsgBox(Lang.Text("Tools.GameLink.Link.ServerHostQuickStopConfirm"),
                        Lang.Text("Main.Overlay.Tab.Console"),
                        Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) != 1)
                    return;

                ModServerHost.SendCommand(captured);
            };

            PanOverlayCommands.Children.Add(button);
        }
    }

    private void TextCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendCurrentCommand();
    }

    private void BtnSendCommand_Click(object sender, MouseButtonEventArgs e) => SendCurrentCommand();

    private void SendCurrentCommand()
    {
        var command = TextCommand.Text?.Trim();
        if (string.IsNullOrWhiteSpace(command)) return;

        if (!ModServerHost.IsRunning)
        {
            HintService.Hint(Lang.Text("Main.Overlay.ConsoleIdle"));
            return;
        }

        ModServerHost.SendCommand(command);
        TextCommand.Clear();
    }
    /// <summary>底部图标：打开完整 Wiki 页面（用系统浏览器）。</summary>
    private void BtnOpenWiki_Click(object sender, EventArgs e) =>
        ModBase.OpenWebsite(ModWikiSearch.BaseUrl);

    /// <summary>底部图标：把当前搜索词或条目链接复制到剪贴板。</summary>
    private void BtnCopyCode_Click(object sender, EventArgs e)
    {
        try
        {
            var text = _openTitle ?? TextSearch.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = ModWikiSearch.BaseUrl;
            }

            Clipboard.SetText(text);
            HintService.Hint(Lang.Text("Main.Overlay.Copied"));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 复制失败", ModBase.LogLevel.Developer);
        }
    }

    #endregion

    #region 状态栏

    private void StartStats()
    {
        if (_statsTimer is not null) return;

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
        RefreshStats();
    }

    private void StopStats()
    {
        _statsTimer?.Stop();
        _statsTimer = null;
    }

    private void RefreshStats()
    {
        try
        {
            var now = DateTime.Now;
            LabClock.Text = now.ToString("HH:mm");
            LabDate.Text = now.ToString(Lang.Text("Main.Overlay.DateFormat"));

            var elapsed = now - _sessionStart;
            LabSession.Text = Lang.Text("Main.Overlay.SessionTime",
                (int)elapsed.TotalHours, elapsed.Minutes);

            var cpu = ModSystemMonitor.GetCpuUsage();
            var (usedMb, totalMb) = ModSystemMonitor.GetMemoryInfo();
            var gameMb = ModSystemMonitor.GetGameMemoryMb();

            var parts = new List<string>
            {
                Lang.Text("Main.Overlay.Stats.Cpu", Math.Round(cpu)),
                Lang.Text("Main.Overlay.Stats.MemoryShort",
                    Math.Round(usedMb), Math.Round(totalMb))
            };

            if (gameMb > 0)
                parts.Add(Lang.Text("Main.Overlay.Stats.GameMemory", Math.Round(gameMb)));

            LabStats.Text = string.Join("   ", parts);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 刷新状态栏失败", ModBase.LogLevel.Developer);
        }
    }

    #endregion

    #region 笔记

    private void LoadNote()
    {
        try
        {
            if (!File.Exists(NotePath)) return;
            TextNote.Text = File.ReadAllText(NotePath);
            _noteDirty = false;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 读取笔记失败", ModBase.LogLevel.Developer);
        }
    }

    private void TextNote_TextChanged(object sender, TextChangedEventArgs e) => _noteDirty = true;

    private void SaveNoteIfDirty()
    {
        if (!_noteDirty) return;

        try
        {
            var dir = Path.GetDirectoryName(NotePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(NotePath, TextNote.Text ?? "");
            _noteDirty = false;
            ModBase.Log("[Overlay] 笔记已保存");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 保存笔记失败", ModBase.LogLevel.Hint);
        }
    }

    #endregion

    #region 搜索

    private void TextSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _openTitle is not null)
        {
            e.Handled = true;
            _ = ShowResultsAgainAsync();
            return;
        }

        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = RunSearchAsync();
    }

    private void BtnSearch_Click(object sender, MouseButtonEventArgs e) => _ = RunSearchAsync();

    private async Task RunSearchAsync()
    {
        var keyword = TextSearch.Text?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            ShowStatus(Lang.Text("Main.Overlay.NeedKeyword"));
            return;
        }

        _cts?.Cancel();
        _articleCts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _openTitle = null;
        BtnBack.Visibility = Visibility.Collapsed;
        PanResults.Children.Clear();
        ShowStatus(Lang.Text("Main.Overlay.Searching", keyword), loading: true);

        try
        {
            var hits = await ModWikiSearch.SearchAsync(keyword, 8, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;

            if (hits.Count == 0)
            {
                ShowStatus(Lang.Text("Main.Overlay.NoResult", keyword));
                return;
            }

            HideStatus();
            foreach (var hit in hits) PanResults.Children.Add(BuildItem(hit));
        }
        catch (OperationCanceledException)
        {
            // 被新搜索覆盖
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 搜索失败", ModBase.LogLevel.Developer);
            ShowStatus(Lang.Text("Main.Overlay.Failed", ex.Message));
        }
    }

    private async Task ShowResultsAgainAsync()
    {
        _articleCts?.Cancel();
        _openTitle = null;
        BtnBack.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(TextSearch.Text))
            await RunSearchAsync().ConfigureAwait(true);
        else
        {
            PanResults.Children.Clear();
            ShowStatus(Lang.Text("Main.Overlay.Hint"));
        }
    }

    private UIElement BuildItem(WikiSearchHit hit)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = hit.Title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(hit.Snippet))
            stack.Children.Add(new TextBlock
            {
                Text = hit.Snippet,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                MaxHeight = 34
            });

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Child = stack
        };

        border.MouseLeftButtonUp += (_, _) => _ = OpenArticleAsync(hit);
        border.MouseEnter += (_, _) =>
            border.Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        border.MouseLeave += (_, _) =>
            border.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

        return border;
    }

    #endregion

    #region 正文

    private async Task OpenArticleAsync(WikiSearchHit hit)
    {
        _articleCts?.Cancel();
        _articleCts = new CancellationTokenSource();
        var token = _articleCts.Token;

        _openTitle = hit.Title;
        BtnBack.Visibility = Visibility.Visible;
        PanResults.Children.Clear();
        ShowStatus(Lang.Text("Main.Overlay.LoadingEntry", hit.Title), loading: true);

        try
        {
            var blocks = await ModWikiSearch.GetArticleAsync(hit.Title, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;

            if (blocks.Count == 0)
            {
                ShowStatus(Lang.Text("Main.Overlay.EmptyEntry"));
                return;
            }

            HideStatus();
            PanResults.Children.Clear();

            foreach (var block in blocks) PanResults.Children.Add(BuildBlock(block));

            PanScroll.ScrollToTop();
        }
        catch (OperationCanceledException)
        {
            // 被新的操作覆盖
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Overlay] 加载正文失败", ModBase.LogLevel.Developer);
            ShowStatus(Lang.Text("Main.Overlay.Failed", ex.Message));
        }
    }

    private UIElement BuildBlock(WikiBlock block)
    {
        var (size, weight, topMargin, color) = block.Kind switch
        {
            WikiBlockKind.Title => (18d, FontWeights.Bold, 0d, Colors.White),
            WikiBlockKind.Heading2 => (16d, FontWeights.Bold, 16d, Colors.White),
            WikiBlockKind.Heading3 => (14d, FontWeights.SemiBold, 12d, (Color)ColorConverter.ConvertFromString("#E8FFFFFF")),
            WikiBlockKind.Bullet => (13d, FontWeights.Normal, 2d, (Color)ColorConverter.ConvertFromString("#DCFFFFFF")),
            _ => (13d, FontWeights.Normal, 6d, (Color)ColorConverter.ConvertFromString("#ECFFFFFF"))
        };

        var text = block.Text;
        if (block.Kind == WikiBlockKind.Bullet) text = "· " + text;

        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = size * 1.55,
            Margin = new Thickness(0, topMargin, 0, 4)
        };

        if (block.Kind != WikiBlockKind.Heading2) return textBlock;

        var panel = new StackPanel();
        panel.Children.Add(textBlock);
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 2, 0, 8),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
        });
        return panel;
    }

    private void BtnBack_Click(object sender, EventArgs e) => _ = ShowResultsAgainAsync();

    #endregion

    #region 状态

    private void ShowStatus(string text, bool loading = false)
    {
        PanStatus.Visibility = Visibility.Visible;
        PanScroll.Visibility = Visibility.Collapsed;
        LabStatus.Text = text;
        Load.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideStatus()
    {
        PanStatus.Visibility = Visibility.Collapsed;
        Load.Visibility = Visibility.Collapsed;
        PanScroll.Visibility = Visibility.Visible;
    }

    #endregion
}