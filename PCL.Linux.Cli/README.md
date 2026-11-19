# MCM Linux CLI

MCM Linux CLI 使用 `PCL.Core.Shared` 中可跨平台的 Minecraft、模组平台和实例服务。Windows 当前仅复用其中部分基础能力，尚未接入新增的模组和整合包服务。Linux 入口独立运行，不加载 WPF；它仍不是 Windows 桌面版的完整替代品。

## 使用

发布包 `MCM_Release_linux-x64.tar.gz` 包含 self-contained 的 Linux x64 `mcm` 可执行文件，不需要单独安装 .NET Runtime。开发构建需要 .NET 10 SDK。首次 Microsoft 登录还需要已授权的 `MCM_MS_CLIENT_ID`；为兼容旧配置，也接受 `PCL_MS_CLIENT_ID`。

解压后可运行 `./mcm --help`；将可执行文件放入 `PATH` 后可直接使用下面的 `mcm` 命令。正式版可用 `mcm update` 更新。

```sh
mcm info    # 当前版本、项目地址、文档、反馈入口和功能说明；无需联网
mcm update  # 从项目 GitHub Release 检查并更新 MCM；需要联网
```

`mcm update` 下载更新包时显示进度条、百分比、已下载/总大小、平均速度和预计剩余时间；服务器未提供总大小时显示已下载量和速度。交互终端在同一行刷新，重定向输出时定期输出普通文本，不写入终端控制符。下载后会单独提示 SHA-256 校验和解压阶段，验证通过后才替换原程序；下载出错或校验失败会保留原程序。
旧 `pcl` 可执行文件的更新器仍使用旧发布资源名，更名后的首次升级需手动下载 MCM 发布包，再改用 `mcm` 命令。

### 配置、Skill、Tor 与备份

```sh
mcm skill install
mcm config
mcm config list
mcm config get backup.enabled
mcm config set backup.enabled true
mcm config set backup.scope instance
mcm config set tor.enabled true
mcm config set tor.auto_bridges true
mcm config set tor.bridges 'obfs4 host:port FINGERPRINT cert=...;snowflake ...'
mcm backup create INSTANCE --scope saves
mcm backup list INSTANCE
mcm backup restore BACKUP --instance INSTANCE --clone
mcm tor status
mcm tor bridge add 'obfs4 host:port FINGERPRINT cert=...'
mcm tor bridge clear
```

`mcm skill install` 只会覆盖 MCM Skill 文件，不会删除其他 Skill；它会跳过没有检测到的 Codex、Claude Code、Gemini CLI 或 Cursor。下载内容先校验 SHA-256 和大小。Tor 默认关闭，仅在用户启用且公开下载连续失败时回退；账号登录、刷新和游戏联机不经过 Tor。自定义网桥优先，自动发现通过 Tor Project 的公开 bridge bootstrap 接口获取，完整网桥内容不会写入普通日志。

自动备份默认关闭；启用后在托管实例启动前备份，默认只保留 5 个自动备份。备份先尝试文件系统克隆/reflink，不支持时退回普通复制并提示。备份目录由实例位置决定，备份和还原拒绝符号链接、重解析点和越界路径；原地还原会先留下保护备份，`--clone` 用于还原到新实例。


```sh
mcm -S 1.21.1 --instance FabricTest --loader fabric
mcm -Ss sodium --game-version 1.21.1 --loader fabric
mcm -S sodium --instance FabricTest
mcm mod list --instance FabricTest
mcm mod update sodium.jar --instance FabricTest
mcm mod disable sodium.jar --instance FabricTest
mcm mod enable sodium.jar.disabled --instance FabricTest
mcm -R sodium.jar --instance FabricTest
mcm pack import ./pack.mrpack --instance MyPack
mcm pack export ./MyPack.mrpack --instance MyPack
mcm instance list
mcm scan
mcm scan /path/to/PCL/.minecraft
mcm scan /path/to/PCL/.minecraft --migrate --version "旧版本目录名" --instance ImportedPack
mcm launch FabricTest
```

