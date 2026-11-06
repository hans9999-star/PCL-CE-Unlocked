using System.Collections.ObjectModel;
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

            ModBase.RunInUi(() =>
            {
                CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListLoading");
                StackPlayerList.Children.Clear();
                CurrentSubpage = Subpages.PanSelect;
            });
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

    private void BtnAgreeEula_Click(object sender, MouseButtonEventArgs e)
    {
        States.Link.LinkEula = true;
        CurrentSubpage = Subpages.PanSelect;
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
        ModBase.RunInUi(() =>
        {
            LabFinishId.Text = LobbyService.CurrentLobbyCode;
            StackPlayerList.Children.Clear();
            foreach (var player in LobbyService.Players)
                StackPlayerList.Children.Add((UIElement)PlayerInfoItem(player, PlayerInfoClick));
        });
    }

    private async void OnServerShuttedDownHandler()
    {
        try
        {
            await LobbyService.LeaveLobbyAsync();

            ModBase.RunInUi(() =>
            {
                CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListLoading");
                StackPlayerList.Children.Clear();
                CurrentSubpage = Subpages.PanSelect;
            });
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
            LabFinishQuality.Text = Lang.Text("Tools.GameLink.Finish.Connected");
            LabFinishPing.Text = Lang.Text("Tools.GameLink.Finish.PingMs", latency);
            LabConnectType.Text = Lang.Text("Tools.GameLink.Finish.Unavailable");
        });
    }

    private void OnUserStopGame()
    {
        ModBase.RunInUi(() =>
        {
            CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListLoading");
            StackPlayerList.Children.Clear();
            CurrentSubpage = Subpages.PanSelect;
        });
        ModMain.MyMsgBox(Lang.Text("Tools.GameLink.Exit.Disbanded"), Lang.Text("Tools.GameLink.Exit.DisbandedTitle"));
    }


    private void OnPlayersChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        ModBase.Log("接收到玩家列表改变事件");
        ModBase.RunInUi(() =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems is not null)
                        foreach (PlayerProfile player in e.NewItems)
                            StackPlayerList.Children.Add((UIElement)PlayerInfoItem(player, PlayerInfoClick));
                    break;
                case NotifyCollectionChangedAction.Remove:
                    if (e.OldItems is not null)
                        foreach (PlayerProfile player in e.OldItems)
                        {
                            var itemToRemove = StackPlayerList.Children.OfType<MyListItem>()
                                .FirstOrDefault(item => ((PlayerProfile)item.Tag).MachineId == player.MachineId);
                            if (itemToRemove is not null) StackPlayerList.Children.Remove(itemToRemove);
                        }

                    break;
                default:
                    StackPlayerList.Children.Clear();
                    foreach (var player in LobbyService.Players)
                        StackPlayerList.Children.Add((UIElement)PlayerInfoItem(player, PlayerInfoClick));
                    break;
            }

            LabFinishQuality.Text = Lang.Text("Tools.GameLink.Finish.Connected");
            CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListCount", LobbyService.Players.Count);
        });
    }


    private void OnDiscoveredWorldsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        LogWrapper.Info("[Lobby] Found new world changes");

        ModBase.RunInUi(() =>
        {
            #region 处理集合变更

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Reset:
                    ComboWorldList.Items.Clear();
                    foreach (var world in LobbyService.DiscoveredWorlds)
                        ComboWorldList.Items.Add(new MyComboBoxItem
                        {
                            Tag = world.Port,
                            Content = world.Name
                        });
                    break;

                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems is not null)
                        foreach (FoundWorld world in e.NewItems)
                            ComboWorldList.Items.Add(new MyComboBoxItem
                            {
                                Tag = world.Port,
                                Content = world.Name
                            });

                    break;

                case NotifyCollectionChangedAction.Remove:
                    if (e.OldItems is not null)
                    {
                        // 使用 HashSet 提高查询效率
                        var portsToRemove = e.OldItems.Cast<FoundWorld>().Select(w => w.Port).ToHashSet();
                        var itemsToRemove = ComboWorldList.Items
                            .Cast<MyComboBoxItem>()
                            .Where(item => portsToRemove.Contains((int)item.Tag))
                            .ToList();

                        foreach (var item in itemsToRemove) ComboWorldList.Items.Remove(item);
                    }

                    break;
            }

            #endregion

            #region 更新 UI 状态

            var hasItems = ComboWorldList.Items.Count > 0;
            ComboWorldList.IsEnabled = hasItems;
            BtnCreate.IsEnabled = hasItems;

            if (hasItems && ComboWorldList.SelectedIndex == -1) ComboWorldList.SelectedIndex = 0;

            #endregion
        });
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

    #region UI 元素

    private object PlayerInfoItem(PlayerProfile info, MyListItem.ClickEventHandler onClick)
    {
        var details = info.Kind == PlayerKind.HOST
            ? Lang.Text("Tools.GameLink.Player.Details", Lang.Text("Tools.GameLink.Player.Host"), info.Vendor)
            : info.Vendor;

        var newItem = new MyListItem
        {
            Title = info.Name,
            Info = details,
            Type = MyListItem.CheckType.Clickable,
            Tag = info
        };
        newItem.Click += onClick;

        return newItem;
    }

    private void PlayerInfoClick(object sender, MouseButtonEventArgs e)
    {
        var info = (PlayerProfile)((MyListItem)sender).Tag;
        ModMain.MyMsgBox(Lang.Text("Tools.GameLink.Player.InfoMessage", info.Name, info.Vendor), Lang.Text("Tools.GameLink.Player.InfoTitle", info.Name));
    }

    #endregion

    private void PasteLobbyId(object sender, MouseButtonEventArgs e)
    {
        string lobbyId;
        try
        {
            lobbyId = Clipboard.GetText(TextDataFormat.Text);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "从剪贴板识别大厅编号出错");
            return;
        }

        if (!string.IsNullOrEmpty(lobbyId))
            TextJoinLobbyId.Text = lobbyId;
        else
            HintService.Hint(Lang.Text("Tools.GameLink.Join.InvalidText"));
    }

    private void ClearLobbyId(object sender, MouseButtonEventArgs e)
    {
        TextJoinLobbyId.Text = string.Empty;
    }

    #endregion

    #region PanSelect | 种类选择页面

    // 刷新按钮
    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        var lobby = LobbyService.DiscoverWorldAsync();
    }

    private async void BtnInputPort_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            BtnInputPort.IsEnabled = false;
            if (!ModLink.LobbyPrecheck()) return;
            var input = ModMain.MyMsgBoxInput(Lang.Text("Tools.GameLink.Create.EnterPort"),
                validateRules: [new IntValidator(65535,1024)]);
            int port;
            if (int.TryParse(input, out port))
                using (var ping = McPingServiceFactory.CreateService("127.0.0.1", port, 5000))
                {
                    var res = await ping.PingAsync();
                    if (res is not null && res.Version.Protocol != 0)
                        await CreateLobbyAsync(port);
                    else
                        HintService.Hint(Lang.Text("Tools.GameLink.Create.NotMcPort"), HintType.Error);
                }
        }
        finally
        {
            BtnInputPort.IsEnabled = true;
        }
    }

    // 创建大厅
    private async void BtnCreate_Click(object sender, MouseButtonEventArgs e)
    {
        if (ComboWorldList.SelectedItem is null)
        {
            HintService.Hint(Lang.Text("Tools.GameLink.Create.NoWorld"));
            return;
        }

        BtnCreate.IsEnabled = false;

        if (!ModLink.LobbyPrecheck())
        {
            BtnCreate.IsEnabled = true;
            return;
        }

        var port = (int)((MyComboBoxItem)ComboWorldList.SelectedItem).Tag;
        await CreateLobbyAsync(port);
    }

    private async Task CreateLobbyAsync(int port)
    {
        ModBase.Log("[Link] 创建大厅，端口：" + port);


        var username = LobbyInfoProvider.GetUsername();

        ModBase.RunInUi(() =>
        {
            BtnFinishPing.Visibility = Visibility.Collapsed;
            LabFinishPing.Text = "-ms";
            BtnConnectType.Visibility = Visibility.Collapsed;
            LabConnectType.Text = Lang.Text("Tools.GameLink.Finish.Connecting");
            CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListLoading");
            StackPlayerList.Children.Clear();
            LabConnectUserName.Text = username;
            LabConnectUserType.Text = Lang.Text("Tools.GameLink.Finish.Host");
            LabFinishId.Text = LobbyService.CurrentLobbyCode;
            BtnFinishCopyIp.Visibility = Visibility.Collapsed;
            BtnCreate.IsEnabled = true;
            BtnFinishExit.Text = Lang.Text("Tools.GameLink.Finish.CloseLobby");
            CurrentSubpage = Subpages.PanFinish;
        });

        var res = await LobbyService.CreateLobbyAsync(port, username).ConfigureAwait(true);

        if (!res)
            ModBase.RunInUi(() =>
            {
                CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListLoading");
                StackPlayerList.Children.Clear();
                CurrentSubpage = Subpages.PanSelect;
            });
    }

    // 加入大厅
    private async void BtnJoin_Click(object sender, MouseButtonEventArgs e)
    {
        if (!ModLink.LobbyPrecheck())
            return;

        ModBase.Log("Start to join lobby.");

        var id = TextJoinLobbyId.Text;
        var username = LobbyInfoProvider.GetUsername();

        ModBase.RunInUi(() =>
        {
            BtnFinishPing.Visibility = Visibility.Visible;
            LabFinishPing.Text = "-ms";
            BtnConnectType.Visibility = Visibility.Visible;
            LabConnectType.Text = Lang.Text("Tools.GameLink.Finish.Connecting");
            CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListLoading");
            StackPlayerList.Children.Clear();
            LabConnectUserName.Text = username;
            LabConnectUserType.Text = Lang.Text("Tools.GameLink.Finish.Guest");
            LabFinishId.Text = id;
            BtnFinishCopyIp.Visibility = Visibility.Visible;
            CurrentSubpage = Subpages.PanFinish;
        });

        var res = await LobbyService.JoinLobbyAsync(id, username).ConfigureAwait(true);

        if (!res)
            ModBase.RunInUi(() =>
            {
                CardPlayerList.Title = Lang.Text("Tools.GameLink.Member.ListLoading");
                StackPlayerList.Children.Clear();
                CurrentSubpage = Subpages.PanSelect;
            });
    }

    private void TextJoinLobbyId_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnJoin_Click(sender, null);
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
    private async void BtnFinishExit_Click(object sender, ModBase.RouteEventArgs routeEventArgs)
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
            BtnFinishExit.Text = Lang.Text("Tools.GameLink.Finish.Exit");
            await LobbyService.LeaveLobbyAsync().ConfigureAwait(true);
        }
    }

    // 复制大厅编号
    private void BtnFinishCopy_Click(object sender, ModBase.RouteEventArgs routeEventArgs)
    {
        ModBase.ClipboardSet(LabFinishId.Text);
    }

    // 复制 IP
    private void BtnFinishCopyIp_Click(object sender, ModBase.RouteEventArgs routeEventArgs)
    {
        var port = LobbyInfoProvider.McForward?.LocalEndPoint?.Port;
        if (port == null) return;
        var ip = $"127.0.0.1:{port}";
        ModMain.MyMsgBox(Lang.Text("Tools.GameLink.CopyIp.Message", ip),
            Lang.Text("Tools.GameLink.CopyIp.Title"),
            Lang.Text("Common.Action.Copy"),
            Lang.Text("Tools.GameLink.CopyIp.Back"),
            button1Action: () => ModBase.ClipboardSet(ip));
    }

    #endregion

    #region 子页面管理

    public enum Subpages
    {
        PanEula,
        PanSelect,
        PanFinish
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
    } = Subpages.PanSelect; // 分支说明：本改版已移除 Natayark 登录，不再展示协议页，直接进入创建/加入界面

    private void PageLinkLobby_OnPageEnter()
    {
        ModMain.frmLinkMain.PanEula.Visibility =
            CurrentSubpage == Subpages.PanEula ? Visibility.Visible : Visibility.Collapsed;
        ModMain.frmLinkMain.PanSelect.Visibility =
            CurrentSubpage == Subpages.PanSelect ? Visibility.Visible : Visibility.Collapsed;
        ModMain.frmLinkMain.PanFinish.Visibility =
            CurrentSubpage == Subpages.PanFinish ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion
}
