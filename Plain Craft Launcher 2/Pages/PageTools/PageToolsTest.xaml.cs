using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.App.Tools;
using PCL.Core.IO;
using PCL.Core.IO.Net;
using PCL.Core.UI;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Secret;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Network.Loaders;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;
using PCL.Core.Utils;
using System.Windows.Media;
using System.Globalization;

namespace PCL;

public partial class PageToolsTest
{
    private Bitmap currentSkinBitmap;
    private Bitmap generatedHeadBitmap;

    private int headSize = 64;
    private string skinPath = "";

    public PageToolsTest()
    {
        InitializeComponent();
        BtnSelectSkin.Click += BtnSelectSkin_Click;
        CmbHeadSize.SelectionChanged += CmbHeadSize_SelectionChanged;
        Loaded += (_, _) => MeLoaded();
        #if DEBUG
        BtnCrash.Visibility = Visibility.Visible;
        #endif
    }

    private void MeLoaded()
    {
        BtnDownloadStart.IsEnabled = false;

        TextDownloadFolder.Text = States.Tool.DownloadFolder;
        TextDownloadFolder.Validate();

        if (!string.IsNullOrEmpty(TextDownloadFolder.ValidateResult) || string.IsNullOrEmpty(TextDownloadFolder.Text))
            TextDownloadFolder.Text = ModBase.exePath + @"PCL\MyDownload\";

        TextDownloadFolder.Validate();
        TextDownloadName.Validate();
        TextDownloadUrl.Validate();
        StartButtonRefresh();
    }

    private void StartButtonRefresh()
    {
        BtnDownloadStart.IsEnabled = string.IsNullOrEmpty(TextDownloadFolder.ValidateResult) &&
                                     string.IsNullOrEmpty(TextDownloadUrl.ValidateResult) &&
                                     string.IsNullOrEmpty(TextDownloadName.ValidateResult);

        BtnDownloadOpen.IsEnabled = string.IsNullOrEmpty(TextDownloadFolder.ValidateResult);

        BtnAchievementPreview.IsEnabled = string.IsNullOrEmpty(AchievementBlockTextBox.ValidateResult) &&
                                          string.IsNullOrEmpty(AchievementTitleTextBox.ValidateResult) &&
                                          string.IsNullOrEmpty(AchievementString1TextBox.ValidateResult);

        BtnAchievementSave.IsEnabled = string.IsNullOrEmpty(AchievementBlockTextBox.ValidateResult) &&
                                       string.IsNullOrEmpty(AchievementTitleTextBox.ValidateResult) &&
                                       string.IsNullOrEmpty(AchievementString1TextBox.ValidateResult);
    }

    private void SaveCacheDownloadFolder(object sender, RoutedEventArgs e)
    {
        States.Tool.DownloadFolder = TextDownloadFolder.Text;
        TextDownloadName.Validate();
    }