`-S` 安装 Minecraft 版本或模组：Minecraft 版本参数创建原版实例；加 `--loader forge|neoforge|fabric|quilt` 会同时安装对应加载器。模组名称可先用 `-Ss` 搜索；默认安装目标版本/加载器兼容的最新文件，也可用 `--mod-version <版本号或文件 ID>` 固定版本。模组搜索默认查询 Modrinth 和 CurseForge；重名时终端会列出候选供选择，可用 `--source modrinth|curseforge` 限定来源。

`-R <file.jar>` 按实例删除模组，与可恢复的 `mod disable` 不同；原有 `mod remove` 命令仍可使用。启用和禁用可分别用 `mod enable`、`mod disable`。

新实例元数据和模组清单分别写入 `mcm-instance.json`、`mcm-mods.json`；旧 `pcl-instance.json`、`pcl-mods.json` 仍可读取，同时存在时优先读取新名称。账户和所选登录资料写入 `$XDG_CONFIG_HOME/mcm/`（默认 `~/.config/mcm/`），兼容读取旧 `pcl-linux/` 配置；退出登录会清除新旧位置中的账户资料，不会因旧文件而恢复登录。

安装 Minecraft、Java、加载器、模组和导入整合包时，CLI 会在标准错误流显示当前阶段和已用时间；Minecraft/Java 文件下载还会显示进度条、文件完成数和当前文件名（百分比按文件数计算）。交互终端约每 0.1 秒刷新等待动画，即使单个大文件仍在下载也有反馈；重定向标准错误流、使用 `TERM=dumb` 或运行 Forge/NeoForge 外部安装器时改为每隔约 5 秒输出普通文本状态，阶段切换和下载完成立即输出。加载器外部安装、模组下载和整合包文件导入暂时显示阶段与等待动画，不估算百分比。

每个命名实例有单独的游戏目录，模组不会跨实例共享。安装模组时省略 `--instance` 会在交互终端选择已有实例；没有实例时会询问 Minecraft 版本、加载器和实例名。整合包导入省略实例名时也会询问。非交互运行必须提供需要选择的参数；CLI 不会静默猜选。

Modrinth `.mrpack`、CurseForge 标准 manifest ZIP、HMCL、MCBBS 和 MultiMC 格式可导入；`.mrpack` 可导出 Modrinth 格式，`.zip` 可导出 CurseForge manifest。导出只引用有跟踪来源、文件 ID/下载 URL 和校验和的模组，不会把未授权的第三方 JAR 塞进整合包。导入的远端文件要求 HTTPS 和 SHA-1 校验；覆盖文件会检查路径、大小并拒绝越界写入和符号链接。

导出会保留 `config/`、`defaultconfigs/`、`kubejs/`、`scripts/`、`resourcepacks/`、`shaderpacks/` 与 `options.txt` 作为 overrides；其他自定义目录尚不自动导出。不会包含账户文件、存档、日志、运行时或符号链接，也不打包第三方 JAR。分享前仍应自行检查模组配置是否包含服务器密码等私密设置。

Release 的应用级 CurseForge key 由 GitHub Actions Secret `CURSEFORGE_API_KEY` 注入发布构建。Linux 生成器只允许读取此 key，以编码字节编译进二进制以降低明文误泄漏；不枚举其他环境变量，也不提供运行时环境回退。不将 key 写入源码、配置、日志或发布说明。普通本地构建不带 key；缺少 key 时 Modrinth 仍可使用，显式请求 CurseForge 会给出配置错误。编码不是加密，随客户端分发的应用 key 仍可被逆向提取，不能用于存放用户凭据。

## 完整离线安装（首期：Linux x86_64 / glibc）

`pack export` 的标准整合包通常只有下载清单，**不是完整离线包**。新增 `offline` 命令把已准备好的命名实例、完整 Java 运行时及 self-contained 单文件 MCM 放进同一个 ZIP。目标电脑不需要预装 Java、.NET 或 MCM，也不需要公网；但必须具有兼容的 Linux 系统、基本命令和图形驱动。此命令不支持 Windows、ARM、musl/Alpine，也不把 Windows 安装目录自动转换成 Linux 离线包。Windows 版使用独立的 v2 包适配、导出页和 PowerShell/CMD 局域网安装脚本，不能导入此 Linux v1 包；详情见 `docs/windows-offline-install.md`。

