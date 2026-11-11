# Plain Craft Launcher Unbound Edition

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

**基于 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的改版启动器**

解除功能限制 · 恢复完整主题 · 找回彩蛋 · 恢复联机与便携开服

[![Build](https://github.com/hans9999-star/PCL-Unbound/actions/workflows/build.yml/badge.svg)](https://github.com/hans9999-star/PCL-Unbound/actions/workflows/build.yml)
![GitHub Release](https://img.shields.io/github/v/release/hans9999-star/PCL-Unbound?label=release&style=flat-square)

</div>

---

## 这是什么

这是一个基于 [PCL CE](https://github.com/PCL-Community/PCL-CE)（Minecraft 启动器 Plain Craft Launcher 的社区版）二次开发的改版。

上游 PCL CE 出于维护与合规考虑，移除了部分原版 PCL 的功能、并对某些功能做了区域限制。本项目把这些限制去掉，同时把原版的主题、彩蛋、联机找回来，并在此基础上补齐了上游没有的服务端相关能力。

**只做功能层面的还原与新增，不改变启动器原有的核心能力。**

## 改动内容

### 解除功能限制

| 项目 | 说明 |
|---|---|
| 地区限制 | 移除基于国家/地区的功能封锁，共 15 处调用点：Minecraft 下载、文件补全、加载器安装、整合包安装、修复、重置 |
| 正版劝导 | 移除启动前的正版购买弹窗与强制试玩模式，任何账户类型均可直接启动 |
| 离线档案 | 移除「必须先登录正版才能创建其他档案」的限制 |
| 单实例限制 | 允许多开启动器 |

### 移除赞助相关

- 按启动次数弹出的赞助劝导
- 设置页的「蓝色困境 / 获取官方快照版」面板
- 关于页的两个赞助链接按钮
- 更新后的社区版说明弹窗
- 主页的 CE 说明卡片

### 恢复完整主题

| 项目 | 说明 |
|---|---|
| 主题数量 | 4 个 → 15 个，编号与原版 `UiLauncherTheme` 一致（0–14） |
| 选择界面 | 还原为原版的 3×5 网格布局，所有主题默认可选，不再需要解锁 |
| 主题背景图 | 从官方 PCL 提取 10 张 `Themes/{编号}.png`，按原版逻辑叠加到标题栏（跳票红带 Mojang 图标） |
| 多彩主题 | 滑稽彩、欧皇彩会随时间循环变色 |
| 自定义配色 | 支持色相 / 饱和度 / 亮度三个滑块调节 |

主题列表：

| 常规 | 隐藏 | 其余 |
|---|---|---|
| 龙猫蓝 | 玄素黑 | 秋仪金 |
| 甜柠青 | 滑稽彩 | 活跃橙 |
| 小草绿 | 铁杆粉 | 跳票红 |
| 菠萝黄 | 神秘紫 | 极客蓝 |
| 橡木棕 | 欧皇彩 | 自定义 |

> 龙猫蓝的配色参数保持 CE 原值 `(色相 255, 明度 0, 色度 -0.2)` 不变。

### 个性化主题

在上游「一套色相/饱和度/亮度」的基础上做了扩展：

- **亮色与暗色各有一套自定义值**，切换深浅色模式时各自生效（上游只有一套，亮暗混搭时语义是坏的）
- **自定义标题图**：用自己的图片替换主界面顶部标题图，优先级高于内置主题背景图
- **自定义背景图**：用自己的图片作为主界面背景，失败时自动回落到渐变或纯色
- **主题文件**：把「亮暗配色 + 标题图 + 背景图」保存成一个主题，支持套用、删除、导出 zip、导入 zip

主题文件存放在 `PCL/Theme/{主题名}/`，内含 `theme.json` 与可选的图片。图片会被复制进主题目录，因此导出的主题在别人电脑上也能直接用。

### 恢复「千万别点」

百宝箱中的经典彩蛋，按官方 PCL 的反编译源码 1:1 复刻：

- 从 8 种效果中随机抽取，并有概率**继续叠加**（`while` 循环，非单次判断）
- 效果包括：眼瞎白、真·滑稽彩、界面镜像翻转、放大全屏、缩小、无限旋转、无限平移绕屏、打开视频
- 抽到眼瞎白时先弹癫痫警告，取消则隐藏按钮
- 无论抽到什么都会给出「PCL 作者不会受理……」的最后警告
- 结束后隐藏按钮（与原版一致）

### 恢复联机

上游移除了联机功能，本项目按 PCL2 2.11.2 的界面还原：

- 顶层「联机」标签，包含「创建房间 / 加入房间 / 本地开服」三张卡片
- `LobbyService` 恢复自动启动，EasyTier 以 `no-tun` 模式运行（无需管理员权限）
- 还原 2.11.2 的完成页样式：邀请码、IP、端口分列显示，一键复制

### 便携开服

上游完全没有服务端能力，本项目补了一套完整的本地开服流程：

| 能力 | 说明 |
|---|---|
| 服务端类型 | 原版 / Paper（插件端）/ Fabric（模组端）/ Forge / NeoForge |
| 版本选择 | 独立于游戏实例，可自由选择任意版本 |
| 一键开服 | 缺服务端时自动下载，写好 `eula.txt` 与 `server.properties`，用启动器已检测到的 Java 启动 |
| 内置控制台 | 实时输出 + 命令输入（`stop` / `list` / `op` 等），滚轮可滚动 |
| 分享到大厅 | 服务端跑起来后一键发布到联机大厅，朋友输邀请码即可进服 |

Forge 与 NeoForge 走官方安装器流程：下载 installer → 以服务器目录为工作目录执行 `--installServer` → 从生成的 `win_args.txt` 取启动参数。NeoForge 的官方 maven 在国内不可达，改用 bmclapi 镜像。

### mod / 插件管理

开服页的「内容管理」直接复用 PCL 原有的资源管理页与下载页，没有另做一套简陋列表：

- **本地管理**：搜索、筛选、排序、多选批量启用停用删除、打开文件夹
- **在线下载**：按当前服务端的加载器与游戏版本过滤，从 Modrinth 取资源
- **插件独立分类**：插件与模组并列，是独立入口，只在服务端插件管理时出现
- **服务端过滤**：服务端 mod 管理只列 `server_side` 为 required/optional 的资源，避免纯客户端 mod 混入

> CurseForge 没有 `server_side` 字段，无法过滤客户端 mod，因此服务端场景只走 Modrinth。

### AI 崩溃日志分析

内置崩溃分析是查表式的，遇到规则库没收录过的崩溃就只剩「未知错误」。这种情况下崩溃窗口会多一个「让 AI 分析」按钮：

- 把日志交给 OpenAI 兼容接口再分析一次，日志过长时只取尾部
- 端点在**设置 → 启动器 → 杂项 → AI 崩溃日志分析**里填写，默认留空（即不启用），需自行指向任意 OpenAI 兼容服务
- 端点返回 401 时就地弹框让用户填 Key 并自动重试；模型名格式不对（400）则让用户填模型名
- 分析期间显示进度窗（PCL 对话框外观 + 镭子加载动画 + 已等待秒数 + 取消）

## 与上游的差异对照

| 功能 | 上游 PCL CE | 本项目 |
|---|---|---|
| 地区限制 | 有 | 无 |
| 正版购买劝导 | 有 | 无 |
| 主题数量 | 3–4 个 | 15 个 |
| 主题解锁 | 需赞助/条件 | 全部开放 |
| 主题背景图 | 无 | 有（10 张） |
| 自定义主题 | 单套配色 | 亮暗各一套 + 自定义标题图/背景图 + 主题文件 |
| 千万别点 | 无 | 有 |
| 联机功能 | 无 | 有（并新增本地开服） |
| 服务端管理 | 无 | 有（mod / 插件） |
| AI 崩溃分析 | 无 | 有 |
| 多开 | 不允许 | 允许 |

## 构建

### 环境要求

- **.NET 10 SDK**（[下载](https://dotnet.microsoft.com/download/dotnet/10.0)）
- Windows 10 1809 (17763) 或更高版本

> 注意：需要 .NET 10 SDK **10.0.400 或更高版本**。项目引用的 `Microsoft.CodeAnalysis.CSharp 5.6.0` 需要 Roslyn 5.6+，而 SDK 10.0.100 自带的是 Roslyn 5.0，会导致源生成器无法加载、出现大量 `CS9248` 错误。

### 命令行构建

```powershell
# 还原依赖
dotnet restore "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -p:Platform=x64

# 编译
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64

# 发布为单文件自包含 exe
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" `
  -c Release -p:Platform=x64 -o ./publish --self-contained
```

产物位于 `./publish/Plain Craft Launcher 2.exe`（约 187 MB，内含 .NET 10 运行时，无需另行安装）。

> 发布产物的文件名由 `AssemblyName` 决定，保持为 `Plain Craft Launcher 2.exe` 是刻意的：全项目的 pack URI（`pack://application:,,,/Plain Craft Launcher 2;component/Images/...`）都写死了程序集名，改 `AssemblyName` 会让所有图片与 XAML 资源加载失败。对外发布的 exe 是在发布之后重命名的。

### 直接下载

前往 [Releases](https://github.com/hans9999-star/PCL-Unbound/releases) 下载最新构建。

## 关于启动器更新功能

本项目的更新功能指向本仓库的 GitHub Releases。启动器会读取：

```
https://api.github.com/repos/hans9999-star/PCL-Unbound/releases/latest
```

因此需要在本仓库发布 Release 才能触发更新提示。CI 工作流会自动完成构建与发布。

## 项目结构

```
PCL.Core/                      核心库（配置、网络、Minecraft、UI 主题等）
PCL.Core.SourceGenerators/     源生成器（配置属性、生命周期）
PCL.Core.Test/                 单元测试
Plain Craft Launcher 2/        主程序（WPF）
├── Images/Themes/             主题背景图（从官方 PCL 提取）
├── Modules/                   业务模块
│   ├── Minecraft/ModServerHost.cs     服务端下载与启动
│   ├── Minecraft/ModServerContent.cs  服务端内容管理目标
│   └── UI/Theme/ModThemeFile.cs       主题文件读写
├── Pages/                     页面
└── Resources/hints/           启动提示文案
```

运行期数据（不在仓库内）：

```
PCL/Theme/{主题名}/            用户保存的主题
PCL/Server/{类型}-{版本}/       服务端文件（mods/ 或 plugins/、world、配置）
```

## 许可证

本项目的许可证继承自上游：

- `Plain Craft Launcher 2/` 目录使用 [PCL 分发有限许可](Plain Craft Launcher 2/LICENCE)
- 其余所有目录使用 [Apache License 2.0](LICENSE)

**重要**：PCL 分发有限许可要求第三方改版：

1. 明确标注这是基于 PCL 的第三方二次创作
2. 名称以 `Plain Craft Launcher (PCL)` 开头并附加足以表明第三方修改的后缀
3. 在关于页首位给出龙腾猫跃的署名及赞助链接
4. 保留 Minecraft 正版购买劝导（本项目为满足「破除限制」的目标已移除，使用者需自行承担相应合规责任）
5. 不得有与 PCL 赞助解锁类似的功能（本项目已解除所有主题限制）

本项目已尽力遵守上述条款（第 4 条因目标冲突而例外）。**请在遵守许可的前提下使用与分发。**

## 致谢

- [龙腾猫跃 (LTCat)](https://github.com/Meloong-Git/PCL) — PCL 原作者
- [PCL Community](https://github.com/PCL-Community/PCL-CE) — PCL CE 社区版
- 以及所有为 PCL 与 PCL CE 做出贡献的开发者

本项目与龙腾猫跃、PCL-Community 均无从属关系，出现任何问题请勿向上游仓库反馈。

## 免责声明

本项目仅供学习交流使用。使用本项目造成的任何后果由使用者自行承担。请支持正版 Minecraft。
