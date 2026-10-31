<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.png" alt="PCl₃" width="80" height="80">

# PCl₃ (三氯化磷启动器)

**PCl₃**，即三氯化磷（Phosphorus Trichloride），无色透明液体，用于制造有机磷化合物。

基于 [PCL CE](https://github.com/PCL-Community/PCL-CE)（Plain Craft Launcher Community Edition）二次改编的 Minecraft Java 版启动器。

[![GitHub Release](https://img.shields.io/github/v/release/dyyxxhh/pcl3?label=release&logo=github&style=for-the-badge)](https://github.com/dyyxxhh/pcl3/releases/latest)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/dyyxxhh/pcl3/build-test.yml?style=for-the-badge)](https://github.com/dyyxxhh/pcl3/actions)

[下载最新版](https://github.com/dyyxxhh/pcl3/releases/latest) | [提交问题](https://github.com/dyyxxhh/pcl3/issues/new/choose)

</div>

## 🧪 这是什么

PCl₃ 是 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的个人改编版。上游 PCL CE 自 2.12.x 起对中国大陆区域做了功能限制（地区检测、账号限制），本仓库将其解除，并重新品牌化为 PCl₃。

**请勿向 PCL CE 官方仓库反馈本仓库的问题。**

## ✨ 相比上游添加 / 修改了什么

| 改动 | 说明 |
|---|---|
| 解除区域限制 | `RegionPolicy` / `RestrictedFeaturePolicy` 判定改为恒不受限，中国大陆可直接下载 Minecraft 本体、整合包与加载器 |
| 解除账号限制 | 无需正版账号：离线登录、第三方登录随时可创建（`ProfileUi.CanCreateOtherProfile` 恒为 `true`） |
| 移除正版购买提示 | 启动游戏前不再弹出「正版购买 / 账号验证」弹窗，受限地区也不再跳过客户端文件下载 |
| 品牌重制 | 启动器名称、标题栏、AUMID、程序集信息、全部 7 种语言文件统一更名为 PCl₃；更换启动器图标 |
| 版本号独立 | 自 3.0.0 起独立编号（`metadata.json` 中 `upstream` 记录所基于的上游版本） |

## 📦 上游（PCL CE）都有什么

以下功能全部继承自上游，本仓库未做删减：

- **启动器核心**：Minecraft Java 版本体、整合包（CurseForge / Modrinth / MC 百科等）、加载器（Forge / NeoForge / Fabric / Quilt / OptiFine）下载安装
- **账号系统**：微软正版、离线、第三方认证服务器（authlib-injector）、统一通行证登录
- **实例管理**：多实例（多版本隔离）、Mod 管理（Jar-in-Jar 依赖分析）、存档 / 截图 / 服务器管理
- **下载源**：官方源、BMCLAPI、官方注入源，支持搜索 Mod、整合包、资源包、光影
- **其他**：Java 管理、内存优化、崩溃分析、自定义主题 / 背景音乐 / 特效、联机（EasyTier）、Java 版启动优化等

详见 [PCL CE 仓库](https://github.com/PCL-Community/PCL-CE)。

## 💻 支持平台

| 操作系统 | 支持情况 | 环境要求 |
|---|---|---|
| Windows 10 1809 (17763) 或更高 | ✅ 完整支持 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 8 - Windows 10 1809 (17763) | ⚠️ 理论能跑 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 7 或更低版本 | ❌ 不支持 | / |
| macOS / Linux | 仅交叉编译开发支持 | [.NET 10 SDK](https://get.dot.net/10) |

Release 提供 x64 与 ARM64 两个架构的依赖框架版本，需系统已安装 .NET 10 Desktop Runtime。

## 🔄 与上游同步

本仓库基于上游 `dev` 分支（构建版本见 [Releases](https://github.com/dyyxxhh/pcl3/releases) 页所注明的上游 commit）。上游更新后，将 PCl₃ 的改动（见上方「相比上游添加 / 修改了什么」）重新应用即可。

## 🔒 许可证

- `Plain Craft Launcher 2/` 使用 [自定义许可证](Plain%20Craft%20Launcher%202/LICENCE)（继承自 PCL CE，禁止商用等）
- `其余所有目录` 使用 [Apache License 2.0](LICENSE)

上游 PCL CE 由 [PCL Community](https://github.com/PCL-Community) 维护，原始 PCL 由 [龙腾猫跃](https://github.com/Meloong-Git/PCL) 开发，感谢他们的工作。