    private static void DownloadState(ModLoader.LoaderCombo<int> loader)
    {
        try
        {
            switch (loader.State)
            {
                case ModBase.LoadState.Finished:
                {
                    HintService.Hint(Lang.Text("Tools.Test.CustomDownload.Finished", loader.name), HintType.Success);
                    Console.Beep();
                    break;
                }
                case ModBase.LoadState.Failed:
                {
                    ModBase.Log(
                        loader.Error,
                        $"{loader.name}失败",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
                    Console.Beep();
                    break;
                }
                case ModBase.LoadState.Aborted:
                {
                    HintService.Hint(Lang.Text("Tools.Test.CustomDownload.Aborted", loader.name));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
        }
    }

    public static void StartCustomDownload(string url, string fileName, string folder = null, string userAgent = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = SystemDialogs.SelectSaveFile(Lang.Text("Tools.Test.CustomDownload.SelectLocation"), fileName);
                if (!folder.Contains(@"\")) return;
                if (folder.EndsWith(fileName)) folder = folder[..^fileName.Length];
            }

            folder = folder.Replace("/", @"\").TrimEnd(new[] { '\\' }) + @"\";
            try
            {
                Directory.CreateDirectory(folder);
                ModBase.CheckPermissionWithException(folder);
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    $"访问文件夹失败（{folder}）",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
                return;
            }

            ModBase.Log("[Download] 自定义下载文件名：" + fileName);
            ModBase.Log("[Download] 自定义下载文件目标：" + folder);
            var uuid = ModBase.GetUuid();
            ModLoader.LoaderBase loaderdownload;
            if (new HttpValidator().Validate(url).IsValid)
                loaderdownload = new LoaderDownload(Lang.Text("Tools.Test.CustomDownload.LoaderName", fileName),
                    new List<DownloadFile> { new(new[] { url }, folder + fileName, null, true, userAgent) });
            else // UNC 路径
                loaderdownload = new LoaderDownloadUnc(Lang.Text("Tools.Test.CustomDownload.LoaderName", fileName),
                    new Tuple<string, string>(url, folder + fileName));
            var loaderCombo = new ModLoader.LoaderCombo<int>(Lang.Text("Tools.Test.CustomDownload.LoaderTitle", uuid), new[] { loaderdownload })
                { OnStateChanged = a => DownloadState((ModLoader.LoaderCombo<int>)a) };
            loaderCombo.Start();
            ModLoader.LoaderTaskbarAdd(loaderCombo);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始自定义下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
        }
    }

    public static void Jrrp()
    {
        var random = new Random(GenerateDailySeed());
        var luckValue = random.Next(0, 101);
        var rating = GetRating(luckValue);
        var title = Lang.Text("Tools.Test.Luck.MsgboxTitle", Lang.Date(DateTime.Now, "d"));

        if (luckValue >= 60)
            ModMain.MyMsgBox(Lang.Text("Tools.Test.Luck.MessageGood", luckValue, rating), title);
        else
            ModMain.MyMsgBox(Lang.Text("Tools.Test.Luck.MessageBad", luckValue, rating), title, isWarn: luckValue <= 30);
    }

    public static void RubbishClear()
    {
        ModBase.RunInUi(() =>
        {
            if (ModMain.frmToolsTest is not null && ModMain.frmToolsTest.BtnClear is not null)
                ModMain.frmToolsTest.BtnClear.IsEnabled = false;
        });
        // 只有当没有运行中的Minecraft游戏且启动器不在加载状态时才能清理

        // 清理的文件数量
        // 所有 Minecraft 文件夹


        // 寻找所有 Minecraft 文件夹

        // 删除 Minecraft 的缓存
        // 删除日志和崩溃报告并计数

        // 删除 Natives 文件

        // 删除 PCL 的缓存

        ModBase.RunInNewThread(() =>
        {
            try
            {
                if (!ModWatcher.hasRunningMinecraft && ModLaunch.mcLaunchLoader.State != ModBase.LoadState.Loading)
                {
                    if (ModNet.HasDownloadingTask())
                    {
                        HintService.Hint(Lang.Text("Tools.Test.Clean.WaitForDownload"));
                        return;
                    }

                    if (!ModFolder.mcFolderList.Any()) ModFolder.mcFolderListLoader.Start();
                    if (States.Hint.CleanJunkFile <= 2)
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Tools.Test.Clean.ConfirmMessage"),
                                Lang.Text("Tools.Test.Clean.ConfirmTitle"),
                                Lang.Text("Common.Action.Confirm"),
                                Lang.Text("Common.Action.Cancel")
                            ) == 2) return;
                        States.Hint.CleanJunkFile += 1;
                    }

                    var num = 0;
                    var cleanMcFolderList = new List<DirectoryInfo>();
                    if (!ModFolder.mcFolderList.Any()) ModFolder.mcFolderListLoader.WaitForExit();
                    foreach (var mcFolder in ModFolder.mcFolderList)
                    {
                        cleanMcFolderList.Add(new DirectoryInfo(mcFolder.Location));
                        var dirInfo = new DirectoryInfo(mcFolder.Location + "versions");
                        if (dirInfo.Exists)
                            foreach (var item in dirInfo.EnumerateDirectories())
                                cleanMcFolderList.Add(item);
                    }

                    foreach (var dirInfo in cleanMcFolderList)
                    {
                        num += ModBase.DeleteDirectory(
                            dirInfo.FullName + (dirInfo.FullName.EndsWith(@"\") ? "" : @"\") + @"crash-reports\", true);
                        num += ModBase.DeleteDirectory(
                            dirInfo.FullName + (dirInfo.FullName.EndsWith(@"\") ? "" : @"\") + @"logs\", true);
                        foreach (var fileInfo in dirInfo.EnumerateFiles("*"))
                            if (fileInfo.Name.StartsWith("hs_err_pid") || fileInfo.Name.EndsWith(".log") ||
                                fileInfo.Name == "WailaErrorOutput.txt")
                            {
                                fileInfo.Delete();
                                num += 1;
                            }

                        foreach (var dirInfo2 in dirInfo.EnumerateDirectories())
                            if ((dirInfo2.Name ?? "") == (dirInfo2.Name + "-natives" ?? "") ||
                                dirInfo2.Name == "natives-windows-x86_64")
                                num += ModBase.DeleteDirectory(dirInfo2.FullName, true);
                    }

                    num += ModBase.DeleteDirectory(ModBase.pathTemp, true);
                    num += ModBase.DeleteDirectory(Path.Combine(SystemPaths.DriveLetter, "ProgramData", "PCL"), true);
                    if (num != 0)
                    {
                        ModMain.MyMsgBox(Lang.Text("Tools.Test.Clean.ClearedMessage", num),
                            Lang.Text("Tools.Test.Clean.Cleared"), Lang.Text("Common.Action.Confirm"), "", "", false, true, true);
                        Process.Start(new ProcessStartInfo(Basics.ExecutablePath));
                        FormMain.EndProgramForce();
                    }
                    else
                    {
                        HintService.Hint(Lang.Text("Tools.Test.Clean.NoFiles"));
                    }
                }
                else
                {
                    HintService.Hint(Lang.Text("Tools.Test.Clean.CloseGameFirst"));
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "清理垃圾失败",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
            }
            finally
            {
                ModBase.RunInUiWait(() =>
                {
                    if (ModMain.frmToolsTest is not null && ModMain.frmToolsTest.BtnClear is not null)
                        ModMain.frmToolsTest.BtnClear.IsEnabled = true;
                });
            }
        }, "Rubbish Clear");
    }

    /// <summary>
    ///     随机获取一条提示。<br/>
    ///     分支说明：原社区版直接用「百宝箱不包含该功能」的占位文本顶替，
    ///     本分支改为真正读取内置提示库。
    /// </summary>
    public static string GetRandomCave() => PageLaunchRight.GetRandomHint(false, true);

    /// <summary>
    ///     随机获取一条提示。
    /// </summary>
    public static string GetRandomHint() => PageLaunchRight.GetRandomHint(false, true);

    /// <summary>
    ///     随机获取一条用于主页预设的提示（限制长度，避免撑爆布局）。
    /// </summary>
    public static string GetRandomPresetHint() => PageLaunchRight.GetRandomHint(true, true);

    private void TextDownloadUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TextDownloadName.Text) || string.IsNullOrEmpty(TextDownloadUrl.Text)) return;
            TextDownloadName.Text = ModBase.GetFileNameFromPath(WebUtility.UrlDecode(TextDownloadUrl.Text));
            StartButtonRefresh();
        }
        catch
        {
        }
    }

    private void MyTextButton_Click(object sender, EventArgs e)
    {
        var text = SystemDialogs.SelectFolder();
        if (!string.IsNullOrEmpty(text)) TextDownloadFolder.Text = text;
    }

    private void BtnDownloadOpen_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var text = TextDownloadFolder.Text;
            Directory.CreateDirectory(text);
            Basics.OpenPath(text);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "打开下载文件夹失败");
        }
    }

    private void BtnDownloadStart_Click(object sender, MouseButtonEventArgs e)
    {
        StartCustomDownload(TextDownloadUrl.Text, TextDownloadName.Text, TextDownloadFolder.Text);
        TextDownloadUrl.Text = "";
        TextDownloadUrl.Validate();
        TextDownloadUrl.ForceShowAsSuccess();
        TextDownloadName.Text = "";
        TextDownloadName.Validate();
        TextDownloadName.ForceShowAsSuccess();
        StartButtonRefresh();
    }

    private void TextDownloadUrl_ValidateChanged(object sender, RoutedEventArgs e)
    {
        StartButtonRefresh();
    }

    private void TextDownloadFolder_ValidateChanged(object sender, EventArgs e)
    {
        StartButtonRefresh();
    }

    private void TextDownloadName_ValidateChanged(object sender, EventArgs e)
    {
        StartButtonRefresh();
    }

    private void BtnClear_Click(object sender, MouseButtonEventArgs e)
    {
        RubbishClear();
    }

    // 下载正版玩家皮肤
    private void BtnSkinSave_Click(object sender, MouseButtonEventArgs e)
    {
        var id = TextSkinID.Text;
        HintService.Hint(Lang.Text("Tools.Test.Skin.Fetching"));
        ModBase.RunInNewThread(() =>
        {
            try
            {
                if (id.Length < 3)
                {
                    HintService.Hint(Lang.Text("Tools.Test.Skin.InvalidId"));
                }
                else
                {
                    var result = (string)ProfileUi.McLoginMojangUuid(id, true);
                    result = ModSkin.McSkinGetAddress(result, "Mojang");
                    result = ModSkin.McSkinDownload(result);
                    ModBase.RunInUi(() =>
                    {
                        var path = SystemDialogs.SelectSaveFile(Lang.Text("Tools.Test.Skin.Save"), $"{id}.png", Lang.Text("Tools.Test.Skin.FileFilter"));
                        ModBase.CopyFile(result, path);
                        HintService.Hint(Lang.Text("Tools.Test.Skin.Saved", id), HintType.Success);
                    });
                }
            }
            catch (Exception ex)
            {
                if (ex.ToString().Contains("429"))
                {
                    HintService.Hint(Lang.Text("Tools.Test.Skin.TooFrequent"), HintType.Error);
                    ModBase.Log($"获取正版皮肤失败（{id}）：获取皮肤太过频繁，请 5 分钟后再试！");
                }
                else
                {
                    ModBase.Log(ex, $"获取正版皮肤失败（{id}）");
                }
            }
        });
    }

    // 今日人品
    private void BtnLuck_Click(object sender, MouseButtonEventArgs e)
    {
        Jrrp();
    }

    public static int GenerateDailySeed()
    {
        var datePart = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        return DJB2Hash(datePart + Identify.LauncherId);
    }

    private static int DJB2Hash(string str)
    {
        var hash = 5381L;
        var prime = 33L;
        foreach (var c in str)
        {
            long charValue = c;
            hash = (hash * prime + charValue) % 0x100000000L;
        }

        return (int)(hash & 0x7FFFFFFFL);
    }

    public static string GetRating(int luckValue)
    {
        var key = luckValue switch
        {
            100 => "Tools.Test.Luck.Rating100",
            >= 95 => "Tools.Test.Luck.Rating95",
            >= 90 => "Tools.Test.Luck.Rating90",
            >= 60 => "Tools.Test.Luck.Rating60",
            >= 40 => "Tools.Test.Luck.Rating40",
            >= 30 => "Tools.Test.Luck.Rating30",
            >= 10 => "Tools.Test.Luck.Rating10",
            _ => "Tools.Test.Luck.Rating0"
        };

        return Lang.Text(key);
    }

    private void BtnCreateShortcut_Click(object sender, MouseButtonEventArgs e)
    {
        var shortcutName = Lang.Text("Tools.Test.Shortcut.FileName", ".lnk");
        var desktopName = Lang.Text("Tools.Test.Shortcut.Desktop");
        var startName = Lang.Text("Tools.Test.Shortcut.StartMenu");
        var desktop = Paths.GetSpecialPath(Environment.SpecialFolder.Desktop, shortcutName);
        var start = Paths.GetSpecialPath(Environment.SpecialFolder.StartMenu, @"Programs\" + shortcutName);
        var choice =
            ModMain.MyMsgBox(
                Lang.Text("Tools.Test.Shortcut.ConfirmMessage", desktopName, desktop, startName, start),
                Lang.Text("Tools.Test.Shortcut.SelectLocation"), Lang.Text("Common.Action.Cancel"), desktopName, startName);
        if (choice == 1)
            return;
        var shortcutPath = choice == 2 ? desktop : start;
        var locationName = choice == 2 ? desktopName : startName;
        Files.CreateShortcut(shortcutPath, Basics.ExecutablePath);
        HintService.Hint(Lang.Text("Tools.Test.Shortcut.Created", locationName), HintType.Success);
    }

    // 启动计数显示
    private void BtnLaunchCount_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.MyMsgBox(Lang.Text("Tools.Test.LaunchCount.Message", States.System.LaunchCount), Lang.Text("Tools.Test.LaunchCount.Title"));
    }

    private async void BtnAchievementPreview_Click(object sender, MouseButtonEventArgs e)
    {
        var url = GetAchievementUrl();
        ModBase.Log("[Net] 获取网络结果" + url);
        await LoadImageAsync(url);
    }

    private async Task LoadImageAsync(string imageUrl)
    {
        var client = NetworkService.GetClient();
        try
        {
            var response = await client.GetAsync(imageUrl);
            if (response.IsSuccessStatusCode)
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.StreamSource = stream;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();

                    Dispatcher.Invoke(() =>
                    {
                        AchievementImage.Source = bitmapImage;
                        AchievementImage.Visibility = Visibility.Visible;
                    });
                }
            else if (response.StatusCode == HttpStatusCode.NotFound)
                Dispatcher.Invoke(() =>
                {
                    ModBase.Log("获取成就图片失败（404）");
                    HintService.Hint(Lang.Text("Tools.Test.Achievement.FetchFailed"), HintType.Error);
                });

            else
                Dispatcher.Invoke(() => ModBase.Log("获取成就图片失败（" + (int)response.StatusCode + "）"));
        }

        catch (Exception ex)
        {
            Dispatcher.Invoke(() => ModBase.Log(ex, "获取成就图片失败"));
        }
    }

