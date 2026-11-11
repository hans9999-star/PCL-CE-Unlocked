using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI;

namespace PCL;

internal sealed class CrashDialogPresenter(CrashAnalysisContext context)
{
    private readonly CrashReportExporter _exporter = new();
    private readonly CrashResultFormatter _formatter = new();

    public void Output(
        bool isHandAnalyze,
        List<string>? extraFiles)
    {
        ModMain.frmMain.ShowWindowToTop();

        var crashContent = _formatter.Format(context, isHandAnalyze);
        var resultText = crashContent.Text;
        var directFile = context.DirectOpenFile;
        var openInstanceSettings =
            context.Instance is not null &&
            crashContent.SuggestedAction == CrashSuggestedAction.OpenInstanceSettings;

        var title = isHandAnalyze
            ? Lang.Text("Crash.Dialog.Title.Manual")
            : Lang.Text("Crash.Dialog.Title.Auto");

        var secondButtonText = _GetSecondButtonText(
            isHandAnalyze,
            directFile,
            openInstanceSettings);

        var thirdButtonText = isHandAnalyze
            ? ""
            : Lang.Text("Crash.Dialog.Button.ExportReport");

        var secondButtonAction = _GetSecondButtonAction(
            isHandAnalyze,
            directFile,
            openInstanceSettings);

        // 分支说明：内置分析是查表式的，遇到没收录过的崩溃只剩「未知错误」。
        // 这种情况下多给一个「让 AI 分析」——按钮集合是 params，不限 3 个。
        var buttons = new List<MsgBoxButtonInfo>
        {
            new(Lang.Text("Common.Action.Confirm"), 1),
            new(secondButtonText, 2, secondButtonAction),
            new(thirdButtonText, 3)
        };

        // 分支说明：原先只在「内置分析没结论」时才给这个按钮，现已按用户要求取消该限制，
        // 只要 AI 分析可用就始终显示，由用户自己决定要不要再问一次。
        if (ModAiLogAnalysis.IsAvailable)
            buttons.Add(new MsgBoxButtonInfo(Lang.Text("Crash.Dialog.Button.AiAnalyze"), 4,
                () => _ = _AnalyzeWithAiAsync()));

        var selectedButton = MsgBoxWrapper.ShowWithCustomButtons(
            resultText,
            title,
            MsgBoxTheme.Info,
            true,
            buttons);

        switch (selectedButton)
        {
            case 2:
                if (openInstanceSettings)
                    _OpenModLoaderInstallPage();
                else if (directFile is not null)
                    _OpenDirectFile(directFile);
                break;

            case 3:
                _ExportReport(extraFiles);
                break;
        }
    }


    /// <summary>把日志交给 AI 分析，并把结果弹出来。</summary>
    /// <summary>把日志交给 AI 分析，并把结果弹出来。</summary>
    /// <summary>把日志交给 AI 分析，期间显示进度窗，完成后弹出结果。</summary>
    /// <summary>把日志交给 AI 分析，期间显示进度窗，完成后弹出结果。</summary>
    private async Task _AnalyzeWithAiAsync(bool retried = false)
    {
        try
        {
            var log = context.LogAll;
            if (string.IsNullOrWhiteSpace(log)) log = context.PreparedLogs?.All ?? "";

            // 分支说明：请求最长要等超时时间（默认 120s），光靠几秒就消失的 toast
            // 看不出是在跑还是卡死。这里做一个外观与 PCL 消息框一致的进度窗：
            // 同样的圆角卡片、投影、23px 标题与分隔线，内容用 PCL 自己的 MyLoading
            // （镐子旋转动画）并实时显示已等待秒数，右下角可取消。
            using var cts = new CancellationTokenSource();
            var res = System.Windows.Application.Current.Resources;

            var title = new TextBlock
            {
                Text = Lang.Text("Crash.Ai.Title"),
                FontSize = 23,
                Margin = new Thickness(7, -1, 40, 9),
                Foreground = (Brush)res["ColorBrush2"]
            };
            var line = new System.Windows.Shapes.Rectangle { Height = 2, Fill = title.Foreground };
            var loading = new MyLoading
            {
                Foreground = (SolidColorBrush)res["ColorBrush3"],
                Margin = new Thickness(0, 8, 0, 0),
                MinHeight = 120
            };
            loading.LabText.Text = Lang.Text("Crash.Ai.Analyzing");
            var cancelButton = new MyButton
            {
                Text = Lang.Text("Common.Action.Cancel"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            cancelButton.Click += (_, _) => cts.Cancel();

            var stack = new StackPanel { Margin = new Thickness(22, 22, 22, 20) };
            stack.Children.Add(title);
            stack.Children.Add(line);
            stack.Children.Add(loading);
            stack.Children.Add(cancelButton);

            var card = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = (Brush)res["ColorBrushBackground"],
                Effect = new DropShadowEffect
                {
                    Color = (Color)res["ColorObjectMsgBoxShadow"],
                    BlurRadius = 20,
                    ShadowDepth = 2,
                    Opacity = 0.8,
                    RenderingBias = RenderingBias.Performance
                },
                Child = stack
            };

            var window = new Window
            {
                Title = Lang.Text("Crash.Ai.Title"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = null,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = ModMain.frmMain,
                Content = card
            };
            // 无边框窗口要自己支持拖动
            card.MouseLeftButtonDown += (_, _) =>
            {
                try
                {
                    window.DragMove();
                }
                catch
                {
                    // 拖动过程中的异常无需处理
                }
            };
            window.Show();

            // 计时刷新「已等待 N 秒」，让等待过程可见
            var startedAt = DateTime.Now;
            var ticker = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }

                    var seconds = (int)(DateTime.Now - startedAt).TotalSeconds;
                    ModBase.RunInUi(() =>
                    {
                        if (window.IsVisible) loading.LabText.Text = Lang.Text("Crash.Ai.Elapsed", seconds);
                    });
                }
            });

