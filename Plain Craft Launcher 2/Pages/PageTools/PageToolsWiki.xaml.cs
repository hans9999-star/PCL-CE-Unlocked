using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     Wiki 查询页。左栏搜索结果，右栏 WebView2 渲染条目。<br/>
///     分支说明：注入 CSS 隐藏 wiki 自带侧栏；页面背景色在运行时从启动器主题读取，
///     因此切换深浅色 / 自定义主题后会自动跟随。<br/>
///     WebView2 懒初始化，缺少 Runtime 时降级为系统浏览器。
/// </summary>
public partial class PageToolsWiki
{
    /// <summary>快捷搜索词。</summary>
    private static readonly string[] Suggestions = ["附魔", "合成", "红石", "下界合金", "村民"];

    /// <summary>
    ///     注入的样式。<br/>
    ///     实测 zh.minecraft.wiki 使用 Vector 旧版皮肤（body.skin-vector-legacy）：
    ///     #mw-panel 左侧导航、#mw-navigation 顶部菜单、#content.mw-body 正文。<br/>
    ///     背景色用占位符 __PCL_BG__ 注入时替换，取值来自启动器当前主题。
    /// </summary>
    private const string InjectedCss = """
                                       /* ═══ 隐藏左侧导航栏 ═══ */
                                       #mw-panel,
                                       .vector-legacy-sidebar,
                                       #mw-navigation,
                                       #mw-head,
                                       #p-personal,
                                       #p-logo {
                                           display: none !important;
                                       }

                                       /* ═══ 正文铺满（原本 margin-left 是给侧栏留的位置）═══ */
                                       #content,
                                       .mw-body {
                                           margin-left: 0 !important;
                                           border-left: none !important;
                                           max-width: none !important;
                                           width: auto !important;
                                       }

                                       #mw-page-base,
                                       #mw-head-base {
                                           display: none !important;
                                       }

                                       .mw-body-content {
                                           padding-right: 8px !important;
                                       }

                                       /* ═══ 去掉底部链接区与分类栏 ═══ */
                                       #footer .mw-footer-places,
                                       #footer ul,
                                       #catlinks {
                                           display: none !important;
                                       }

                                       /* ═══ 背景跟随启动器 ═══ */
                                       /* 分支说明：必须用 high specificity（html body ...）压过 wiki 自己的
                                          wgl-theme-light 皮肤规则；background 用 transparent 逐层清掉，
                                          只让 html/body 保留主题底色。 */
                                       html,
                                       body {
                                           background-color: __PCL_BG__ !important;
                                           background-image: none !important;
                                           color: __PCL_FG__ !important;
                                       }

                                       /* 所有后代强制透明背景 + 继承前景色，
                                          否则 wiki 给 p/li/div 单独设的深色字会压在深色底上 */
                                       html body * {
                                           background-color: transparent !important;
                                           background-image: none !important;
                                           color: inherit !important;
                                           border-color: __PCL_LINE__ !important;
                                       }

                                       /* ═══ 需要保留底色的块 ═══ */
                                       html body table,
                                       html body table *,
                                       html body pre,
                                       html body code,
                                       html body #toc,
                                       html body .toc,
                                       html body .infobox,
                                       html body .wikitable,
                                       html body .thumbinner,
                                       html body .thumbcaption,
                                       html body .navbox,
                                       html body .vertical-navbox {
                                           background-color: __PCL_SURFACE__ !important;
                                       }

                                       /* ═══ 链接 ═══ */
                                       html body a,
                                       html body a * {
                                           color: __PCL_LINK__ !important;
                                       }

                                       /* 不存在的页面链接（红链） */
                                       html body a.new,
                                       html body a.new * {
                                           color: #ff7b72 !important;
                                       }

                                       /* ═══ 标题 ═══ */
                                       html body h1,
                                       html body h2,
                                       html body h3,
                                       html body h4,
                                       html body h5,
                                       html body h6,
                                       html body #firstHeading {
                                           color: __PCL_FG__ !important;
                                           border-bottom-color: __PCL_LINE__ !important;
                                       }

                                       /* ═══ 目录浮层化 ═══ */
                                       html body #toc,
                                       html body .toc {
                                           max-width: 260px !important;
                                           float: left !important;
                                           margin: 0 12px 12px 0 !important;
                                       }

                                       /* ═══ 图片自适应 ═══ */
                                       html body #mw-content-text img {
                                           max-width: 100% !important;
                                           height: auto !important;
                                       }
                                       """;