    private async void BtnAchievementSave_Click(object sender, MouseButtonEventArgs e)
    {
        var url = GetAchievementUrl();
        await DownloadImageToLocalAsync(url);
    }

    private async Task DownloadImageToLocalAsync(string imageUrl)
    {
        var savePath = ModBase.pathTemp + @"Download\" + ModBase.GetHash(imageUrl) + ".png";
        var client = NetworkService.GetClient();
        try
        {
            // 异步发送 GET 请求
            var response = await client.GetAsync(imageUrl);

            // 如果响应状态码是成功的，则继续
            if (response.IsSuccessStatusCode)
            {
                // 异步读取响应内容为字节流
                var imageBytes = await response.Content.ReadAsByteArrayAsync();

                // 将字节写入本地文件
                File.WriteAllBytes(savePath, imageBytes);

                var path =
                    SystemDialogs.SelectSaveFile(Lang.Text("Tools.Test.Achievement.Save"), AchievementTitleTextBox.Text + ".png", Lang.Text("Tools.Test.Achievement.FileFilter"));
                if (string.IsNullOrEmpty(path))
                {
                    ModBase.Log("用户取消了保存操作");
                    File.Delete(savePath);
                    return;
                }

                ModBase.CopyFile(savePath, path);
                File.Delete(savePath);
                HintService.Hint(Lang.Text("Tools.Test.Achievement.Saved"), HintType.Success);
            }
            // 下载成功，返回 True
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 捕获 404 错误
                ModBase.Log("获取成就图片失败（404）");
                HintService.Hint(Lang.Text("Tools.Test.Achievement.FetchFailed"), HintType.Error);
            }
            else
            {
                // 处理其他非成功状态码
                ModBase.Log("获取成就图片失败（" + (int)response.StatusCode + "）");
            }
        }

