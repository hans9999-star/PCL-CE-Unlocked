using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.UI.Theme;

namespace PCL;

public static class ThemeManager
{
    private static bool _contextMenuHandlerRegistered;

    public static bool IsDarkMode => ThemeService.IsDarkMode;

    public static ResourceDictionary AppResources => System.Windows.Application.Current.Resources;

    public static ModBase.MyColor colorGray1 = new(AppResources["ColorObjectGray1"]);
    public static ModBase.MyColor colorGray4 = new(AppResources["ColorObjectGray4"]);
    public static ModBase.MyColor colorGray5 = new(AppResources["ColorObjectGray5"]);
    public static ModBase.MyColor colorSemiTransparent = new(AppResources["ColorBrushSemiTransparent"]);

    public static void ThemeRefresh(int newTheme = -1)
    {
        colorGray1 = new ModBase.MyColor(AppResources["ColorObjectGray1"]);
        colorGray4 = new ModBase.MyColor(AppResources["ColorObjectGray4"]);
        colorGray5 = new ModBase.MyColor(AppResources["ColorObjectGray5"]);
        colorSemiTransparent = new ModBase.MyColor(AppResources["ColorBrushSemiTransparent"]);
        ThemeRefreshMain();
    }

    public static void ThemeRefreshMain()
    {
        ModBase.RunInUi(() =>
        {
            if (!ModMain.frmMain.IsLoaded) return;
            RefreshTitleImage();
            RefreshBackground();
            RefreshAllContextMenuThemes();
        });
    }

    // 顶部栏主题背景图
    /// <summary>
    ///     把主题背景图叠加到顶部栏。<br/>
    ///     分支说明：与原版 PCL 一致——编号 5 及之后、且不是「自定义(14)」的主题，
    ///     会以 50% 透明度（13 号「欧皇彩」为 25%）叠加 Images/Themes/{编号}.png，
    ///     使跳票红带 Mojang 图标等主题特征得以还原。其余主题不叠加图片。
    /// </summary>
    private static void RefreshTitleImage()
    {
        var main = ModMain.frmMain;
        if (main?.ImgTitle is null) return;

        var theme = ThemeService.CurrentTheme;
        var id = (int)theme;
        if (id >= 5 && theme != ColorTheme.Custom)
        {
            try
            {
                // MyImage.Source 接受字符串形式的 pack URI
                main.ImgTitle.Source =
                    $"pack://application:,,,/Plain Craft Launcher 2;component/Images/Themes/{id}.png";
                main.ImgTitle.Opacity = theme == ColorTheme.LuckyColor ? 0.25d : 0.5d;
                main.ImgTitle.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                // 缺少图片资源时安静降级为纯色顶部栏，不影响使用
                ModBase.Log(ex, $"加载主题背景图失败（{id}.png），已降级为纯色顶部栏");
                main.ImgTitle.Source = null;
                main.ImgTitle.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            main.ImgTitle.Source = null;
            main.ImgTitle.Visibility = Visibility.Collapsed;
        }
    }
    
    // 主页面背景
    private static void RefreshBackground()
    {
        if (Config.Preference.Background.BackgroundColorful)
        {
            var brush = new LinearGradientBrush
            {
                EndPoint = new Point(0.1, 1),
                StartPoint = new Point(0.9, 0)
            };

            var hue = ThemeService.GetCurrentThemeArgs().Hue;
            var hue1 = hue - 15;
            var hue2 = hue + 15;
            var tone = ThemeService.CurrentTone;
            var darkLight = IsDarkMode ? 0.2d : 1d;
            brush.GradientStops.Add(new GradientStop
                { Offset = -0.1d, Color = LabColor.FromLch(0.84d * darkLight, tone.C5, hue1) });
            brush.GradientStops.Add(new GradientStop
                { Offset = 0.4d, Color = LabColor.FromLch(0.96d * darkLight, tone.C7, hue) });
            brush.GradientStops.Add(new GradientStop
                { Offset = 1.1d, Color = LabColor.FromLch(0.84d * darkLight, tone.C5, hue2) });
            ModMain.frmMain.PanForm.Background = brush;
        }
        else
        {
            ModMain.frmMain.PanForm.Background = (Brush)System.Windows.Application.Current.Resources["ColorBrushBackground"];
        }

        ModMain.frmMain.PanForm.Background.Freeze();
    }

    // 通用ContextMenu主题刷新
    private static void RefreshAllContextMenuThemes()
    {
        try
        {
            if (!_contextMenuHandlerRegistered)
            {
                EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                    new RoutedEventHandler(OnContextMenuOpened));
                _contextMenuHandlerRegistered = true;
            }

            foreach (Window window in System.Windows.Application.Current.Windows)
                RefreshContextMenusInElement(window);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "刷新ContextMenu主题时出错");
        }
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is ContextMenu contextMenu)
            {
                contextMenu.ClearValue(FrameworkElement.StyleProperty);
                contextMenu.UpdateDefaultStyle();
            }
        }
        catch
        {
            // 忽略个别错误
        }
    }

    private static void RefreshContextMenusInElement(DependencyObject element)
    {
        if (element is null)
            return;

        try
        {
            if (element is FrameworkElement { ContextMenu: not null } fe)
            {
                fe.ContextMenu.ClearValue(FrameworkElement.StyleProperty);
                fe.ContextMenu.UpdateDefaultStyle();
            }

            var childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
                RefreshContextMenusInElement(VisualTreeHelper.GetChild(element, i));
        }
        catch
        {
            // 忽略个别元素的错误，继续处理其他元素
        }
    }
}