            string text;
            try
            {
                text = await ModAiLogAnalysis.AnalyzeAsync(log, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                cts.Cancel();
                await ticker.ConfigureAwait(false);
                ModBase.RunInUi(() =>
                {
                    if (window.IsVisible) window.Close();
                });
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                MsgBoxWrapper.Show(Lang.Text("Crash.Ai.Empty"),
                    Lang.Text("Crash.Ai.Title"), MsgBoxTheme.Warning);
                return;
            }

            MsgBoxWrapper.Show(text, Lang.Text("Crash.Ai.Title"), MsgBoxTheme.Info);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                LogWrapper.Info("Crash", "AI 分析被用户取消");
                return;
            }

            LogWrapper.Error(ex, "Crash", "AI 分析崩溃日志失败");

            // 401 = 缺 API Key；提到 model 的 400 = 模型名不被接受
            //（有的本地服务要求 provider/model 形式）。两者都就地让用户填一次再重试。
            var needKey = !retried && ex.Message.Contains("401");
            var needModel = !retried && ex.Message.Contains("400") &&
                            ex.Message.Contains("model", StringComparison.OrdinalIgnoreCase);
            if (needKey || needModel)
            {
                var key = Microsoft.VisualBasic.Interaction.InputBox(
                    Lang.Text(needKey ? "Crash.Ai.NeedKey" : "Crash.Ai.NeedModel"),
                    Lang.Text("Crash.Ai.Title"), "");
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (needKey) Config.Preference.Ai.ApiKey = key.Trim();
                    else Config.Preference.Ai.Model = key.Trim();
                    await _AnalyzeWithAiAsync(true).ConfigureAwait(false);
                    return;
                }
            }

            MsgBoxWrapper.Show(Lang.Text("Crash.Ai.Failed", ex.Message),
                Lang.Text("Crash.Ai.Title"), MsgBoxTheme.Error);
        }
    }

    private static string _GetSecondButtonText(
        bool isHandAnalyze,
        CrashLogEntry? directFile,
        bool openInstanceSettings)
    {
        if (isHandAnalyze || directFile is null)
            return "";

        return openInstanceSettings
            ? Lang.Text("Crash.Dialog.Button.GoToModify")
            : Lang.Text("Crash.Dialog.Button.OpenLog");
    }

    private static Action? _GetSecondButtonAction(
        bool isHandAnalyze,
        CrashLogEntry? directFile,
        bool openInstanceSettings)
    {
        if (isHandAnalyze ||
            directFile is null ||
            openInstanceSettings)
            return null;

        return () => _OpenDirectFile(directFile);
    }

    private void _OpenModLoaderInstallPage()
    {
        PageInstanceLeft.McInstance = context.Instance;

        ModBase.RunInUi(() => ModMain.frmMain.PageChange(
            FormMain.PageType.InstanceSetup,
            FormMain.PageSubType.VersionInstall));
    }

    private static void _OpenDirectFile(CrashLogEntry directFile)
    {
        if (File.Exists(directFile.FullPath))
        {
            Basics.OpenPath(directFile.FullPath);
            return;
        }

        var filePath = Path.Combine(Paths.Temp, "Crash.txt");

        CrashFileIo.WriteText(filePath, string.Join("\r\n", directFile.Lines));
        Basics.OpenPath(filePath);
    }

    private void _ExportReport(List<string>? extraFiles)
    {
        string? fileAddress = null;

        try
        {
            fileAddress = _SelectReportSavePath();

            if (string.IsNullOrEmpty(fileAddress))
                return;

            _exporter.Export(context, fileAddress, extraFiles);

            HintWrapper.Show(
                Lang.Text("Crash.Report.Export.Success"),
                HintTheme.Success);

            Basics.OpenPath(Path.GetDirectoryName(fileAddress) ?? fileAddress);
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Crash", "导出错误报告失败");

            var message = _CreateExportFailureMessage(fileAddress, ex);
            MsgBoxWrapper.ShowWithCustomButtons(
                message,
                Lang.Text("Crash.Export.Failed.Title"),
                MsgBoxTheme.Error,
                false,
                new MsgBoxButtonInfo(Lang.Text("Common.Action.Confirm"), 1),
                new MsgBoxButtonInfo(
                    Lang.Text("Crash.Export.Failed.CopyDetails"),
                    2,
                    () => ModBase.ClipboardSet(message, false)));
        }
    }

    private static string _CreateExportFailureMessage(
        string? targetZipPath,
        Exception exception)
    {
        var summary = string.IsNullOrWhiteSpace(targetZipPath)
            ? Lang.Text("Crash.Export.Failed.MessageWithoutPath")
            : Lang.Text("Crash.Export.Failed.Message", targetZipPath);

        return ExceptionDetails.Compose(summary, exception);
    }

    private static string? _SelectReportSavePath()
    {
        string? fileAddress = null;

        ModBase.RunInUiWait(() => fileAddress = SystemDialogs.SelectSaveFile(
            Lang.Text("Crash.Report.SaveDialog.Title"),
            _GetDefaultReportFileName(),
            Lang.Text("Crash.Report.SaveDialog.Filter")));

        return fileAddress;
    }

    private static string _GetDefaultReportFileName()
    {
        var time = DateTime.Now
            .ToString("G", CultureInfo.InvariantCulture)
            .Replace("/", "-")
            .Replace(":", ".")
            .Replace(" ", "_");

        return Lang.Text("Crash.Report.SaveDialog.DefaultFileName", time);
    }
}