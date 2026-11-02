# AGENTS.md

## 项目概况

PCL₃ 是基于 PCL Community Edition 的 Minecraft Java 版启动器 fork。Windows 主程序为 .NET 10 WPF；Linux 有独立的、仍在开发中的命令行客户端。保持 Windows UI 和现有行为不变，除非任务明确要求修改。不要将 Linux 客户端描述为已具备 Windows 全部功能。

## 项目结构

- `Plain Craft Launcher 2/`：Windows WPF 入口、页面、控件、Minecraft 下载/启动模块及资源；项目文件为 `Plain Craft Launcher 2.csproj`。
- `PCL.Core/`：现有 Windows 核心，目标 `net10.0-windows`，包含业务、WPF UI 和平台服务，不能直接作为跨平台库使用。
- `PCL.Core.Shared/`：`net10.0` 共享代码，包括 Linux 原版安装、Java 发现、启动和认证。Windows 当前仅部分使用该库（例如 Java 版本规则），不要误称所有启动逻辑已经共享。
- `PCL.Linux.Cli/`：Linux CLI 命令、终端交互；详细命令参见其 README。
- `PCL.Core.Shared.Tests/`：共享核心 MSTest 测试。
- `PCL.Core.Test/`：Windows 核心测试；`PCL.Core.SourceGenerators/`：源生成器；`tests/UpdateRegression/`：更新回归测试。
- `.github/workflows/`：CI、复用 Windows 构建和手动 Release workflow；发布资源统一使用 `PCL3_` 前缀。
- `docs/research/`：研究记录；注意保留现有未跟踪文件。

## 构建与验证

需要 .NET 10 SDK。在仓库根目录运行：

```sh
dotnet build PCL.Linux.Cli/PCL.Linux.Cli.csproj
dotnet test PCL.Core.Shared.Tests/PCL.Core.Shared.Tests.csproj
dotnet publish PCL.Linux.Cli/PCL.Linux.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./artifact-linux
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj"
python3 scripts/check_launcher_fixes.py
python3 scripts/check_update_isolation.py
python3 scripts/check_fork_privacy.py
```

Linux 可交叉编译 Windows 目标，但不能以此证明 WPF 在 Windows 的运行行为无回归。完整图形游戏启动、微软认证和第三方验证需对应的桌面环境/真实账户验证。已有 Windows 源码会产生大量编译警告，应区分历史警告与新问题。不要提交 `bin/`、`obj/`、SDK 或发布产物。

## 登录与安全

Linux CLI 支持离线账户和传统 Yggdrasil/authlib-injector 第三方验证，使用当前选中账户启动。第三方密码只在交互式终端输入，不应放入命令行参数、日志、测试记录或配置文件。账户令牌文件保存在用户配置目录，必须限制访问权限；它们是敏感数据。不要打印或提交任何凭据、令牌、客户端密钥或 GitHub Secrets。服务器测试使用独立 mock，避免访问真实账户。

## 架构和维护约定

- 优先把可跨平台复用的领域逻辑放在共享核心，前端保留平台/UI 适配；不能声称将 Windows 和 Linux 的所有功能都迁入同一内核。
- 任何 Windows UI 或业务改动须保留原有体验并运行相应回归；不要用删除原平台能力换取编译成功。
- 路径使用 `Path` API，Linux 文件名区分大小写；下载内容须验证哈希并避免目录穿越。
- 变更认证、启动参数、版本或 Release 文件名时添加回归测试，检查 Windows 更新器与 Actions 保持一致。
- 运行 Git 命令前先检查工作树；不要覆盖或删除非本任务的修改、文件或未跟踪内容。
