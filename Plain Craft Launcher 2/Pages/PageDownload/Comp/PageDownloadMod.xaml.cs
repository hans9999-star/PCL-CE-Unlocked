using PCL.Core.App.Localization;

namespace PCL;

public partial class PageDownloadMod
{
    public PageDownloadMod()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     分支新增：服务端插件下载复用本页骨架，只把内容类型换成 Plugin。<br/>
    ///     PageComp 的 Modrinth facet 由枚举名小写生成，因此 Plugin 会得到
    ///     project_type:plugin，加载器 Paper 会得到 categories:'paper'。
    /// </summary>
    public PageDownloadMod(ModComp.CompType compType) : this()
    {
        Content.PageType = compType;
        if (compType != ModComp.CompType.Plugin) return;

        // Mod 的分类标签对插件没有意义，只保留「全部」
        Content.SearchTags.Clear();
        Content.SearchTags.Add(new MyComboBoxItem
        {
            Content = Lang.Text("Common.Option.All"),
            Tag = "",
            IsSelected = true
        });
    }
}