        catch (Exception ex)
        {
            // 捕获所有其他异常（如网络连接问题）
            ModBase.Log(ex, "获取成就图片失败");
        }
    }

    private string GetAchievementUrl()
    {
        var block = AchievementBlockTextBox.Text.Trim();
        var title = AchievementTitleTextBox.Text.Replace(" ", "..");
        var str1 = AchievementString1TextBox.Text.Replace(" ", "..");
        var str2 = AchievementString2TextBox.Text.Replace(" ", "..");
        var url = $"https://minecraft-api.com/api/achivements/{block}/{title}/{str1}";
        if (!string.IsNullOrEmpty(str2)) url += $"/{str2}";
        return url;
    }

    private void BtnCrash_Click(object sender, MouseButtonEventArgs e)
    {
        throw new Exception(Lang.Text("Tools.Test.Crash.ManualCrash"));
    }

    #region 千万别点

    /// <summary>
    ///     「千万别点」：完整复刻原版 PCL 百宝箱中的同名彩蛋。<br/>
    ///     原版行为（已通过反编译官方 2.99 版核对）：
    ///     <list type="number">
    ///         <item><description>从 8 种效果中随机抽取一种（愚人节当天固定追加「打开视频」）。</description></item>
    ///         <item><description>抽到「眼瞎白」时，如果同时抽到警告，会先弹癫痫警告；取消则直接隐藏按钮。</description></item>
    ///         <item><description>无论抽到什么都先弹「PCL 作者不会受理……」的最后警告。</description></item>
    ///         <item><description>执行效果，并把按钮隐藏。</description></item>
    ///     </list>
    ///     与原版唯一的区别：本分支不检查赞助等级，所有效果均可触发。
    /// </summary>
    private void BtnDontClick_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            /* 原版逻辑（反编译自官方 2.99 版 BtnClick_Click）：
               list = { RandomInteger(0, 7) }
               While RandomInteger(0, 1) = 1 : list.Add(RandomInteger(1, 6))
               注意这里是【循环】而不是一次判断——每追加一次都会重新掷骰子，
               因此有 50% 概率追加 1 个、25% 概率 2 个、12.5% 概率 3 个……
               所以确实会同时触发多个效果，叠加效果受 2^N 衰减。
               （RandomInteger 为闭区间，NextInt 语义相同） */
            var effects = new List<int> { RandomUtils.NextInt(0, 7) };
            while (RandomUtils.NextInt(0, 1) == 1) effects.Add(RandomUtils.NextInt(1, 6));

            // 愚人节当天固定追加「打开视频」
            if (DateTime.Now.Month == 4 && DateTime.Now.Day == 1)
                effects = new List<int> { 7 };

            // 抽到「眼瞎白」（编号 0）时的癫痫警告。<br/>
            // 说明：官方反编译代码在这里写的是 list.Contains(1)，但按 case 映射
            //   case 0 → ThemeDontClick = 1（眼瞎白，会闪屏，正是警告针对的对象）
            //   case 1 → ThemeDontClick = 2（滑稽彩，只是彩色，不会闪）
            // 所以官方的 Contains(1) 与实际闪屏效果（case 0）是错位的。
            // 这里按警告文案的真实意图改为判断 0，使「会闪瞎眼」时才警告。
            if (effects.Contains(0))
            {
                if (ModMain.MyMsgBox(
                        Lang.Text("Tools.Test.DontClick.Warn.Message"),
                        Lang.Text("Tools.Test.DontClick.Warn.Title"),
                        Lang.Text("Common.Action.Confirm"),
                        Lang.Text("Common.Action.Cancel"),
                        isWarn: true) == 2)
                {
                    BtnDontClick.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            // 无论如何都给出最后警告。
            // 分支说明：必须让这个弹窗真正阻塞，否则效果会在它显示之前就执行完。
            // 官方这里是三个「确定」按钮，天然满足 button2 非空 → 阻塞等待。
            ModMain.MyMsgBox(Lang.Text("Tools.Test.DontClick.LastWarn.Message"),
                Lang.Text("Tools.Test.DontClick.Warn.Title"),
                Lang.Text("Common.Action.Confirm"),
                Lang.Text("Common.Action.Confirm"),
                Lang.Text("Common.Action.Confirm"), isWarn: true);

            var main = ModMain.frmMain;
            if (main is null) return;

            foreach (var effect in effects)
            {
                switch (effect)
                {
                    // 分支说明：效果编号严格对应官方反编译结果：
                    //   case 0 => ThemeDontClick = 1（眼瞎白）
                    //   case 1 => ThemeDontClick = 2（真·滑稽彩）
                    //   case 2 => 界面镜像翻转
                    //   case 3 => 放大到全屏
                    //   case 4 => 缩小
                    //   case 5 => 无限旋转
                    //   case 6 => 无限平移并绕屏环绕
                    //   case 7 => 打开视频
                    case 0: // 眼瞎白
                        SetCorruption(ThemeService.ThemeCorruption.Blinding);
                        break;

                    case 1: // 真·滑稽彩
                        SetCorruption(ThemeService.ThemeCorruption.Random);
                        break;

                    case 2: // 界面镜像翻转
                        if (main.PanBack.LayoutTransform is not null)
                            main.PanBack.RenderTransformOrigin = new System.Windows.Point(1.25d, 1.25d);
                        switch (RandomUtils.NextInt(0, 2))
                        {
                            case 0:
                                main.PanBack.RenderTransform = new ScaleTransform(1d, -1d);
                                break;
                            case 1:
                                main.PanBack.RenderTransform = new ScaleTransform(-1d, -1d);
                                break;
                            case 2:
                                main.PanBack.RenderTransform = new ScaleTransform(-1d, 1d);
                                break;
                        }

                        break;

                    case 3: // 放大到全屏并重置位置
                        main.isSizeSaveable = false;
                        main.PanBack.LayoutTransform = new ScaleTransform(2.5d, 2.5d);
                        main.Height = SystemParameters.WorkArea.Height - 200d;
                        main.Width = SystemParameters.WorkArea.Width - 200d;
                        main.Left = 0d;
                        main.Top = 0d;
                        ModAnimation.AniStop("Don't Click Scale");
                        break;

                    case 4: // 缩小并保持
                        main.isSizeSaveable = false;
                        main.RenderTransform = new ScaleTransform();
                        ModAnimation.AniStart(
                            ModAnimation.AaScaleTransform(main, -0.85d, 5000, 0,
                                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
                            "Don't Click Scale");
                        ModAnimation.AniStop("Don't Click Rotate");
                        break;

                    case 5: // 无限旋转
                        main.RenderTransform = new RotateTransform();
                        ModAnimation.AniStart(
                            ModAnimation.AaRotateTransform(main, 1000000d * RandomUtils.PickRandom(new[] { 1, -1 }), 10000000),
                            "Don't Click Rotate");
                        ModAnimation.AniStop("Don't Click Scale");
                        break;

                    case 6: // 无限平移，并且绕屏环绕
                        main.isSizeSaveable = false;
                        if (RandomUtils.NextInt(0, 1) != 0)
                            ModAnimation.AniStart(
                                ModAnimation.AaX(main, 10000000d * RandomUtils.PickRandom(new[] { 1, -1 }), 30000000),
                                "Don't Click Move X");
                        else
                            ModAnimation.AniStart(
                                ModAnimation.AaY(main, 10000000d * RandomUtils.PickRandom(new[] { 1, -1 }), 50000000),
                                "Don't Click Move Y");

                        ModBase.RunInNewThread(() =>
                        {
                            while (true)
                            {
                                ModBase.RunInUi(() =>
                                {
                                    var screen = SystemParameters.PrimaryScreenWidth;
                                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                                    if (main.Top < 0d - main.Height) main.Top = screenHeight + 49d;
                                    if (main.Top > screenHeight + 50d) main.Top = 0d - main.Height + 1d;
                                    if (main.Left < 0d - main.Width) main.Left = screen + 49d;
                                    if (main.Left > screen + 50d) main.Left = 0d - main.Width + 1d;
                                    if (main.Width < 1d) main.Width = 1d;
                                    if (main.Height < 1d) main.Height = 1d;
                                });
                                Thread.Sleep(10);
                            }
                        }, "Don't Click Move", ThreadPriority.BelowNormal);
                        break;

                    case 7: // 打开视频
                        ModBase.OpenWebsite("https://www.bilibili.com/video/BV1GJ411x7h7");
                        break;
                }
            }

            // 原版：无论抽到什么，按钮都会被隐藏
            BtnDontClick.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, Lang.Text("Tools.Test.DontClick.Error"), ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
        }
    }

    /// <summary>
    ///     设置「千万别点」的异常配色，并立即把新配色应用到界面上。<br/>
    ///     分支说明：CE 的 <see cref="ThemeManager.ThemeRefresh" /> 只负责背景与右键菜单，
    ///     不会重新计算资源字典里的主题色，因此这里必须显式调用
    ///     <see cref="ThemeService.ApplyColorResources" />，否则界面上看不到任何变化。
    /// </summary>
    private static void SetCorruption(ThemeService.ThemeCorruption mode)
    {
        ThemeService.Corruption = mode;
        ModBase.Log($"[Don't Click] 已切换异常配色：{mode}");
        // 重新计算并写入 ColorObject* / ColorBrush* 资源，这一步才是真正改变界面的关键
        ThemeService.ApplyColorResources();
        // 再刷新顶部栏、背景与右键菜单等依赖主题色的部分
        ThemeManager.ThemeRefresh();
    }

    #endregion

    private int GetHeadSize() => CmbHeadSize.SelectedIndex switch
    {
        0 => 64,
        1 => 96,
        2 => 128,
        _ => 64
    };

    private void BtnSelectSkin_Click(object sender, RoutedEventArgs e)
    {
        var filePath = SystemDialogs.SelectFile(Lang.Text("Tools.Test.Avatar.FileFilter"),
            Lang.Text("Tools.Test.Avatar.SelectSkinFile"));
        if (!string.IsNullOrEmpty(filePath)) LoadAndGenerateHead(filePath);
    }

    private void LoadAndGenerateHead(string skinPath)
    {
        try
        {
            using (var stream = new FileStream(skinPath, FileMode.Open, FileAccess.Read))
            {
                currentSkinBitmap = new Bitmap(stream);
            }

            this.skinPath = skinPath;

            if (currentSkinBitmap.Width != currentSkinBitmap.Height)
            {
                HintService.Hint(Lang.Text("Tools.Test.Avatar.InvalidSize"), HintType.Error);
                SkinPreviewBorder.Visibility = Visibility.Collapsed;
                return;
            }

            generatedHeadBitmap = GenerateHeadFromSkin(currentSkinBitmap);

            ImgFace.Source = BitmapToBitmapImage(generatedHeadBitmap);
            ImgHair.Source = null;

            SkinPreviewBorder.Visibility = Visibility.Visible;
            HintService.Hint(Lang.Text("Tools.Test.Avatar.Generated"), HintType.Success);
        }

        catch (Exception ex)
        {
            ModBase.Log(ex, "生成头像失败");
            HintService.Hint(Lang.Text("Tools.Test.Avatar.GenerateFailed", ex.Message), HintType.Error);
            SkinPreviewBorder.Visibility = Visibility.Collapsed;
        }
    }

    private Bitmap GenerateHeadFromSkin(Bitmap skinBitmap)
    {
        var scale = skinBitmap.Width / 64;
        headSize = GetHeadSize();
        var headBitmap = new Bitmap(headSize, headSize);

        using (var g = Graphics.FromImage(headBitmap))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            DrawFaceLayer(g, skinBitmap, scale);
            if (skinBitmap.Width >= 64) DrawHairLayer(headBitmap, skinBitmap, scale);
        }

        return headBitmap;
    }

    private void DrawFaceLayer(Graphics g, Bitmap skinBitmap, int scale)
    {
        var faceRect = new Rectangle(8 * scale, 8 * scale, 8 * scale, 8 * scale);
        var faceSize = headSize - headSize / 8;
        var faceScaled = new Bitmap(faceSize, faceSize);

        using (var gFace = Graphics.FromImage(faceScaled))
        {
            gFace.InterpolationMode = InterpolationMode.NearestNeighbor;
            gFace.PixelOffsetMode = PixelOffsetMode.Half;
            gFace.DrawImage(skinBitmap, new Rectangle(0, 0, faceSize, faceSize), faceRect, GraphicsUnit.Pixel);
        }

        var offset = headSize / 16;
        g.DrawImage(faceScaled, offset, offset, faceSize, faceSize);
    }

    private void DrawHairLayer(Bitmap headBitmap, Bitmap skinBitmap, int scale)
    {
        var hairRect = new Rectangle(40 * scale, 8 * scale, 8 * scale, 8 * scale);
        var hairScaled = new Bitmap(headSize, headSize);

        using (var gHair = Graphics.FromImage(hairScaled))
        {
            gHair.InterpolationMode = InterpolationMode.NearestNeighbor;
            gHair.PixelOffsetMode = PixelOffsetMode.Half;
            gHair.DrawImage(skinBitmap, new Rectangle(0, 0, headSize, headSize), hairRect, GraphicsUnit.Pixel);
        }

        for (int x = 0, loopTo = headSize - 1; x <= loopTo; x++)
        for (int y = 0, loopTo1 = headSize - 1; y <= loopTo1; y++)
        {
            var pixel = hairScaled.GetPixel(x, y);
            if (pixel.A > 0) headBitmap.SetPixel(x, y, pixel);
        }
    }

    private void BtnSaveHead_Click(object sender, MouseButtonEventArgs e)
    {
        if (generatedHeadBitmap is null)
        {
            HintService.Hint(Lang.Text("Tools.Test.Avatar.SelectFirst"), HintType.Error);
            return;
        }

        var savePath = SystemDialogs.SelectSaveFile(Lang.Text("Tools.Test.Avatar.Save"), "Head.png");

        if (string.IsNullOrEmpty(savePath))
            return;

        generatedHeadBitmap.Save(savePath, ImageFormat.Png);
        HintService.Hint(Lang.Text("Tools.Test.Avatar.Saved"), HintType.Success);
    }

    private void CmbHeadSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (currentSkinBitmap is not null && skinPath is not null) LoadAndGenerateHead(skinPath);
    }

    private BitmapImage BitmapToBitmapImage(Bitmap bitmap)
    {
        using (var memoryStream = new MemoryStream())
        {
            bitmap.Save(memoryStream, ImageFormat.Png);
            memoryStream.Position = 0L;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = memoryStream;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            return bitmapImage;
        }
    }

    private void TextDownloadFolder_OnValidatedTextChanged(object sender, RoutedEventArgs e)
    {
        SaveCacheDownloadFolder(sender, e);
        TextDownloadName_ValidateChanged(sender, e);
    }
}
