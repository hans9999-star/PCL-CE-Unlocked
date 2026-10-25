# PCL CE Unlocked

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

**基于 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的改版启动器**

解除功能限制 · 恢复完整主题 · 找回彩蛋

[![Build](https://github.com/hans9999-star/PCL-CE-Unlocked/actions/workflows/build.yml/badge.svg)](https://github.com/hans9999-star/PCL-CE-Unlocked/actions/workflows/build.yml)
![GitHub Release](https://img.shields.io/github/v/release/hans9999-star/PCL-CE-Unlocked?label=release&style=flat-square)

</div>

---

## 这是什么

这是一个基于 [PCL CE](https://github.com/PCL-Community/PCL-CE)（Minecraft 启动器 Plain Craft Launcher 的社区版）二次开发的改版。

上游 PCL CE 出于维护与合规考虑，移除了部分原版 PCL 的功能、并对某些功能做了区域限制。本项目把这些限制去掉，同时把原版的主题与彩蛋找回来。

**只做功能层面的还原与解限，不改变启动器原有的核心能力。**

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

### 恢复「千万别点」

百宝箱中的经典彩蛋，按官方 PCL 的反编译源码 1:1 复刻：

- 从 8 种效果中随机抽取，并有概率**继续叠加**（`while` 循环，非单次判断）
- 效果包括：眼瞎白、真·滑稽彩、界面镜像翻转、放大全屏、缩小、无限旋转、无限平移绕屏、打开视频
- 抽到眼瞎白时先弹癫痫警告，取消则隐藏按钮
- 无论抽到什么都会给出「PCL 作者不会受理……」的最后警告
- 结束后隐藏按钮（与原版一致）

### 移除联机

- 删除联机主页 `PageToolsGameLink`（约 1400 行）
- 删除联机设置页 `PageSetupGameLink`
- 删除 `ModLink` 模块
- 移除工具页与设置页的联机入口
- `LobbyService` 不再自动启动
- **保留** `PCL.Core.Link.McPing`（实例设置-服务器、瞅眼服务器仍需要它）

## 与上游的差异对照

| 功能 | 上游 PCL CE | 本项目 |
|---|---|---|
| 地区限制 | 有 | 无 |
| 正版购买劝导 | 有 | 无 |
| 主题数量 | 3–4 个 | 15 个 |
| 主题解锁 | 需赞助/条件 | 全部开放 |
| 主题背景图 | 无 | 有（10 张） |
| 千万别点 | 无 | 有 |
| 联机功能 | 有 | 无 |
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

### 直接下载

前往 [Releases](https://github.com/hans9999-star/PCL-CE-Unlocked/releases) 下载最新构建。

## 关于启动器更新功能

本项目的更新功能指向本仓库的 GitHub Releases。启动器会读取：

```
https://api.github.com/repos/hans9999-star/PCL-CE-Unlocked/releases/latest
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
├── Pages/                     页面
└── Resources/hints/           启动提示文案
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