### 在有网的来源电脑准备并打包

请使用 self-contained 的单文件 MCM 发布版；`dotnet run` / 普通 framework-dependent 构建不能生成包含完整启动器的离线包。

```sh
mcm -S 1.21.1 --instance Travel --loader fabric
# 安装需要的模组、资源包与配置；建议先在来源电脑实际启动一次。
# Java 必须先准备好：1.21.1 通常使用 Java 21，其他版本按 CLI 提示选择。
mcm java install 21
mcm offline export ./Travel-offline.zip --instance Travel
mcm offline inspect ./Travel-offline.zip
```

`export`、`inspect` 和 `import` 都不联网。导出会检查本地客户端 JAR、启动依赖、assets 及旧 LWJGL 的已解压 natives；缺少文件或已知 SHA-1 不匹配会直接失败，必须先在来源电脑补齐。默认从实例自己的 `runtime/`，然后从游戏根的 `runtime/` 查找对应 Java；也可指定 `--java /path/to/portable-java-home` 或其 `bin/java`。使用自定义 `--game-dir` 时，准备 Java 也应设置同一 `MCM_GAME_DIR`。不支持只给一个 Java 二进制文件、复制 `/usr/bin/java` 而不带完整运行时，或含有指向 Java home 以外文件（例如 `/etc`）的发行版 JDK；请改用独立、可再分发的完整 Java 发行包。

包内包含：

- `versions/`、`libraries/`、`assets/`、`mods/` 及运行所需的本地文件；加载器必须已安装完成。
- `config/`、`defaultconfigs/`、`kubejs/`、`scripts/`、`resourcepacks/`、`shaderpacks/`、旧版 `resources/`、常用选项与模组跟踪信息。
- 完整 Java home（含库和许可证），以及内置 .NET Runtime 的单文件 MCM。

不包含账户、已知令牌文件、存档、日志、截图或其他未列出的自定义目录。Java 内部的普通文件链接会复制成普通文件；游戏文件链接、目录链接以及指向 Java home 外部的链接会被拒绝。导出时请关闭游戏、模组安装和自动更新，避免来源文件同时变化。**模组的任意配置可能自行保存密码、服务器地址或其他隐私，分享前仍需人工检查。** 这是个人备份/迁移功能，不代表取得游戏、模组或 Java 的公开再分发许可，请遵守相关许可证。

### 拷贝到完全断网的电脑

用 U 盘等拷贝整个 ZIP。目标电脑若没有 MCM，可用系统已有解压工具取出包内的 `bootstrap/mcm`，例如：

```sh
unzip ./Travel-offline.zip bootstrap/mcm -d ./offline-bootstrap
chmod +x ./offline-bootstrap/bootstrap/mcm
./offline-bootstrap/bootstrap/mcm offline import ./Travel-offline.zip --instance Travel
# 也可把这个 self-contained mcm 放入 ~/.local/bin。
./offline-bootstrap/bootstrap/mcm login offline MyPlayer
./offline-bootstrap/bootstrap/mcm launch Travel --offline
```

如果目标机连解压工具也没有，可以在来源电脑取出包里的 `bootstrap/mcm`，与 ZIP 一起拷贝；不需要再去目标机下载任何运行时。`--game-dir <path>` 可指定目标游戏根，`--instance <name>` 可重命名。导入按文件检查 SHA-256，校验本地游戏依赖和 Java 版本，在同一磁盘的临时目录完成后才登记实例；出错或取消会清理临时实例。**不会覆盖同名实例，也不会覆盖已有导出文件。** 请预留 ZIP、展开后的实例及临时文件所需空间。

`launch --offline` 是明确的禁止联网模式：不自动安装/修补游戏或 Java，不刷新 Microsoft/第三方账户；必须先 `login offline <玩家名>`。普通 `launch` 保留原有账户和自动安装行为。离线账户不等于 Microsoft 离线令牌，也不会绕过在线服务器的正版验证；有些模组自己的首次下载、授权或在线服务不受启动器控制，应在断网前验证整套实例。

