# PCL Linux CLI

PCL₃ 的 Linux CLI 与 Windows WPF 前端引用同一个 `PCL.Core.Shared` 项目。Linux 入口独立运行，不加载 WPF；Windows 页面和现有功能路径保持原样。

## 使用

发布包包含 self-contained 的 Linux x64 `pcl` 可执行文件，不需要单独安装 .NET Runtime。开发构建需要 .NET 10 SDK。首次登录还需要仓库维护者配置 `PCL_MS_CLIENT_ID`。

```sh
pcl login
pcl java scan
pcl install latest
pcl instance list
pcl launch <version>
```

例如指定独立游戏目录：

```sh
pcl install 1.20.1 --game-dir "$HOME/Games/Minecraft"
pcl launch 1.20.1 --game-dir "$HOME/Games/Minecraft"
```

`launch` 会根据版本 JSON 中的 `javaVersion.majorVersion` 选择匹配的系统 Java；找不到时，从 Mojang 官方 runtime 清单下载对应 Linux runtime。首次下载客户端、libraries、assets 和 natives；重复安装会校验 SHA-1 并跳过有效文件。下载最多 8 路并发，失败会有限次重试。

`pcl login` 使用 Microsoft device-code flow，终端显示验证网址和代码。刷新令牌保存在 `${XDG_CONFIG_HOME:-$HOME/.config}/pcl-linux/microsoft-account.json`，文件权限为仅当前用户可读写；短期 access token 不落盘。`pcl logout` 删除本地账户令牌。

## 当前范围

已实现官方原版 Minecraft 版本元数据获取、文件下载/校验、assets/native 安装、Java runtime 自动安装、Microsoft/Xbox/Minecraft 认证、账号刷新及原版启动。

暂不支持 Forge/Fabric/NeoForge 等加载器、整合包导入、自定义 Yggdrasil 认证、TUI、自动更新，以及 Windows 桌面功能的 Linux 等价实现。Linux 首期是 CLI，不会显示 PCL 桌面 UI；Minecraft 游戏窗口由游戏进程直接创建。

Microsoft 登录依赖已授权的 client ID。没有 client ID 时，`pcl login` 会明确报错，不会退回伪造的离线玩家身份。当前启动参数和文件规则已用 Minecraft 1.20.1 验证到 Java 初始化与 GLFW 窗口创建阶段；实际可玩性仍需在具备可用图形会话、显卡驱动和已授权账号的 Linux 桌面环境验收。

## 本地开发

```sh
dotnet build PCL.Linux.Cli/PCL.Linux.Cli.csproj
dotnet test PCL.Core.Shared.Tests/PCL.Core.Shared.Tests.csproj
```
