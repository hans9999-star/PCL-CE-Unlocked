using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.Link;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Lobby;
using PCL.Core.Link.McPing;
using PCL.Core.Link.Natayark;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Link.Scaffolding.EasyTier;
using PCL.Core.Logging;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageLinkMain
{
    // 分支说明：PCL2 原版联机界面所需的少量状态
    private int _serverPort;          // 建房使用的端口
    private int _lastDiscoveredPort;  // 最近发现的局域网世界端口（用于预填端口弹窗）
    private string? _lastCode;        // 上一次使用的邀请码
    private bool _finishIsHost;       // 完成页展示「已创建房间」还是「已加入房间」

    static PageLinkMain()
    {
        initLoader = new ModLoader.LoaderCombo<int>(Lang.Text("Link.Mod.Task.InitLobby"),
            new[] { new ModLoader.LoaderTask<int, int>(Lang.Text("Common.Action.Initialize"), InitTask) { ProgressWeight = 0.5d } });
    }

    public PageLinkMain()
    {
        InitializeComponent();
        LoaderInit();
        Loaded += (_, _) => Reload();
        PageEnter += PageLinkLobby_OnPageEnter;
    }

    #region 初始化

    // 加载器初始化
    private void LoaderInit()
    {
        PageLoaderInit(Load, PanLoad, PanContent, null, initLoader, autoRun: false);
        // 注册自定义的 OnStateChanged
        initLoader.OnStateChangedUi += OnLoadStateChanged;

        LobbyService.OnNeedDownloadEasyTier += () => ModLink.DownloadEasyTier();
        LobbyService.DiscoveredWorlds.CollectionChanged += OnDiscoveredWorldsChanged;
        LobbyService.Players.CollectionChanged += OnPlayersChanged;
        LobbyService.OnUserStopGame += OnUserStopGame;
        LobbyService.OnClientPing += OnClientPingHandler;
        LobbyService.OnServerShutDown += OnServerShuttedDownHandler;
        LobbyService.OnServerStarted += OnServerStartedHandler;
        LobbyService.OnServerException += OnServerExceptionHandler;
        // 本地开服：把服务端输出接到本页控制台
        ModServerHost.OutputReceived += AppendServerOutput;
        ModServerHost.Exited += () => ModBase.RunInUi(RefreshServerHostStatus);

        if (lobbyAnnouncementLoader is null)
        {
            var loaders = new List<ModLoader.LoaderBase>();
            loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Link.Mod.Task.InitLobbyUi"), _ => ModBase.RunInUi(() =>
            {
                HintAnnounce.Visibility = Visibility.Visible;
                HintAnnounce.Theme = MyHint.Themes.Blue;
                HintAnnounce.Text = Lang.Text("Tools.GameLink.Loading.ConnectingServer");
            })));
            loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Link.Mod.Task.FetchAnnouncement"), _ => GetAnnouncement()) { ProgressWeight = 0.5d });
            lobbyAnnouncementLoader = new ModLoader.LoaderCombo<int>("Lobby Announcement", loaders) { show = false };
        }
    }

    private async void OnServerExceptionHandler(Exception ex)
    {
        ModBase.RunInUi(() => HintService.Hint(
            Lang.Text("Tools.GameLink.Error.ServerMessage", ex.Message),
            HintType.Error));

        try
        {
            await LobbyService.LeaveLobbyAsync();

            ModBase.RunInUi(() => CurrentSubpage = Subpages.PanSelect);
        }
        catch (Exception secEx)
        {
            ModBase.Log(secEx, "Occurred an exception when exit server.");
            HintService.Hint(Lang.Text("Tools.GameLink.Error.ServerExit"), HintType.Error);
        }
    }

    public async void Reload()
    {
        HintAnnounce.Visibility = Visibility.Visible;
        HintAnnounce.Text = Lang.Text("Tools.GameLink.Loading.ConnectingServer");
        HintAnnounce.Theme = MyHint.Themes.Blue;

        if (_linkAnnounceUpdateCancelSource is not null)
            _linkAnnounceUpdateCancelSource.Cancel();
        _linkAnnounceUpdateCancelSource = new CancellationTokenSource();
        BeginAnnouncementRequest();
        _linkAnnounces.Clear();

        // 加载公告
        _announcementLoadState = AnnouncementLoadState.Loading;
        lobbyAnnouncementLoader.Start(isForceRestart: true);
        await Dispatcher.BeginInvoke(new Action(async () =>
            await _LinkAnnounceUpdateAsync())); // 我实在不理解为啥 BeginInvoke 这个委托要 MustBeInherit

        await LobbyService.InitializeAsync().ConfigureAwait(false);
    }

    private static readonly ModLoader.LoaderCombo<int> initLoader;

    private static async void InitTask(ModLoader.LoaderTask<int, int> task)
    {
        await LobbyService.InitializeAsync();
    }

    #region Subscribser

    private void OnServerStartedHandler()
    {
        ModBase.Log("Received server started event.");
        ModBase.RunInUi(UpdateFinishPanel);
    }

    private async void OnServerShuttedDownHandler()
    {
        try
        {
            await LobbyService.LeaveLobbyAsync();

            ModBase.RunInUi(() => CurrentSubpage = Subpages.PanSelect);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "Occurred an exception when exit server.");
            HintService.Hint(Lang.Text("Tools.GameLink.Error.ServerExit"), HintType.Error);
        }
    }

    private void OnClientPingHandler(long latency)
    {
        ModBase.RunInUi(() =>
        {
            LabFinishPing.Text = Lang.Text("Tools.GameLink.Link.Finish.PingUnit", latency);
        });
    }

    private void OnUserStopGame()
    {
        ModBase.RunInUi(() => CurrentSubpage = Subpages.PanSelect);
        ModMain.MyMsgBox(Lang.Text("Tools.GameLink.Exit.Disbanded"), Lang.Text("Tools.GameLink.Exit.DisbandedTitle"));
    }


    private void OnPlayersChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        ModBase.Log("接收到玩家列表改变事件");
        ModBase.RunInUi(() =>
        {
            LabFinishPlayer.Text =
                Lang.Text("Tools.GameLink.Link.Finish.PlayerUnit", Math.Max(1, LobbyService.Players.Count));
        });
    }


    private void OnDiscoveredWorldsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        LogWrapper.Info("[Lobby] Found new world changes");
        // 分支说明：按 PCL2 原版，创建房间用弹窗输入端口；
        // 这里只记住最近发现的局域网世界端口，用于预填弹窗。
        var world = LobbyService.DiscoveredWorlds.LastOrDefault();
        if (world is not null) _lastDiscoveredPort = world.Port;
    }

    #endregion

    #endregion

    #region 公告

    public static ModLoader.LoaderCombo<int> lobbyAnnouncementLoader;
    private readonly ObservableCollection<LinkAnnounceInfo> _linkAnnounces = new();

    private CancellationTokenSource _linkAnnounceUpdateCancelSource;
    private readonly object _announcementRequestLock = new();
    private int _announcementRequestId;
    private AnnouncementLoadState _announcementLoadState;

    private enum AnnouncementLoadState
    {
        Loading,
        Loaded,
        Failed
    }

    // 公告轮播实现
    private async Task _LinkAnnounceUpdateAsync()
    {
        var currentIndex = 0;
        var globalCancelToken = _linkAnnounceUpdateCancelSource.Token;
        CancellationTokenSource waiterCts = null;

        _linkAnnounces.CollectionChanged += (sender, e) =>
        {
            if (waiterCts is not null) waiterCts.Cancel();
        };

        while (!globalCancelToken.IsCancellationRequested)
        {
            waiterCts = CancellationTokenSource.CreateLinkedTokenSource(globalCancelToken);
            var waiterCancelToken = waiterCts.Token;

            if (_announcementLoadState == AnnouncementLoadState.Failed)
            {
                // 错误提示由请求失败处理设置，轮播不应覆盖它。
            }
            else if (_linkAnnounces.Count > 0)
            {
                HintAnnounce.Visibility = Visibility.Visible;
                var info = _linkAnnounces[currentIndex];
                string prefix;
                if (info.Type == LinkAnnounceType.Important)
                {
                    HintAnnounce.Theme = MyHint.Themes.Red;
                    prefix = Lang.Text("Tools.GameLink.Announcement.Important");
                }
                else if (info.Type == LinkAnnounceType.Warning)
                {
                    HintAnnounce.Theme = MyHint.Themes.Yellow;
                    prefix = Lang.Text("Tools.GameLink.Announcement.Warning");
                }
                else
                {
                    HintAnnounce.Theme = MyHint.Themes.Blue;
                    prefix = Lang.Text("Tools.GameLink.Announcement.Notice");
                }

                HintAnnounce.Text = Lang.Text("Tools.GameLink.Announcement.Format", prefix,
                    info.Content.Replace("\n", "\r\n"));
            }
            else if (_announcementLoadState == AnnouncementLoadState.Loaded)
            {
                HintAnnounce.Visibility = Visibility.Collapsed;
            }

            try
            {
                await Task.Delay(10000, waiterCancelToken);
            }
            catch (TaskCanceledException)
            {
                // 忽略取消任务的异常
            }

            if (!waiterCancelToken.IsCancellationRequested)
                currentIndex += 1;
            if (currentIndex >= _linkAnnounces.Count)
                currentIndex = 0;
            waiterCts = null;
        }
    }

    // 获取公告信息
    private void GetAnnouncement()
    {
        var requestId = Volatile.Read(ref _announcementRequestId);
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var serverNumber = 0;
                JsonObject jObj = null;

                #region 多服务器轮询获取公告

                while (serverNumber < Secrets.LinkServers.Length)
                    try
                    {
                        // 获取缓存版本号
                        var cacheRes = Requester.Fetch($"{Secrets.LinkServers[serverNumber]}/api/link/v2/cache.ini",
                            new FetchParam
                            {
                                Method = "GET",
                                ContentType = "application/json",
                                Timeout = 7000
                            }).Trim();
                        var cacheVer = int.Parse(cacheRes);

                        if (cacheVer == States.Link.AnnounceCacheVer)
                        {
                            LogWrapper.Info("[Link] Using cached announcement data");
                            jObj = (JsonObject)ModBase.GetJson(States.Link.AnnounceCache);
                        }
                        else
                        {
                            LogWrapper.Info("[Link] Fetching new announcement data");
                            var received = Requester.Fetch(
                                $"{Secrets.LinkServers[serverNumber]}/api/link/v2/announce.json",
                                new FetchParam
                                {
                                    Method = "GET",
                                    ContentType = "application/json",
                                    Timeout = 7000
                                });
                            jObj = (JsonObject)ModBase.GetJson(received);

                            // 更新缓存
                            if (!TryApplyCurrentAnnouncementRequest(requestId, () =>
                                {
                                    States.Link.AnnounceCache = received;
                                    States.Link.AnnounceCacheVer = cacheVer;
                                })) return;
                        }

                        break; // 成功获取，跳出轮询
                    }
                    catch (Exception ex)
                    {
                        LogWrapper.Error(ex, $"[Link] Failed to get announcement from server {serverNumber}");
                        if (!TryApplyCurrentAnnouncementRequest(requestId, () =>
                            {
                                States.Link.AnnounceCacheConfig.Reset();
                                States.Link.AnnounceCacheVerConfig.Reset();
                            })) return;
                        serverNumber++;
                    }

                #endregion

                if (jObj is null) throw new Exception("Failed to fetch lobby data");
                if (!IsCurrentAnnouncementRequest(requestId)) return;

                #region 解析基础状态与版本限制

                LobbyInfoProvider.IsLobbyAvailable = (bool)jObj["available"];
                LobbyInfoProvider.AllowCustomName = (bool)jObj["allowCustomName"];
                LobbyInfoProvider.RequiresLogin = (bool)jObj["requireLogin"];
                LobbyInfoProvider.RequiresRealName = (bool)jObj["requireRealname"];

                if (jObj["version"].ToObject<double>() > LobbyInfoProvider.ProtocolVersion)
                {
                    ModBase.RunInUi(() =>
                    {
                        if (!IsCurrentAnnouncementRequest(requestId)) return;
                        _announcementLoadState = AnnouncementLoadState.Failed;
                        HintAnnounce.Visibility = Visibility.Visible;
                        HintAnnounce.Theme = MyHint.Themes.Red;
                        HintAnnounce.Text = Lang.Text("Tools.GameLink.Error.UpdateRequired");
                        LobbyInfoProvider.IsLobbyAvailable = false;
                    });
                    return;
                }

                #endregion

                #region 解析公告列表 (Notices)

                var notices = (JsonArray)jObj["notices"];
                var announcements = new List<LinkAnnounceInfo>();
                foreach (JsonObject notice in notices)
                {
                    if (!IsCurrentAnnouncementRequest(requestId)) return;
                    var content = notice["content"]?.ToString();
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    // 版本过滤
                    var minVer = notice["minVer"].ToObject<double>();
                    var maxVer = notice["maxVer"].ToObject<double>();
                    if (ModBase.versionCode < minVer || ModBase.versionCode > maxVer) continue;

                    // 类型映射
                    var type = LinkAnnounceType.Notice;
                    var typeStr = notice["type"]?.ToString().ToLower();
                    if (typeStr == "important" || typeStr == "red") type = LinkAnnounceType.Important;
                    else if (typeStr == "warning" || typeStr == "yellow") type = LinkAnnounceType.Warning;

                    // 按行拆分公告
                    foreach (var announce in content.Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(announce)) continue;
                        announcements.Add(new LinkAnnounceInfo(type, announce));
                    }
                }

                #endregion

                #region 解析中继服务器 (Relays)

                var relays = (JsonArray)jObj["relays"];
                ETRelay.RelayList = new List<ETRelay>();
                foreach (var relay in relays)
                    ETRelay.RelayList.Add(new ETRelay
                    {
                        Name = relay["name"]?.ToString(),
                        Url = relay["url"]?.ToString(),
                        Type = relay["type"]?.ToString() == "official" ? ETRelayType.Selfhosted : ETRelayType.Community
                    });

                #endregion

                ModBase.RunInUi(() =>
                {
                    if (!IsCurrentAnnouncementRequest(requestId)) return;
                    _linkAnnounces.Clear();
                    foreach (var announcement in announcements)
                        _linkAnnounces.Add(announcement);
                    _announcementLoadState = AnnouncementLoadState.Loaded;
                    if (_linkAnnounces.Count == 0)
                        HintAnnounce.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                if (!IsCurrentAnnouncementRequest(requestId)) return;
                LobbyInfoProvider.IsLobbyAvailable = false;
                ModBase.RunInUi(() =>
                {
                    if (!IsCurrentAnnouncementRequest(requestId)) return;
                    _announcementLoadState = AnnouncementLoadState.Failed;
                    HintAnnounce.Visibility = Visibility.Visible;
                    HintAnnounce.Theme = MyHint.Themes.Red;
                    HintAnnounce.Text = Lang.Text("Tools.GameLink.Error.ConnectFailed");
                });
                LogWrapper.Error(ex, "[Link] Failed to get lobby announcement");
            }
        });
    }

    private void BeginAnnouncementRequest()
    {
        lock (_announcementRequestLock)
            _announcementRequestId++;
    }

    private bool IsCurrentAnnouncementRequest(int requestId)
    {
        lock (_announcementRequestLock)
            return requestId == _announcementRequestId;
    }

    private bool TryApplyCurrentAnnouncementRequest(int requestId, Action action)
    {
        lock (_announcementRequestLock)
        {
            if (requestId != _announcementRequestId) return false;
            action();
            return true;
        }
    }

    #endregion

    #region 信息获取与展示

    #endregion

    #region PanSelect | 创建 / 加入房间

    // 创建房间：按 PCL2 原版弹窗输入端口
    private async void BtnCreate_Click(object sender, MouseButtonEventArgs e)
    {
        PanSelectCreate.IsEnabled = false;
        try
        {
            if (!ModLink.LobbyPrecheck()) return;

            var input = ModMain.MyMsgBoxInput(
                Lang.Text("Tools.GameLink.Link.InputPortTitle"),
                Lang.Text("Tools.GameLink.Link.InputPortDesc"),
                _lastDiscoveredPort > 0 ? _lastDiscoveredPort.ToString() : "",
                hintText: Lang.Text("Tools.GameLink.Link.InputPortHint"));
            if (string.IsNullOrWhiteSpace(input)) return;
            if (!int.TryParse(input.Trim(), out var port) || port < 1024 || port > 65535)
            {
                HintService.Hint(Lang.Text("Tools.GameLink.Link.InvalidPort"), HintType.Error);
                return;
            }

            using (var ping = McPingServiceFactory.CreateService("127.0.0.1", port, 5000))
            {
                var res = await ping.PingAsync();
                if (res is null || res.Version.Protocol == 0)
                {
                    HintService.Hint(Lang.Text("Tools.GameLink.Create.NotMcPort"), HintType.Error);
                    return;
                }
            }

            await CreateLobbyAsync(port);
        }
        finally
        {
            PanSelectCreate.IsEnabled = true;
        }
    }

    // 加入房间：按 PCL2 原版弹窗输入邀请码
    private async void BtnJoin_Click(object sender, MouseButtonEventArgs e)
    {
        PanSelectJoin.IsEnabled = false;
        try
        {
            if (!ModLink.LobbyPrecheck()) return;

            var code = ModMain.MyMsgBoxInput(
                Lang.Text("Tools.GameLink.Link.InputCodeTitle"),
                Lang.Text("Tools.GameLink.Link.InputCodeDesc"),
                _lastCode ?? "",
                hintText: Lang.Text("Tools.GameLink.Link.InputCodeHint"));
            if (string.IsNullOrWhiteSpace(code)) return;

            _lastCode = code.Trim();
            await JoinLobbyAsync(_lastCode);
        }
        finally
        {
            PanSelectJoin.IsEnabled = true;
        }
    }

    private async Task CreateLobbyAsync(int port)
    {
        ModBase.Log("[Link] 创建大厅，端口：" + port);
        _serverPort = port;
        var username = LobbyInfoProvider.GetUsername();

        ModBase.RunInUi(() =>
        {
            _finishIsHost = true;
            CurrentSubpage = Subpages.PanFinish;
        });

        var res = await LobbyService.CreateLobbyAsync(port, username).ConfigureAwait(true);

        if (!res)
            ModBase.RunInUi(() => CurrentSubpage = Subpages.PanSelect);
        else
            UpdateFinishPanel();
    }

    private async Task JoinLobbyAsync(string code)
    {
        ModBase.Log("Start to join lobby.");
        var username = LobbyInfoProvider.GetUsername();

        ModBase.RunInUi(() =>
        {
            _finishIsHost = false;
            CurrentSubpage = Subpages.PanFinish;
        });

        var res = await LobbyService.JoinLobbyAsync(code, username).ConfigureAwait(true);

        if (!res)
            ModBase.RunInUi(() => CurrentSubpage = Subpages.PanSelect);
        else
            UpdateFinishPanel();
    }

    /// <summary>
    ///     按 PCL2 原版文案刷新完成页：标题、描述（创建时直接写出邀请码）、
    ///     IP / 端口 / 玩家数 / 延迟，以及「关闭 / 离开」按钮。
    /// </summary>
    private void UpdateFinishPanel()
    {
        ModBase.RunInUi(() =>
        {
            var code = LobbyService.CurrentLobbyCode ?? _lastCode ?? "";
            var players = Math.Max(1, LobbyService.Players.Count);
            var localPort = LobbyInfoProvider.McForward?.LocalEndPoint?.Port;
            var showPort = _finishIsHost ? _serverPort : localPort ?? _serverPort;

            // 分支说明：与 PCL2 2.11.2 一致，信息栏只显示数值（图标已表达含义）
            LabFinishTitle.Text = Lang.Text(_finishIsHost
                ? "Tools.GameLink.Link.Finish.Created"
                : "Tools.GameLink.Link.Finish.Joined");
            LabFinishDesc.Text = Lang.Text(_finishIsHost
                ? "Tools.GameLink.Link.Finish.CreatedDesc"
                : "Tools.GameLink.Link.Finish.JoinedDesc", code);
            // 分支说明：完全按 PCL2 2.11.2 的逻辑——建房端只显示端口，加入端只显示 IP
            BtnFinishPort.Visibility = _finishIsHost ? Visibility.Visible : Visibility.Collapsed;
            LabFinishPort.Visibility = _finishIsHost ? Visibility.Visible : Visibility.Collapsed;
            BtnFinishIp.Visibility = _finishIsHost ? Visibility.Collapsed : Visibility.Visible;
            LabFinishIp.Visibility = _finishIsHost ? Visibility.Collapsed : Visibility.Visible;
            LabFinishPort.Text = showPort > 0 ? showPort.ToString() : "-";
            LabFinishIp.Text = localPort is null ? "-" : $"127.0.0.1:{localPort}";
            LabFinishPlayer.Text = Lang.Text("Tools.GameLink.Link.Finish.PlayerUnit", players);
            LabFinishPing.Text = Lang.Text("Tools.GameLink.Link.Finish.PingConnecting");
            BtnFinishCopy.Visibility = _finishIsHost ? Visibility.Visible : Visibility.Collapsed;
            BtnFinishExit.Text = Lang.Text(_finishIsHost
                ? "Tools.GameLink.Link.Finish.Close"
                : "Tools.GameLink.Link.Finish.Leave");
        });
    }

    #endregion

    #region PanLoad | 加载中页面

    // 承接状态切换的 UI 改变
    private void OnLoadStateChanged(ModLoader.LoaderBase loader, ModBase.LoadState newState, ModBase.LoadState oldState)
    {
    }

    private static string _loadStep = "准备初始化";

    private static void SetLoadDesc(string intro, string step)
    {
        ModBase.Log("连接步骤：" + intro);
        _loadStep = step;
        ModBase.RunInUiWait(() =>
        {
            if (ModMain.frmLinkMain is null || !ModMain.frmLinkMain.LabLoadDesc.IsLoaded)
                return;
            ModMain.frmLinkMain.LabLoadDesc.Text = intro;
            ModMain.frmLinkMain.UpdateProgress();
        });
    }

    // 承接重试
    private void CardLoad_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (initLoader.State != ModBase.LoadState.Failed)
            return;
        initLoader.Start(isForceRestart: true);
    }

    // 取消加载
    private void CancelLoad(object sender, EventArgs eventArgs)
    {
        if (initLoader.State == ModBase.LoadState.Loading)
        {
            CurrentSubpage = Subpages.PanSelect;
            initLoader.Abort();
        }
        else
        {
            initLoader.State = ModBase.LoadState.Waiting;
        }
    }

    // 进度改变
    private void UpdateProgress(double value = -1)
    {
        if (value == -1)
            value = initLoader.Progress;
        var displayingProgress = ColumnProgressA.Width.Value;
        if (Math.Round(value - displayingProgress, 3) == 0d)
            return;
        if (displayingProgress > value)
        {
            ColumnProgressA.Width = new GridLength(value, GridUnitType.Star);
            ColumnProgressB.Width = new GridLength(1d - value, GridUnitType.Star);
            ModAnimation.AniStop("LobbyController Progress");
        }
        else
        {
            var newProgress = value == 1d ? 1d : (value - displayingProgress) * 0.2d + displayingProgress;
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaGridLengthWidth(ColumnProgressA, newProgress - ColumnProgressA.Width.Value, 300,
                        ease: new ModAnimation.AniEaseOutFluent()),
                    ModAnimation.AaGridLengthWidth(ColumnProgressB, 1d - newProgress - ColumnProgressB.Width.Value, 300,
                        ease: new ModAnimation.AniEaseOutFluent())
                }, "LobbyController Progress");
        }
    }

    private void CardResized(object sender, SizeChangedEventArgs sizeChangedEventArgs)
    {
        RectProgressClip.Rect = new Rect(0d, 0d, CardLoad.ActualWidth, 12d);
    }

    #endregion

    #region PanFinish | 加载完成页面

    // 退出
    private async void BtnFinishExit_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.MyMsgBox(
                Lang.Text(LobbyService.IsHost
                    ? "Tools.GameLink.Exit.ConfirmMessageWithHost"
                    : "Tools.GameLink.Exit.ConfirmMessage"),
                Lang.Text("Tools.GameLink.Exit.ConfirmTitle"),
                Lang.Text("Common.Action.Confirm"),
                Lang.Text("Common.Action.Cancel"),
                isWarn: true
            ) == 1)
        {
            CurrentSubpage = Subpages.PanSelect;
            await LobbyService.LeaveLobbyAsync().ConfigureAwait(true);
        }
    }

    // 复制邀请码
    private void BtnFinishCopy_Click(object sender, MouseButtonEventArgs e)
    {
        var code = LobbyService.CurrentLobbyCode ?? _lastCode ?? "";
        if (string.IsNullOrWhiteSpace(code))
            return;
        ModBase.ClipboardSet(code);
        HintService.Hint(Lang.Text("Tools.GameLink.Link.Finish.Copied"));
    }

    // 点击复制 IP
    private void BtnFinishIp_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var port = LobbyInfoProvider.McForward?.LocalEndPoint?.Port;
        ModBase.ClipboardSet(port is null ? "127.0.0.1" : $"127.0.0.1:{port}");
        HintService.Hint(Lang.Text("Tools.GameLink.Link.Finish.Copied"));
    }

    // 点击复制端口
    private void BtnFinishPort_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var port = LobbyInfoProvider.McForward?.LocalEndPoint?.Port ?? _serverPort;
        if (port <= 0) return;
        ModBase.ClipboardSet(port.ToString());
        HintService.Hint(Lang.Text("Tools.GameLink.Link.Finish.Copied"));
    }

    #endregion

    #region 子页面管理

    public enum Subpages
    {
        PanSelect,
        PanFinish,
        PanServerHost
    }

    public Subpages CurrentSubpage
    {
        get => field;
        set
        {
            if (field == value)
                return;
            field = value;
            ModBase.Log("[Link] 子页面更改为 " + ModBase.GetStringFromEnum(value));
            PageOnContentExit();
        }
    } = Subpages.PanSelect; // 分支说明：按 PCL2 原版，进入页面直接是创建/加入，无协议页

    private void PageLinkLobby_OnPageEnter()
    {
        ModMain.frmLinkMain.PanSelect.Visibility =
            CurrentSubpage == Subpages.PanSelect ? Visibility.Visible : Visibility.Collapsed;
        ModMain.frmLinkMain.PanFinish.Visibility =
            CurrentSubpage == Subpages.PanFinish ? Visibility.Visible : Visibility.Collapsed;
        ModMain.frmLinkMain.PanServerHost.Visibility =
            CurrentSubpage == Subpages.PanServerHost ? Visibility.Visible : Visibility.Collapsed;
        if (CurrentSubpage == Subpages.PanServerHost) RefreshServerHostStatus();
    }

    #region 本地开服

    /// <summary>当前实例的开服端口（分享给朋友时要用）。</summary>
    private int _serverHostPort = 25565;

    private void BtnServerHost_Click(object sender, MouseButtonEventArgs e)
    {
        CurrentSubpage = Subpages.PanServerHost;
    }

    private void AppendServerOutput(string text)
    {
        ModBase.RunInUi(() =>
        {
            TextServerConsole.AppendText(text);
            PanServerConsole.ScrollToEnd();
        });
    }

    private void RefreshServerHostStatus()
    {
        var inst = PageInstanceLeft.McInstance;
        if (inst is null)
        {
            LabServerStatus.Text = Lang.Text("Instance.ServerHost.Status.NoInstance");
            BtnServerStart.IsEnabled = false;
            BtnServerStop.IsEnabled = false;
            BtnServerShare.IsEnabled = false;
            return;
        }

        var running = ModServerHost.IsRunning;
        var jarExists = File.Exists(ModServerHost.GetJarPath(inst.PathInstance));
        LabServerStatus.Text = (running
            ? Lang.Text("Instance.ServerHost.Status.Running")
            : jarExists
                ? Lang.Text("Instance.ServerHost.Status.Ready")
                : Lang.Text("Instance.ServerHost.Status.NoJar"))
            + "  ·  " + inst.Name;
        BtnServerStart.IsEnabled = !running;
        BtnServerStop.IsEnabled = running;
        BtnServerShare.IsEnabled = running;
    }

    private async void BtnServerStart_Click(object sender, MouseButtonEventArgs e)
    {
        var inst = PageInstanceLeft.McInstance;
        if (inst is null) return;

        var dir = inst.PathInstance;
        var jarPath = ModServerHost.GetJarPath(dir);

        if (!File.Exists(jarPath))
        {
            var url = ModServerHost.GetServerJarUrl(dir, out var sha1, out var size);
            if (string.IsNullOrWhiteSpace(url))
            {
                HintService.Hint(Lang.Text("Instance.ServerHost.Error.NoServerDownload", inst.Name), HintType.Error);
                return;
            }

            BtnServerStart.IsEnabled = false;
            LabServerStatus.Text = Lang.Text("Instance.ServerHost.Status.Downloading");
            ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.DownloadingStart", inst.Name));
            if (!await ModServerHost.DownloadJarAsync(url, jarPath, size, sha1).ConfigureAwait(true))
            {
                HintService.Hint(Lang.Text("Instance.ServerHost.Error.DownloadFailed"), HintType.Error);
                RefreshServerHostStatus();
                return;
            }

            ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.Downloaded"));
        }

        var port = int.TryParse(TextServerPort.Text?.Trim(), out var p) && p is > 0 and < 65536 ? p : 25565;
        var maxPlayers = int.TryParse(TextServerMaxPlayers.Text?.Trim(), out var m) && m > 0 ? m : 20;
        var memory = int.TryParse(TextServerMemory.Text?.Trim(), out var mem) && mem >= 512 ? mem : 2048;
        var onlineMode = CheckServerOnlineMode.Checked != false;
        _serverHostPort = port;

        try
        {
            ModServerHost.PrepareFiles(dir, port, onlineMode, maxPlayers, "PCL CE Unlocked Server");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerHost] 准备服务端文件失败", ModBase.LogLevel.Hint);
            HintService.Hint(Lang.Text("Instance.ServerHost.Error.PrepareFailed"), HintType.Error);
            RefreshServerHostStatus();
            return;
        }

        ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.Starting",
            ModServerHost.GetJavaPath(), memory, port));
        if (!ModServerHost.Start(dir, jarPath, memory))
            HintService.Hint(Lang.Text("Instance.ServerHost.Error.StartFailed"), HintType.Error);

        RefreshServerHostStatus();
    }

    private async void BtnServerStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (!ModServerHost.IsRunning) return;
        BtnServerStop.IsEnabled = false;
        ModServerHost.Log(Lang.Text("Instance.ServerHost.Log.Stopping"));
        await ModServerHost.StopAsync().ConfigureAwait(true);
        RefreshServerHostStatus();
    }

    /// <summary>把本地服务端发布到联机大厅，朋友输邀请码即可进服。</summary>
    private async void BtnServerShare_Click(object sender, MouseButtonEventArgs e)
    {
        if (!ModServerHost.IsRunning)
        {
            HintService.Hint(Lang.Text("Instance.ServerHost.Error.NotRunning"));
            return;
        }

        if (!ModLink.LobbyPrecheck()) return;

        var username = LobbyInfoProvider.GetUsername();
        _finishIsHost = true;
        _serverPort = _serverHostPort;
        CurrentSubpage = Subpages.PanFinish;
        ModBase.RunInUi(() => BtnFinishCopy.Visibility = Visibility.Collapsed);

        var res = await LobbyService.CreateLobbyAsync(_serverHostPort, username).ConfigureAwait(true);
        if (!res)
        {
            CurrentSubpage = Subpages.PanServerHost;
            return;
        }

        UpdateFinishPanel();
    }

    private void BtnServerSend_Click(object sender, MouseButtonEventArgs e)
    {
        SendServerCommand();
    }

    private void TextServerCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SendServerCommand();
        e.Handled = true;
    }

    private void SendServerCommand()
    {
        var command = TextServerCommand.Text?.Trim();
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!ModServerHost.IsRunning)
        {
            HintService.Hint(Lang.Text("Instance.ServerHost.Error.NotRunning"));
            return;
        }

        ModServerHost.SendCommand(command);
        TextServerCommand.Text = string.Empty;
    }

    #endregion

    #endregion
}