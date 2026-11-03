# PCL Linux CLI

PCL₃ 的 Linux CLI 与 Windows WPF 前端引用同一个 `PCL.Core.Shared` 项目。Linux 入口独立运行，不加载 WPF；Windows 页面和现有功能路径保持原样。

## 使用

发布包包含 self-contained 的 Linux x64 `pcl` 可执行文件，不需要单独安装 .NET Runtime。开发构建需要 .NET 10 SDK。首次登录还需要仓库维护者配置 `PCL_MS_CLIENT_ID`。

```sh
pcl login offline <玩家名>
pcl login authlib <验证服务器URL> <用户名>
pcl login microsoft
pcl java scan
pcl install latest
pcl instance list
pcl launch <version>
pcl update
```

第三方登录使用标准 Yggdrasil `/authserver`，在交互终端隐藏输入密码；不保存密码。选中角色和访问令牌保存到 `selected-profile.json`，仅限当前用户访问；启动时刷新令牌并下载、校验 authlib-injector。仅允许 HTTPS（本机测试可用 HTTP）。若服务器未指定角色而返回多个角色，当前选择第一个角色。

例如指定独立游戏目录：

```sh
pcl install 1.20.1 --game-dir "$HOME/Games/Minecraft"
pcl launch 1.20.1 --game-dir "$HOME/Games/Minecraft"
```

`launch` 会根据版本 JSON 中的 `javaVersion.majorVersion` 先检查当前游戏目录 `runtime` 下已安装且可运行的 Java，再检查系统 Java；都找不到时才从 Mojang 官方清单下载。完整安装后的本地 runtime 不需要联网即可复用。首次下载客户端、libraries、assets 和 natives；重复安装会校验 SHA-1 并跳过有效文件。下载最多 8 路并发，失败会有限次重试。离线启动仍需事先安装完整游戏并选择离线账户；Microsoft 和第三方账户的刷新需要联网。

`pcl update` 从本项目最新正式 Release 下载 Linux x64 归档并校验 SHA-256，仅替换当前单文件可执行程序，完成后再次运行即可。需要对可执行文件所在目录有写权限；开发构建和其他架构不支持自更新。校验文件与归档来自同一 Release，能检测下载损坏，但不是独立的签名验证。

`pcl login` 使用 Microsoft device-code flow，终端显示验证网址和代码。刷新令牌保存在 `${XDG_CONFIG_HOME:-$HOME/.config}/pcl-linux/microsoft-account.json`，文件权限为仅当前用户可读写；短期 access token 不落盘。`pcl logout` 删除本地账户令牌。

## 当前范围

已实现官方原版 Minecraft 版本元数据获取、文件下载/校验、assets/native 安装、Java runtime 自动安装、Microsoft/Xbox/Minecraft 认证、账号刷新及原版启动。

暂不支持 Forge/Fabric/NeoForge 等加载器、整合包导入、Yggdrasil Connect（统一通行证）、TUI，以及 Windows 桌面功能的 Linux 等价实现。Linux 首期是 CLI，不会显示 PCL 桌面 UI；Minecraft 游戏窗口由游戏进程直接创建。

Microsoft 登录依赖已授权的 client ID。没有 client ID 时，`pcl login` 会明确报错，不会退回伪造的离线玩家身份。当前启动参数和文件规则已用 Minecraft 1.20.1 验证到 Java 初始化与 GLFW 窗口创建阶段；实际可玩性仍需在具备可用图形会话、显卡驱动和已授权账号的 Linux 桌面环境验收。

## 本地开发

```sh
dotnet build PCL.Linux.Cli/PCL.Linux.Cli.csproj
dotnet test PCL.Core.Shared.Tests/PCL.Core.Shared.Tests.csproj
```
