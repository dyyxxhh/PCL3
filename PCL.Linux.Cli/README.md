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
pcl scan
pcl scan /path/to/PCL/.minecraft
pcl scan /path/to/PCL/.minecraft --migrate --version "旧版本目录名" --instance ImportedPack
pcl launch FabricTest
```

`-S` 安装 Minecraft 版本或模组：Minecraft 版本参数创建原版实例；加 `--loader forge|neoforge|fabric|quilt` 会同时安装对应加载器。模组名称可先用 `-Ss` 搜索；默认安装目标版本/加载器兼容的最新文件，也可用 `--mod-version <版本号或文件 ID>` 固定版本。模组搜索默认查询 Modrinth 和 CurseForge；重名时终端会列出候选供选择，可用 `--source modrinth|curseforge` 限定来源。

安装 Minecraft、Java、加载器、模组和导入整合包时，CLI 会在标准错误流显示当前阶段和已用时间；Minecraft/Java 文件下载还会显示进度条、文件完成数和当前文件名（百分比按文件数计算）。交互终端约每 0.1 秒刷新等待动画，即使单个大文件仍在下载也有反馈；重定向标准错误流、使用 `TERM=dumb` 或运行 Forge/NeoForge 外部安装器时改为每隔约 5 秒输出普通文本状态，阶段切换和下载完成立即输出。加载器外部安装、模组下载和整合包文件导入暂时显示阶段与等待动画，不估算百分比。

每个命名实例有单独的游戏目录，模组不会跨实例共享。安装模组时省略 `--instance` 会在交互终端选择已有实例；没有实例时会询问 Minecraft 版本、加载器和实例名。整合包导入省略实例名时也会询问。非交互运行必须提供需要选择的参数；CLI 不会静默猜选。

Modrinth `.mrpack`、CurseForge 标准 manifest ZIP、HMCL、MCBBS 和 MultiMC 格式可导入；`.mrpack` 可导出 Modrinth 格式，`.zip` 可导出 CurseForge manifest。导出只引用有跟踪来源、文件 ID/下载 URL 和校验和的模组，不会把未授权的第三方 JAR 塞进整合包。导入的远端文件要求 HTTPS 和 SHA-1 校验；覆盖文件会检查路径、大小并拒绝越界写入和符号链接。

导出会保留 `config/`、`defaultconfigs/`、`kubejs/`、`scripts/`、`resourcepacks/`、`shaderpacks/` 与 `options.txt` 作为 overrides；其他自定义目录尚不自动导出。不会包含账户文件、存档、日志、运行时或符号链接，也不打包第三方 JAR。分享前仍应自行检查模组配置是否包含服务器密码等私密设置。

Release 的应用级 CurseForge key 由 Actions Secret `CURSEFORGE_API_KEY` 注入到带 `PCL_WRITE_SECRET=1` 的发布构建。Linux 生成器只允许读取此 key，以编码字节编译进二进制以降低明文误泄漏；不枚举其他环境变量，也不提供运行时环境回退。不将 key 写入源码、配置、日志或发布说明。普通本地构建不带 key；缺少 key 时 Modrinth 仍可使用，显式请求 CurseForge 会给出配置错误。编码不是加密，随客户端分发的应用 key 仍可被逆向提取，不能用于存放用户凭据。

## 目录扫描与旧 PCL 迁移

游戏根目录按 `--game-dir`、环境变量 `PCL_GAME_DIR`、用户主目录的 `~/.minecraft`、可执行文件旁的 `.minecraft` 依次选择。最后两个位置取第一个已存在的目录；两者都不存在时以 `~/.minecraft` 作为新安装目录。这里的“可执行文件旁”不是终端当前工作目录。主目录中的 `.minecraft` 即使没有版本，仍优先于同级目录。

`pcl scan` 会检查选定游戏根目录和两个默认位置，并显示版本、原版 Minecraft 版本、加载器和游戏数据位置。可传入旧 `.minecraft`、它的父目录、`versions` 或某个版本目录；扫描会定位到游戏根目录。支持原版 PCL 的 `versions/<目录名>/<目录名>.json`，以及同名 JSON 缺失但版本目录中只有一个 JSON 的情况。损坏 JSON、缺失父版本、循环继承、客户端 JAR 缺失等会显示跳过原因，而不是把它们当作空列表。

`instance list` 也会显示仅有一个非同名 JSON 的旧版本；直接启动此类版本或需要继承合并的旧版本时，会提示先迁移，不会误把旧实例名当作 Mojang 版本重新下载。

在交互终端中，扫描后可以选择一个版本迁移，并输入新的 PCL3 实例名。非交互调用需加 `--migrate --version <旧版本目录名> --instance <新实例名>`；只有一个扫描结果时可省略 `--version`。`--game-dir <目录>` 指定迁移目标游戏根；位置参数指定扫描来源。

迁移通过复制创建 `instances/<新实例名>/`，保留原目录，拒绝覆盖已有实例。会平铺版本继承、保存独立实例元数据，并复制公共 `assets/`、`libraries/`、客户端 JAR，以及选定数据目录中的模组、配置、存档、资源包、光影、脚本、截图和常用游戏选项。不复制启动器账户、令牌、PCL 设置或 Java 运行时；未知自定义目录尚不自动复制。公共资源会复制到每个新实例，需预留相应磁盘空间。

版本隔离只影响游戏数据来源，不影响版本 JSON 的发现。迁移优先读取版本自己的 PCL 隔离设置（新 YAML/JSON 配置文件优先，兼容旧 `PCL/Setup.ini`）；没有明确设置时，根据根目录和版本目录中的实际数据判断。两处都有数据且无法确认时会要求选择，也可用 `--data-dir <旧游戏根或旧版本目录>` 明确指定，避免混合两组模组或存档。不会修改旧 PCL 的设置，也不根据可能已失效的全局设置猜选。

迁移后的命名实例在启动时会按原版元数据补齐并校验 Linux 依赖、解压 Linux natives；可能需要网络和对应 Java。Windows 专用模组或本地库不会自动转换成 Linux 版本。迁移复制不跟随符号链接，遇到链接会报出路径；请使用普通目录副本。尚不导入旧启动器的 JVM/内存等实例设置。

## 当前范围

Linux CLI 支持原版 Minecraft 元数据、文件校验和启动；Forge、NeoForge、Fabric、Quilt 客户端加载器安装；Modrinth/CurseForge 模组搜索、兼容版本安装及本地模组更新、启用、禁用和删除；Modrinth `.mrpack` 与 CurseForge 标准 manifest 的导入/导出，以及 HMCL、MCBBS、MultiMC 基础清单和覆盖文件导入。

当前不支持 Windows 桌面 UI/TUI、OptiFine、旧版/变体加载器、整合包在线搜索、MultiMC JSON patch 的完整迁移、传统包内启动参数/实例设置迁移，以及 CurseForge 未提供下载 URL 的文件。`-S` 模组安装与 `mod update` 会解析必需模组依赖；整合包按清单下载文件，不递归改写包的固定依赖。远端 CurseForge 模组保留项目和文件 ID，Modrinth 模组按 SHA-1 查询来源；可识别来源的导入模组可继续更新和导出，未识别或包内自带的 JAR 仅支持本地维护（导出需要来源元数据）。Linux CLI 仍不等同 Windows 桌面端全部功能，也不支持 Yggdrasil Connect。

`pcl launch` 使用所选账户启动实例。离线账户、Microsoft device-code flow 和传统 Yggdrasil/authlib-injector 已支持。第三方登录密码仅在交互终端隐藏输入；访问令牌保存在用户配置目录并限制权限。首次下载后会校验内容；Java 版本不足时才从 Mojang 官方清单下载 Linux runtime。需要可用图形会话、显卡驱动和已授权账号才能验收实际游戏画面。

## 本地开发

```sh
dotnet build PCL.Linux.Cli/PCL.Linux.Cli.csproj
dotnet test PCL.Core.Shared.Tests/PCL.Core.Shared.Tests.csproj
```