### 局域网一键安装（无公网）

来源电脑先生成完整离线包，再启动只读分发服务：

```sh
mcm offline serve ./Travel-offline.zip
# 默认监听本机接口的 5078 端口，也可 --bind 192.168.1.10 --port 5078。
```

同一可信局域网的目标电脑执行（换成服务端实际 IP；可无互联网）：

```sh
curl -fS --noproxy '*' http://192.168.1.10:5078/ | bash
```

脚本仅从该局域网端口下载包内的 MCM 和完整离线包，先校验 SHA-256，再安装到 `~/.local/bin/mcm` 和 `~/.minecraft/instances/<包内实例名>/`；不调用软件源、GitHub、Mojang、模组平台或在线登录，不自动启动游戏，也不修改账户。来源服务启动前会校验整包，启动后请勿修改原 ZIP。已有不同版本的 `mcm` 不会被覆盖，相同二进制可复用；已有实例仍会拒绝覆盖。

可用环境变量指定安装位置和新实例名（变量要传给管道右侧的 Bash）：

```sh
curl -fS --noproxy '*' http://192.168.1.10:5078/ | \
  MCM_INSTALL_DIR="$HOME/offline-bin" MCM_GAME_DIR="$HOME/Games/Minecraft" MCM_INSTANCE=TravelCopy bash
```

目标系统需已有 Bash、curl 和 coreutils；USB 导入则不需要 curl。服务只接受回环、RFC 1918 私网、链路本地和 IPv6 ULA 地址，只提供 `/`、`/install.sh`、`/mcm`、`/bundle.zip` 的 GET/HEAD，不提供上传或目录浏览。默认最多同时处理 8 个请求，单次传输限时 30 分钟，Ctrl+C 停止服务。

**HTTP 不加密、不认证，SHA-256 只能发现损坏，不能证明分发者可信。只在可信局域网使用，不要映射到公网，也不要执行陌生服务器的 `curl | bash`。** 可先将根路径脚本下载为本地文件，人工检查后再执行。防火墙只需向可信局域网开放服务端选定端口，不应关闭整个防火墙。

导出 Java 的查找顺序为：显式 `--java`、实例内的运行时、游戏根目录的运行时、兼容的 `JAVA_HOME`、系统 Java 扫描。`--java` 可以指向完整 Java home 或 `bin/java`，会解析 `default-runtime`、`/usr/bin/java` 等链接，并将完整运行时复制进包；目标机不需要预装 Java。版本必须与游戏要求一致，导出不会联网下载。

Arch/Debian 等发行版将 Java 配置、公共证书和许可证放在安装目录外时，导出会在受限的系统路径范围内将它们展开成包内普通文件/目录。不会复制全系统手册、可选源码包或 JMX 密码文件，也不会跟随任意外部链接。无法转换的布局会明确报错，可用 `--java` 指定便携版 Java。

### 局域网下载进度、并行和访问日志

`curl ... | bash` 安装过程中，MCM 和离线包分别显示下载百分比、已下载/总量、平均速度以及预计剩余秒数。大于等于 4 MiB 的文件默认使用 **4 个连接分段下载**；小文件使用单连接。服务端支持单范围 HTTP Range 请求，最多同时处理 8 个请求。分段下载失败会自动清理分片，回退单连接重试；最终仍须通过完整 SHA-256 校验才会执行安装。

可设置客户端连接数为 1–8（变量需要传给 `bash`，而不是管道左边的 `curl`）：

```sh
# 默认 4 连接；IP 换成实际服务端地址
curl -fS --noproxy '*' http://192.168.1.2:5078 | bash
# 硬盘较慢、并发客户端较多或想关闭分段时，改用 1 连接
curl -fS --noproxy '*' http://192.168.1.2:5078 | MCM_DOWNLOAD_CONNECTIONS=1 bash
```

并行下载不能突破网卡、Wi-Fi、路由器或磁盘的上限；机械硬盘上随机读取可能反而更慢。分片在临时目录中下载后合并，需要额外的临时磁盘空间；SHA-256 验证和导入解压是下载之后的独立步骤，不算作网络传输时间。

