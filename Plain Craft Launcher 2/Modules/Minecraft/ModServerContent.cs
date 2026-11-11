using System.IO;

namespace PCL;

/// <summary>内容类型：mod（模组）或 plugin（插件）。</summary>
public enum ContentKind
{
    Mod,
    Plugin
}

/// <summary>
///     服务端内容管理目标（分支新增）。<br/>
///     让「实例资源页」<see cref="PageInstanceCompResource" /> 与「资源下载页」
///     在没有 <see cref="McInstance" /> 的情况下，也能针对某个服务端目录做 mod / 插件管理。<br/>
///     这些页面原本处处依赖当前实例，本对象提供路径、版本号、加载器与内容类型的覆盖值。
/// </summary>
public sealed class ServerContentTarget
{
    /// <summary>标题里显示的短名，如「Fabric · 1.21.1」。</summary>
    public required string Name { get; init; }

    /// <summary>内容根目录（等价于实例的 PathIndie），末尾带反斜杠。</summary>
    public required string RootPath { get; init; }

    /// <summary>Minecraft 版本号。</summary>
    public required string VanillaName { get; init; }

    /// <summary>该端使用的加载器列表。</summary>
    public required List<ModComp.CompLoaderType> Loaders { get; init; }

    /// <summary>内容类型（Mod / Plugin）。</summary>
    public required ModComp.CompType CompType { get; init; }

    /// <summary>内容目录：RootPath 加 mods\ 或 plugins\。</summary>
    public string ContentPath => RootPath + ModLocalComp.GetPathNameByCompType(CompType) + @"\";
}

/// <summary>
///     服务端内容管理（分支新增）。<br/>
///     原版开服页只负责把服务端跑起来，没有任何内容管理；本类负责把服务端描述成
///     一个可供 PCL 原有资源管理页 / 下载页使用的目标，从而完整复用那套 UI，
///     而不是自绘一套简陋列表。<br/>
///     分支说明：本地列表、启用停用、删除、在线搜索与下载全部由原有页面完成。
/// </summary>
public static class ModServerContent
{
    /// <summary>
    ///     资源下载页当前生效的服务端目标。<br/>
    ///     非空时，单个资源的默认下载目录会指向该服务端的内容目录。
    /// </summary>
    public static ServerContentTarget? ActiveTarget;

    /// <summary>
    ///     本机已安装的游戏实例（按名称排序），供开服页做实例选择用。
    /// </summary>
    public static List<McInstance> GetInstances()
    {
        var list = new List<McInstance>();
        try
        {
            foreach (var pair in ModInstanceList.mcInstanceList)
                if (pair.Value is not null)
                    list.AddRange(pair.Value.Where(i => i is not null));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerContent] 读取实例列表失败", ModBase.LogLevel.Developer);
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>该服务端类型下管理的是哪类内容。</summary>
    public static ContentKind KindOf(ServerSoftware software) =>
        software == ServerSoftware.Paper ? ContentKind.Plugin : ContentKind.Mod;

    /// <summary>
    ///     按当前选择的服务端构造内容管理目标。
    /// </summary>
    public static ServerContentTarget BuildTarget(
        ServerSoftware software, string version, string vanillaName, string displayName)
    {
        var kind = KindOf(software);
        var loaders = new List<ModComp.CompLoaderType>();
        if (kind == ContentKind.Plugin)
        {
            // 插件端的加载器标识，ModComp 会据此生成 Modrinth 的 categories:'paper'
            loaders.Add(ModComp.CompLoaderType.Paper);
        }
        else
        {
            switch (software)
            {
                case ServerSoftware.Fabric:
                    loaders.Add(ModComp.CompLoaderType.Fabric);
                    break;
                case ServerSoftware.Forge:
                    loaders.Add(ModComp.CompLoaderType.Forge);
                    break;
                case ServerSoftware.NeoForge:
                    loaders.Add(ModComp.CompLoaderType.NeoForge);
                    break;
                default:
                    loaders.Add(ModComp.CompLoaderType.Any);
                    break;
            }
        }

        return new ServerContentTarget
        {
            Name = displayName,
            RootPath = ModServerHost.GetServerDir(software, version) + @"\",
            VanillaName = vanillaName,
            Loaders = loaders,
            CompType = kind == ContentKind.Plugin ? ModComp.CompType.Plugin : ModComp.CompType.Mod
        };
    }

    /// <summary>本地内容目录：mod 端为 mods，插件端为 plugins。</summary>
    public static string GetContentDir(ServerSoftware software, string version, ContentKind kind) =>
        Path.Combine(ModServerHost.GetServerDir(software, version),
            kind == ContentKind.Plugin ? "plugins" : "mods");
}
