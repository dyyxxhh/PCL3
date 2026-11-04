# PCL Linux CLI

PCL₃ Linux CLI 使用 `PCL.Core.Shared` 中可跨平台的 Minecraft、模组平台和实例服务。Windows 当前仅复用其中部分基础能力，尚未接入新增的模组和整合包服务。Linux 入口独立运行，不加载 WPF；它仍不是 Windows 桌面版的完整替代品。

## 使用

发布包包含 self-contained 的 Linux x64 `pcl` 可执行文件，不需要单独安装 .NET Runtime。开发构建需要 .NET 10 SDK。首次 Microsoft 登录还需要已授权的 `PCL_MS_CLIENT_ID`。

```sh
pcl -S 1.21.1 --instance FabricTest --loader fabric
pcl -Ss sodium --game-version 1.21.1 --loader fabric
pcl -S sodium --instance FabricTest
pcl mod list --instance FabricTest
pcl mod update sodium.jar --instance FabricTest
pcl mod disable sodium.jar --instance FabricTest
pcl mod enable sodium.jar.disabled --instance FabricTest
pcl mod remove sodium.jar --instance FabricTest
pcl pack import ./pack.mrpack --instance MyPack
pcl pack export ./MyPack.mrpack --instance MyPack
pcl instance list
pcl launch FabricTest
```

`-S` 安装 Minecraft 版本或模组：Minecraft 版本参数创建原版实例；加 `--loader forge|neoforge|fabric|quilt` 会同时安装对应加载器。模组名称可先用 `-Ss` 搜索；默认安装目标版本/加载器兼容的最新文件，也可用 `--mod-version <版本号或文件 ID>` 固定版本。模组搜索默认查询 Modrinth 和 CurseForge；重名时终端会列出候选供选择，可用 `--source modrinth|curseforge` 限定来源。

每个命名实例有单独的游戏目录，模组不会跨实例共享。安装模组时省略 `--instance` 会在交互终端选择已有实例；没有实例时会询问 Minecraft 版本、加载器和实例名。整合包导入省略实例名时也会询问。非交互运行必须提供需要选择的参数；CLI 不会静默猜选。

Modrinth `.mrpack`、CurseForge 标准 manifest ZIP、HMCL、MCBBS 和 MultiMC 格式可导入；`.mrpack` 可导出 Modrinth 格式，`.zip` 可导出 CurseForge manifest。导出只引用有跟踪来源、文件 ID/下载 URL 和校验和的模组，不会把未授权的第三方 JAR 塞进整合包。导入的远端文件要求 HTTPS 和 SHA-1 校验；覆盖文件会检查路径、大小并拒绝越界写入和符号链接。

导出会保留 `config/`、`defaultconfigs/`、`kubejs/`、`scripts/`、`resourcepacks/`、`shaderpacks/` 与 `options.txt` 作为 overrides；其他自定义目录尚不自动导出。不会包含账户文件、存档、日志、运行时或符号链接，也不打包第三方 JAR。分享前仍应自行检查模组配置是否包含服务器密码等私密设置。

Release 的应用级 CurseForge key 由 Actions Secret `CURSEFORGE_API_KEY` 注入到带 `PCL_WRITE_SECRET=1` 的发布构建。Linux 生成器只允许读取此 key，以编码字节编译进二进制以降低明文误泄漏；不枚举其他环境变量，也不提供运行时环境回退。不将 key 写入源码、配置、日志或发布说明。普通本地构建不带 key；缺少 key 时 Modrinth 仍可使用，显式请求 CurseForge 会给出配置错误。编码不是加密，随客户端分发的应用 key 仍可被逆向提取，不能用于存放用户凭据。

## 当前范围

Linux CLI 支持原版 Minecraft 元数据、文件校验和启动；Forge、NeoForge、Fabric、Quilt 客户端加载器安装；Modrinth/CurseForge 模组搜索、兼容版本安装及本地模组更新、启用、禁用和删除；Modrinth `.mrpack` 与 CurseForge 标准 manifest 的导入/导出，以及 HMCL、MCBBS、MultiMC 基础清单和覆盖文件导入。

当前不支持 Windows 桌面 UI/TUI、OptiFine、旧版/变体加载器、整合包在线搜索、MultiMC JSON patch 的完整迁移、传统包内启动参数/实例设置迁移，以及 CurseForge 未提供下载 URL 的文件。`-S` 模组安装与 `mod update` 会解析必需模组依赖；整合包按清单下载文件，不递归改写包的固定依赖。远端 CurseForge 模组保留项目和文件 ID，Modrinth 模组按 SHA-1 查询来源；可识别来源的导入模组可继续更新和导出，未识别或包内自带的 JAR 仅支持本地维护（导出需要来源元数据）。Linux CLI 仍不等同 Windows 桌面端全部功能，也不支持 Yggdrasil Connect。

`pcl launch` 使用所选账户启动实例。离线账户、Microsoft device-code flow 和传统 Yggdrasil/authlib-injector 已支持。第三方登录密码仅在交互终端隐藏输入；访问令牌保存在用户配置目录并限制权限。首次下载后会校验内容；Java 版本不足时才从 Mojang 官方清单下载 Linux runtime。需要可用图形会话、显卡驱动和已授权账号才能验收实际游戏画面。

## 本地开发

```sh
dotnet build PCL.Linux.Cli/PCL.Linux.Cli.csproj
dotnet test PCL.Core.Shared.Tests/PCL.Core.Shared.Tests.csproj
```