`mcm offline serve` 会在终端输出每次访问的开始和结束记录：时间、客户端 IP、允许的路径、HTTP 状态码、写出字节数、耗时以及是否中断；分段请求显示 `206` 和字节范围。未允许的路径统一记作 `[other]`，不会把查询参数、任意路径或账户信息写入日志。可由终端重定向保存输出；日志包含局域网客户端 IP，请按需保护和清理。

### 显卡驱动、系统库和断网前检查

```sh
mcm offline doctor --instance Travel
```

该命令不联网，检查本地游戏文件、可用的本地 Java、Linux OpenGL/X11/Wayland/音频库能否加载及图形会话环境变量。它会区分实例内置 Java 和本机 Java；本机有兼容 Java 时不会仅因实例没有 runtime 目录就报告 Java 失败。检查失败返回非零；通过也**不代表**已经创建 OpenGL 上下文或实际验证游戏画面，较老的 Minecraft/LWJGL 在 Wayland 桌面下仍可能需要 XWayland。

| 内容 | 是否打包 / 处理方法 |
| --- | --- |
| 游戏、加载器、模组、资源、完整 Java、MCM/.NET Runtime | 包含在离线包中，不从公网补下载。 |
| glibc、ICU、OpenSSL、zlib 等 Linux/.NET/Java 系统依赖 | 由目标发行版提供，不能从来源机器随意复制系统 `.so`；按目标发行版及版本准备离线安装介质或完整依赖包缓存。 |
| NVIDIA/AMD/Intel 驱动、Mesa/OpenGL、X11/Wayland/XWayland、音频库 | 依赖目标 GPU、内核、发行版和版本，不复制来源电脑的驱动，不自动 `sudo` 或修改系统。 |
| 不支持所需 OpenGL 的老显卡 | 打包无法修复硬件能力；需要兼容的驱动/游戏版本或更换硬件。 |

推荐先确认目标机的发行版、版本、CPU 架构、GPU 和内核，再在相同环境的有网机器/虚拟机中准备发行版原生离线软件包及**全部传递依赖**，或使用发行版安装介质。NVIDIA 内核模块尤其需要匹配目标内核；AMD/Intel 通常由发行版内核与 Mesa 提供用户态支持，但仍须在目标机实测。不要因为来源电脑能玩，就把它的 `/usr/lib` 或显卡驱动拷过去。

