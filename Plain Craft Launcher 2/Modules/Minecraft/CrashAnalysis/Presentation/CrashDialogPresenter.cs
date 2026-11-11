using System.Globalization;
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

        if ((!_HasSolution() || ModAiLogAnalysis.AlwaysShow) && ModAiLogAnalysis.IsAvailable)
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

    /// <summary>内置分析是否给出了结论。没有结论时才提议让 AI 再看一遍。</summary>
    private bool _HasSolution() => context.Result?.HasFinding == true;

    /// <summary>把日志交给 AI 分析，并把结果弹出来。</summary>
    /// <summary>把日志交给 AI 分析，并把结果弹出来。</summary>
    private async Task _AnalyzeWithAiAsync(bool retried = false)
    {
        try
        {
            // 分支说明：Hint 是几秒就消失的提示，而请求最长要等超时时间（默认 120s）。
            // 只闪一下就没了，用户会以为界面卡住。这里等待期间每 3 秒重刷一次提示，
            // 关键步骤另有日志输出，保证过程可见。
            using var hintCts = new CancellationTokenSource();
            var hintTask = _KeepAnalyzingHintAsync(hintCts.Token);

            string text;
            try
            {
                var log = context.LogAll;
                if (string.IsNullOrWhiteSpace(log)) log = context.PreparedLogs?.All ?? "";
                text = await ModAiLogAnalysis.AnalyzeAsync(log).ConfigureAwait(false);
            }
            finally
            {
                hintCts.Cancel();
                await hintTask.ConfigureAwait(false);
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

    private static async Task _KeepAnalyzingHintAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HintWrapper.Show(Lang.Text("Crash.Ai.Analyzing"), HintTheme.Info);
            try
            {
                await Task.Delay(3000, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
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