    private CancellationTokenSource? _cts;
    private WebView2? _web;
    private bool _webReady;
    private bool _webFailed;
    private bool _compact = true;
    private bool _navigating;
    private DateTime _loadingStarted;
    private string _bgColor = "#fbfbfb";

    public PageToolsWiki()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _bgColor = ResolveLauncherBackground();
            BuildSuggestions();
            TextSearch.Focus();
        };
    }

    public new void PageOnEnter()
    {
        // 每次进入页面都刷新一次背景色，这样在设置里改完主题再回来能立刻生效
        _bgColor = ResolveLauncherBackground();
        PanWebHost.Background = new SolidColorBrush(ParseColor(_bgColor));
        // 页面背景已设为透明，底色来自 WebView2 自身的 DefaultBackgroundColor，
        // 主题改变后要同步过来，否则深色主题下会残留浅色底。
        if (_webReady && _web is not null)
        {
            var c = ParseColor(_bgColor);
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            _ = ApplyStylesAsync();
        }

        Dispatcher.BeginInvoke(new Action(() => TextSearch.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    #region 背景色

    /// <summary>
    ///     取启动器卡片的实际背景色，转成不透明的 #RRGGBB。<br/>
    ///     分支说明：卡片用的是 ColorBrushTransparentBackground（形如 #d2fbfbfb，带 alpha），
    ///     直接拿去当网页背景会让 WebView 透出后面的桌面。这里把 alpha 合成掉，
    ///     按「卡片色叠在启动器底色上」算出最终可见颜色。
    /// </summary>
    private static string ResolveLauncherBackground()
    {
        try
        {
            // 分支说明：不能只看 ColorBrushTransparentBackground —— 它是固定的 #d2fbfbfb，
            // 深色模式下也不会变。真正随主题变深的是 PanForm.Background：
            // 浅色时是纯色 ColorBrushBackground，深色时是 LCH 生成的渐变画刷。
            // 因此优先从 PanForm 实际使用的画刷取色。
            var formBrush = ModMain.frmMain?.PanForm?.Background;
            var baseColor = SampleBrush(formBrush);

            // 卡片背景是半透明的 ColorBrushTransparentBackground，叠在上面算最终色
            var cardBrush = System.Windows.Application.Current.TryFindResource("ColorBrushTransparentBackground")
                as SolidColorBrush;
            if (cardBrush is null) return ToHex(baseColor);

            var card = cardBrush.Color;
            var alpha = card.A / 255d;
            var r = (byte)Math.Round(card.R * alpha + baseColor.R * (1 - alpha));
            var g = (byte)Math.Round(card.G * alpha + baseColor.G * (1 - alpha));
            var b = (byte)Math.Round(card.B * alpha + baseColor.B * (1 - alpha));
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Wiki] 读取主题背景色失败", ModBase.LogLevel.Developer);
            return ThemeManager.IsDarkMode ? "#1e1e1e" : "#fbfbfb";
        }
    }

    /// <summary>从任意画刷里取一个代表色（渐变取第一个色标）。</summary>
    private static Color SampleBrush(Brush? brush)
    {
        switch (brush)
        {
            case SolidColorBrush solid:
                return solid.Color;
            case GradientBrush { GradientStops.Count: > 0 } gradient:
                return gradient.GradientStops[1].Color;   // 取中间那个，最接近整体观感
            default:
                return ThemeManager.IsDarkMode
                    ? Color.FromRgb(0x1e, 0x1e, 0x1e)
                    : Color.FromRgb(0xfb, 0xfb, 0xfb);
        }
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0xfb, 0xfb, 0xfb);
        }
    }

    #endregion

    #region 搜索

    private void BuildSuggestions()
    {
        if (PanSuggest.Children.Count > 0) return;
        foreach (var word in Suggestions)
        {
            var button = new MyButton
            {
                Text = word,
                MinWidth = 64,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 3, 10, 3)
            };
            var captured = word;
            button.Click += (_, _) =>
            {
                TextSearch.Text = captured;
                _ = RunSearchAsync();
            };
            PanSuggest.Children.Add(button);
        }
    }

    private void TextSearch_KeyDown(object sender, KeyEventArgs e)
    {
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
            ShowStatus(Lang.Text("Tools.Wiki.Empty.Title"), Lang.Text("Tools.Wiki.EmptyKeyword"));
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        PanItems.Children.Clear();
        LabCount.Text = "";
        ShowStatus(Lang.Text("Tools.Wiki.Empty.Title"), Lang.Text("Tools.Wiki.SearchingShort"), loading: true);

        try
        {
            var hits = await ModWikiSearch.SearchAsync(keyword, 12, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;

            if (hits.Count == 0)
            {
                ShowStatus(Lang.Text("Tools.Wiki.Empty.Title"), Lang.Text("Tools.Wiki.NoResult", keyword));
                return;
            }

            foreach (var hit in hits) PanItems.Children.Add(BuildResultItem(hit));
            HideStatus();
            LabCount.Text = Lang.Text("Tools.Wiki.ResultCountShort", hits.Count);
        }
        catch (OperationCanceledException)
        {
            // 被新的搜索覆盖
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Wiki] 搜索失败", ModBase.LogLevel.Developer);
            ShowStatus(Lang.Text("Tools.Wiki.Empty.Title"), Lang.Text("Tools.Wiki.Failed", ex.Message));
        }
        finally
        {
            if (_cts is not null && _cts.Token == token) BtnSearch.IsEnabled = true;
        }
    }

    private void ShowStatus(string title, string description, bool loading = false)
    {
        PanStatus.Visibility = Visibility.Visible;
        LabStatusTitle.Text = title;
        LabStatusDesc.Text = description;
        Load.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        PanResultScroll.Visibility = Visibility.Collapsed;
        BtnSearch.IsEnabled = !loading;
    }

    private void HideStatus()
    {
        PanStatus.Visibility = Visibility.Collapsed;
        Load.Visibility = Visibility.Collapsed;
        PanResultScroll.Visibility = Visibility.Visible;
        BtnSearch.IsEnabled = true;
    }

    private UIElement BuildResultItem(WikiSearchHit hit)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = hit.Title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("ColorBrush2")
        });

        if (!string.IsNullOrWhiteSpace(hit.Snippet))
            stack.Children.Add(new TextBlock
            {
                Text = hit.Snippet,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                Opacity = 0.6,
                MaxHeight = 36
            });

        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Child = stack,
            ToolTip = hit.Url
        };

        border.MouseLeftButtonUp += (_, _) => _ = OpenEntryAsync(hit);
        border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("ColorBrushGray7");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;

        return border;
    }

    #endregion

    #region 内置浏览器

    /// <summary>返回搜索结果列表。</summary>
    private void BtnReturn_Click(object sender, MouseButtonEventArgs e) => ShowListView();

    /// <summary>
    ///     切到结果列表视图。<br/>
    ///     分支说明：PCL 的页面切换都是「淡出 → 换内容 → 淡入」的两段动画，
    ///     直接改 Visibility 会显得突兀，因此这里套用同样的节奏。
    /// </summary>
    private void ShowListView() => SwitchView(CardResult, CardBrowser);

    /// <summary>切到条目内容视图。</summary>
    private void ShowBrowserView() => SwitchView(CardBrowser, CardResult);

    /// <summary>
    ///     把 <paramref name="target" /> 显示出来、<paramref name="source" /> 收起来，带淡入淡出。
    /// </summary>
    private void SwitchView(FrameworkElement target, FrameworkElement source)
    {
        if (ReferenceEquals(target, source)) return;
        if (target.Visibility == Visibility.Visible && source.Visibility != Visibility.Visible)
        {
            // 已经在目标视图上，不重复播动画
            return;
        }

        ModAnimation.AniStop("Wiki View Switch");

        // 第一阶段：旧视图淡出
        var fadeOut = ModAnimation.AaOpacity(source, -source.Opacity, 90);

        // 第二阶段：换内容 + 新视图从下方轻微上移并淡入
        var swap = ModAnimation.AaCode(() =>
        {
            source.Visibility = Visibility.Collapsed;
            source.Opacity = 1d;

            target.Visibility = Visibility.Visible;
            target.Opacity = 0d;

            var transform = target.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                target.RenderTransform = transform;
            }

            transform.Y = 12d;
        }, 10);

        var fadeIn = ModAnimation.AaOpacity(target, 1d, 150, 0,
            new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak));
        var slideUp = ModAnimation.AaDouble(i =>
        {
            if (target.RenderTransform is TranslateTransform t) t.Y += (double)i;
        }, -12d, 170, 0,
            new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak));

        ModAnimation.AniStart(new[] { fadeOut, swap, fadeIn, slideUp }, "Wiki View Switch");
    }
    

    private void BtnBack_Click(object sender, EventArgs e)
    {
        if (_webReady && _web?.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack();
    }

    private void BtnForward_Click(object sender, EventArgs e)
    {
        if (_webReady && _web?.CoreWebView2?.CanGoForward == true) _web.CoreWebView2.GoForward();
    }

    private void BtnReload_Click(object sender, EventArgs e)
    {
        if (_webReady) _web?.CoreWebView2?.Reload();
    }

    private void BtnCompact_Click(object sender, EventArgs e)
    {
        _compact = !_compact;
        BtnCompact.ToolTip = Lang.Text(_compact
            ? "Tools.Wiki.Compact.On"
            : "Tools.Wiki.Compact.Off");
        _ = ApplyStylesAsync();
    }

    private void BtnExternal_Click(object sender, MouseButtonEventArgs e)
    {
        var url = _webReady ? _web?.CoreWebView2?.Source : null;
        ModBase.OpenWebsite(string.IsNullOrWhiteSpace(url) ? ModWikiSearch.BaseUrl : url);
    }

    /// <summary>
    ///     应用（或移除）注入样式。每次导航完成都要重新调用 ——
    ///     页面内跳转会创建新文档，旧的 style 标签会随之丢失。
    /// </summary>
    private async Task<bool> ApplyStylesAsync()
    {
        if (!_webReady || _web?.CoreWebView2 is null) return false;
        try
        {
            if (!_compact)
            {
                await _web.CoreWebView2.ExecuteScriptAsync(
                    "(() => { const el = document.getElementById('pcl-style'); if (el) el.remove(); return 1; })();")
                    .ConfigureAwait(true);
                return true;
            }

            // 深浅色主题下的文字/链接/线条颜色跟着切换，避免白底浅字
            var isDark = ThemeManager.IsDarkMode;
            var fg = isDark ? "#dcdcdc" : "#202020";
            var link = isDark ? "#6cb6ff" : "#0b5bcb";
            var surface = isDark ? "rgba(255,255,255,0.06)" : "rgba(0,0,0,0.04)";
            var line = isDark ? "rgba(255,255,255,0.15)" : "rgba(0,0,0,0.12)";

            var css = InjectedCss.Replace("__PCL_BG__", _bgColor)
                .Replace("__PCL_FG__", fg)
                .Replace("__PCL_LINK__", link)
                .Replace("__PCL_SURFACE__", surface)
                .Replace("__PCL_LINE__", line)
                .Replace("\\", "\\\\").Replace("`", "\\`")
                .Replace("\r", "").Replace("\n", " ");

            var js = $$"""
                       (() => {
                           let el = document.getElementById('pcl-style');
                           if (!el) {
                               el = document.createElement('style');
                               el.id = 'pcl-style';
                               document.head.appendChild(el);
                           }
                           el.textContent = `{{css}}`;
                           return 'pcl-style applied';
                       })();
                       """;

            var result = await _web.CoreWebView2.ExecuteScriptAsync(js).ConfigureAwait(true);

            // 回读实际计算样式，确认 CSS 真的压过了 wiki 自己的皮肤
            var probe = await _web.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                    const cs = getComputedStyle(document.body);
                    const p = document.querySelector("#mw-content-text p");
                    return JSON.stringify({
                        bodyBg: cs.backgroundColor,
                        bodyColor: cs.color,
                        pColor: p ? getComputedStyle(p).color : "n/a",
                        styleTag: !!document.getElementById("pcl-style")
                    });
                })();
                """).ConfigureAwait(true);

            ModBase.Log($"[Wiki] 注入结果 {_bgColor}：实际样式 {probe}");

            // ExecuteScriptAsync 返回 JSON 字符串，读到 styleTag 即为生效
            return probe.Contains("pcl-style") || probe.Contains("styleTag");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Wiki] 注入样式失败", ModBase.LogLevel.Developer);
            return false;
        }
    }

    private async Task OpenEntryAsync(WikiSearchHit hit)
    {
        ShowBrowserView();
        LabUrl.Text = hit.Url;
        ModBase.Log($"[Wiki] 打开条目：{hit.Title} -> {hit.Url}");

        if (_webFailed)
        {
            ModBase.OpenWebsite(hit.Url);
            return;
        }

        // 分支说明：整段过程都盖着不透明加载遮罩，直到样式注入并确认生效才揭开。
        // 否则用户会先看到 wiki 的原始白底页面，再「跳」一下变成主题色，观感很差。
        BeginLoading(hideWeb: true);

        if (!_webReady)
        {
            var ok = await EnsureWebViewAsync().ConfigureAwait(true);
            if (!ok)
            {
                EndLoadingNow();
                LabWebHint.Text = Lang.Text("Tools.Wiki.WebViewUnavailable");
                LabWebHint.Visibility = Visibility.Visible;
                ModBase.OpenWebsite(hit.Url);
                return;
            }
        }

        Navigate(hit.Url);   // 后续由 NavigationCompleted 负责收尾
    }

    /// <summary>
    ///     开始加载：盖上不透明遮罩。
    /// </summary>
    /// <param name="hideWeb">
    ///     是否同时隐藏 WebView。首次打开条目时传 true（避免看到未处理的白底），
    ///     站内跳转时传 false（保留当前画面，只盖遮罩，否则会闪一下）。
    /// </param>
    private void BeginLoading(bool hideWeb = false)
    {
        _navigating = true;
        _loadingStarted = DateTime.Now;
        PanWebEmpty.Visibility = Visibility.Collapsed;
        if (hideWeb) PanWebHost.Visibility = Visibility.Collapsed;

        LabLoadTip.Text = Lang.Text("Tools.Wiki.LoadingPage");
        LoadPageCover.Visibility = Visibility.Visible;
    }

    /// <summary>遮罩的最小显示时长（毫秒）。</summary>
    private const int MinLoadingMs = 1000;

    /// <summary>
    ///     加载完成：显示 WebView、收起遮罩。<br/>
    ///     分支说明：本地缓存命中时页面几十毫秒就好了，遮罩一闪而过反而像闪烁。
    ///     这里保证遮罩至少显示 <see cref="MinLoadingMs" /> 毫秒，观感更稳。
    /// </summary>
    private async Task EndLoadingAsync()
    {
        var elapsed = (DateTime.Now - _loadingStarted).TotalMilliseconds;
        var remain = MinLoadingMs - elapsed;
        if (remain > 0)
            await Task.Delay((int)remain).ConfigureAwait(true);

        _navigating = false;
        PanWebHost.Visibility = Visibility.Visible;
        LoadPageCover.Visibility = Visibility.Collapsed;
    }

    /// <summary>立即收起遮罩，不等最小时长（用于失败 / 降级路径，避免用户干等）。</summary>
    private void EndLoadingNow()
    {
        _navigating = false;
        PanWebHost.Visibility = Visibility.Visible;
        LoadPageCover.Visibility = Visibility.Collapsed;
    }


    private async Task<bool> EnsureWebViewAsync()
    {
        if (_webReady) return true;

        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                ModBase.Log("[Wiki] 未检测到 WebView2 Runtime，降级为系统浏览器", ModBase.LogLevel.Hint);
                _webFailed = true;
                return false;
            }

            ModBase.Log($"[Wiki] WebView2 Runtime {version}");

            var dataDir = Path.Combine(ModBase.exePath, "PCL", "WebView2");
            Directory.CreateDirectory(dataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir).ConfigureAwait(true);

            // 分支说明：DefaultBackgroundColor 决定网页透明处的底色。
            // 把网页背景设为透明、由它来提供底色，是最可靠的「跟随主题」做法 ——
            // 深色主题下 PanForm 背景是 LCH 动态生成的渐变，没有固定值可取。
            var bg = ParseColor(_bgColor);
            _web = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(bg.A, bg.R, bg.G, bg.B)
            };
            PanWebHost.Children.Clear();
            PanWebHost.Children.Add(_web);

            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // 分支说明：站内点击链接时，从开始加载到 NavigationCompleted 之间会白屏一段时间。
            // 这里在导航一开始就盖上遮罩，让用户看到「正在加载」，而不是一片空白。
            core.NavigationStarting += (_, _) =>
            {
                // 只在页面已显示的情况下才盖遮罩；首次打开时 OpenEntryAsync 已经盖过了
                if (!_navigating) BeginLoading();
            };

            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    ModBase.Log($"[Wiki] 页面加载失败：{args.WebErrorStatus}", ModBase.LogLevel.Developer);
                    // 加载失败也要收起遮罩，否则会一直转圈
                    EndLoadingNow();
                    return;
                }

                // 分支说明：样式注入完成、并回读确认生效之后才揭开遮罩。
                // 这样用户看到的第一帧就是已适配主题的页面，不会先闪一下白底。
                if (_compact)
                {
                    var applied = await ApplyStylesAsync().ConfigureAwait(true);
                    if (!applied)
                        ModBase.Log("[Wiki] 样式未能确认生效，仍显示页面", ModBase.LogLevel.Hint);
                }

                // 再等一帧，让浏览器完成一次重绘，避免揭开瞬间仍是旧画面
                await Task.Delay(30).ConfigureAwait(true);
                await EndLoadingAsync().ConfigureAwait(true);
            };

            core.SourceChanged += (_, _) => ModBase.RunInUi(() =>
            {
                if (_web?.CoreWebView2 is not null) LabUrl.Text = _web.CoreWebView2.Source;
            });

            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrWhiteSpace(args.Uri)) Navigate(args.Uri);
            };

            _webReady = true;
            ModBase.Log("[Wiki] WebView2 初始化完成");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Wiki] WebView2 初始化失败", ModBase.LogLevel.Hint);
            _webFailed = true;
            return false;
        }
    }

    private void Navigate(string url)
    {
        if (!_webReady || _web?.CoreWebView2 is null) return;
        try
        {
            LabUrl.Text = url;
            _web.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Wiki] 导航失败", ModBase.LogLevel.Developer);
        }
    }

    #endregion
}