参考：[.NET Linux 部署依赖](https://learn.microsoft.com/en-us/dotnet/core/install/linux)、[GLFW 平台与 OpenGL 兼容性](https://www.glfw.org/docs/latest/compat_guide.html)。协议与安全边界见 [离线包格式说明](../docs/offline-bundles.md)。

## 目录扫描与旧 PCL 迁移

游戏根目录按 `--game-dir`、环境变量 `MCM_GAME_DIR`（兼容旧变量 `PCL_GAME_DIR`）、用户主目录的 `~/.minecraft`、可执行文件旁的 `.minecraft` 依次选择。最后两个位置取第一个已存在的目录；两者都不存在时以 `~/.minecraft` 作为新安装目录。这里的“可执行文件旁”不是终端当前工作目录。主目录中的 `.minecraft` 即使没有版本，仍优先于同级目录。

`mcm scan` 会检查选定游戏根目录和两个默认位置，并显示版本、原版 Minecraft 版本、加载器和游戏数据位置。可传入旧 `.minecraft`、它的父目录、`versions` 或某个版本目录；扫描会定位到游戏根目录。支持原版 PCL 的 `versions/<目录名>/<目录名>.json`，以及同名 JSON 缺失但版本目录中只有一个 JSON 的情况。损坏 JSON、缺失父版本、循环继承、客户端 JAR 缺失等会显示跳过原因，而不是把它们当作空列表。

`instance list` 也会显示仅有一个非同名 JSON 的旧版本；直接启动此类版本或需要继承合并的旧版本时，会提示先迁移，不会误把旧实例名当作 Mojang 版本重新下载。

在交互终端中，扫描后可以选择一个版本迁移，并输入新的 MCM 实例名。非交互调用需加 `--migrate --version <旧版本目录名> --instance <新实例名>`；只有一个扫描结果时可省略 `--version`。`--game-dir <目录>` 指定迁移目标游戏根；位置参数指定扫描来源。

迁移通过复制创建 `instances/<新实例名>/`，保留原目录，拒绝覆盖已有实例。会平铺版本继承、保存独立实例元数据，并复制公共 `assets/`、`libraries/`、客户端 JAR，以及选定数据目录中的模组、配置、存档、资源包、光影、脚本、截图和常用游戏选项。不复制启动器账户、令牌、PCL 设置或 Java 运行时；未知自定义目录尚不自动复制。公共资源会复制到每个新实例，需预留相应磁盘空间。

版本隔离只影响游戏数据来源，不影响版本 JSON 的发现。迁移优先读取版本自己的 PCL 隔离设置（新 YAML/JSON 配置文件优先，兼容旧 `PCL/Setup.ini`）；没有明确设置时，根据根目录和版本目录中的实际数据判断。两处都有数据且无法确认时会要求选择，也可用 `--data-dir <旧游戏根或旧版本目录>` 明确指定，避免混合两组模组或存档。不会修改旧 PCL 的设置，也不根据可能已失效的全局设置猜选。

迁移后的命名实例在启动时会按原版元数据补齐并校验 Linux 依赖、解压 Linux natives；可能需要网络和对应 Java。Windows 专用模组或本地库不会自动转换成 Linux 版本。迁移复制不跟随符号链接，遇到链接会报出路径；请使用普通目录副本。尚不导入旧启动器的 JVM/内存等实例设置。

## 当前范围

Linux CLI 支持原版 Minecraft 元数据、文件校验和启动；Forge、NeoForge、Fabric、Quilt 客户端加载器安装；Modrinth/CurseForge 模组搜索、兼容版本安装及本地模组更新、启用、禁用和删除；Modrinth `.mrpack` 与 CurseForge 标准 manifest 的导入/导出，以及 HMCL、MCBBS、MultiMC 基础清单和覆盖文件导入。

当前不支持 Windows 桌面 UI/TUI、OptiFine、旧版/变体加载器、整合包在线搜索、MultiMC JSON patch 的完整迁移、传统包内启动参数/实例设置迁移，以及 CurseForge 未提供下载 URL 的文件。`-S` 模组安装与 `mod update` 会解析必需模组依赖；整合包按清单下载文件，不递归改写包的固定依赖。远端 CurseForge 模组保留项目和文件 ID，Modrinth 模组按 SHA-1 查询来源；可识别来源的导入模组可继续更新和导出，未识别或包内自带的 JAR 仅支持本地维护（导出需要来源元数据）。Linux CLI 仍不等同 Windows 桌面端全部功能，也不支持 Yggdrasil Connect。

`mcm launch` 使用所选账户启动实例。离线账户、Microsoft device-code flow 和传统 Yggdrasil/authlib-injector 已支持。第三方登录密码仅在交互终端隐藏输入；访问令牌保存在用户配置目录并限制权限。首次下载后会校验内容；普通启动缺少对应 Java 时才从 Mojang 官方清单下载 Linux runtime；`--offline` 明确禁止该行为。需要可用图形会话、显卡驱动和已授权账号才能验收实际游戏画面。

MCM 默认根据系统可用内存预算设置 Java 最大堆（约四分之一，上限 4096 MiB）。可用 `mcm launch my --memory 6144` 覆盖单次启动，或用 `mcm config set memory.max_mib 6144 --instance my` 保存实例设置。设为 `auto` 可恢复自动分配。

## 本地开发

```sh
dotnet build PCL.Linux.Cli/PCL.Linux.Cli.csproj
dotnet test PCL.Core.Shared.Tests/PCL.Core.Shared.Tests.csproj
# 对 self-contained 单文件发布产物执行，无真实账户/公网依赖：
python3 scripts/check_mcm_offline.py /absolute/path/to/mcm
```
