# AGENTS.md

## 项目与边界
- PCL₃ 是 Minecraft Java 启动器 fork：Windows 为 .NET 10 WPF，Linux 为独立的 MCM CLI。
- `Plain Craft Launcher 2/` 是 Windows 前端；`PCL.Core/` 是 Windows 核心，不能直接当跨平台库。
- `PCL.Core.Shared/` 只承载明确可复用的领域逻辑；不得声称 Windows/Linux 已共享全部启动、认证或安装逻辑。
- `PCL.Linux.Cli/`、`PCL.Core.Shared.Tests/`、`PCL.Core.Test/`、`tests/UpdateRegression/` 分别是 Linux CLI、共享测试、Windows 测试和更新回归。
- `.github/workflows/` 管理 CI、Windows 构建和 Release；保留 `PCL3_`、`MCM_` 资源命名约定。
- 保留 `docs/research/` 中已有文件，包括未跟踪研究记录。

## 修改与安全
- 开始前检查工作树、分支和未跟踪文件；不得覆盖、删除或提交无关修改。
- 保持现有 Windows/Linux 行为；UI、启动、认证、更新和发布改动必须补相应回归。
- 路径使用 `Path` API；下载内容校验哈希、长度、架构并防止目录穿越和重解析点攻击。
- 不输出、读取、提交或传播密码、令牌、客户端密钥、Cookies 或 GitHub Secrets；测试使用独立 mock。
- 不提交 `bin/`、`obj/`、SDK、临时目录或本地发布产物。

## 验证
- 默认运行：`dotnet build PCL.Linux.Cli/PCL.Linux.Cli.csproj`、共享测试、Windows WPF 构建，以及三个 `scripts/check_*.py` 回归脚本。
- 发布前按改动范围增加测试；Linux 交叉编译不能证明 Windows WPF、PowerShell、CMD、图形启动或真实认证可用。
- 区分历史警告与新增错误；所有编译错误和回归失败必须修复后再继续。

## 完成、推送与正式 Release
- 完成实现后必须修复验证中发现的问题，重新验证，不能只报告失败就结束；禁止无进展重复同一失败操作。
- 默认从 `Plain Craft Launcher 2/metadata.json` 读取 `x.y.z`，递增 patch 为 `x.y.(z+1)`；异常格式、预发布后缀或版本策略不明确时先询问。
- 同步 `metadata.json`、`AssemblyVersion`、`FileVersion`、tag 与 workflow 输入；tag 使用 `vX.Y.Z`。
- 验证通过后提交并推送分支和 tag；必须使用 GitHub Actions 构建和上传 Release 资源，不用本地产物替代。
- 默认创建正式 Release；只有 Actions 构建、测试、签名和上传全部成功才算发布完成。
- Actions 失败时读取日志、修复根因、重新验证、推送修复并重新运行；遇到权限、凭据、破坏性操作或需要选择的策略时暂停询问。
