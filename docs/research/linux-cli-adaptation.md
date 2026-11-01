# Linux CLI/TUI 适配方案调研

调查日期：2026-11-09

## 结论

如果目标是尽量少改现有项目、保留 Windows 用户熟悉的体验，建议采用双入口：Windows 继续使用当前 WPF 客户端；Linux 新增独立 CLI，先实现安装与启动的最小闭环，之后再根据需求为同一套用例服务增加 TUI。

```text
现有 Windows WPF 客户端 ─┐
                          ├── 无 UI 的共享业务服务
Linux CLI（首期）────────┤
Linux TUI（可选后续）────┘
```

这条路线不需要重写 Windows UI，也不需要为 Linux 重做整套桌面 UI。但现有 `PCL.Core` 不能原样作为 Linux CLI 的项目引用：它本身目标为 Windows 并依赖 WPF。首期应只抽取 CLI 端到端用例需要的业务代码，避免先做大规模核心重构。

相比 Avalonia 或 Tauri/WebView，这条路线 Linux UI 能力较弱，但没有桌面 UI 的逐页移植成本。若后续用户需要菜单式终端交互，可在 CLI 用例稳定后增加 TUI，不应复制业务逻辑。

## 本地代码现状

- 桌面程序目标为 `net10.0-windows`，输出 `WinExe`，启用 `UseWPF`，并引用 `Markdig.Wpf`：[Plain Craft Launcher 2.csproj](../../Plain%20Craft%20Launcher%202/Plain%20Craft%20Launcher%202.csproj#L4-L22)、[同一项目的依赖项](../../Plain%20Craft%20Launcher%202/Plain%20Craft%20Launcher%202.csproj#L68-L82)。
- 核心项目同样目标为 `net10.0-windows` 并启用 WPF：[PCL.Core.csproj](../../PCL.Core/PCL.Core.csproj#L13-L23)。核心中不仅有 Minecraft/下载/配置代码，也有 WPF 生命周期服务，例如 [ApplicationService.cs](../../PCL.Core/App/Essentials/ApplicationService.cs#L1-L25)、[MainWindowService.cs](../../PCL.Core/App/Essentials/MainWindowService.cs#L1-L23)。
- 仓库中约有 100 个桌面 XAML 文件、数百个桌面 C# 文件；因此把整个桌面 UI 移植到另一个框架不是“小改项目文件”。CLI 路线不需要搬迁这些页面。
- 仍然需要处理平台代码。比如 Java 服务目前注册了 Windows 注册表扫描器：[JavaService.cs](../../PCL.Core/Minecraft/Java/JavaService.cs#L16-L35)；默认扫描器检查 `java.exe`：[DefaultPathsScanner.cs](../../PCL.Core/Minecraft/Java/Scanner/DefaultPathsScanner.cs#L85-L110)。Linux 需要找 `java`，并支持从 PATH、常见安装目录或用户手动指定位置发现 Java。
- 仓库有一定 MVVM/ViewModel 结构，但不少交互与 WPF 控件和 code-behind 直接耦合。不能假设现有所有 ViewModel 都是可在无 UI 环境中直接运行的用例服务。
- 当前 CI 在 Ubuntu runner 上构建 Windows 目标，不代表已经支持 Linux 原生运行：[reusable-build.yml](../../.github/workflows/reusable-build.yml#L16-L20)、[Windows 目标发布命令](../../.github/workflows/reusable-build.yml#L46-L60)。

## 目标结构

建议逐步建立以下边界，而不是一次性重写 `PCL.Core`：

1. **共享业务层**：普通 `net10.0` 项目，仅包含 CLI 闭环确实需要的领域模型、版本/文件元数据、下载、安装、配置读写、启动参数组装等逻辑。这里禁止依赖 `System.Windows`、WPF 生命周期和 Windows P/Invoke。
2. **平台能力层**：将应用/游戏目录、Java 发现、浏览器打开、凭据存储、进程启动与终止等操作放到小型接口后面。Windows 继续使用现有实现；Linux 新增 Linux 实现。
3. **Windows 前端**：现有 WPF 项目和发布方式先保持不变。可在共享层抽取稳定后逐步引用共享服务；每次迁移都要验证 Windows 行为。
4. **Linux CLI**：独立的 `net10.0` 可执行项目，负责命令解析、非交互参数、交互输入、进度及日志输出。
5. **可选 TUI**：后续仅替换 CLI 的交互层，复用相同的共享业务服务。不要把业务逻辑放在 TUI 页面事件处理器中。

文件迁移可先采用小范围抽取：沿一个用例移动/改造其依赖，建立边界和测试，再扩大共享层。不要把所有核心源文件一次性链接到新项目，因为其中可能通过间接依赖重新带入 WPF 或 Windows 专属 API。

## Linux 首期范围

推荐以 Linux x64 为首个正式目标，先交付一个边界明确的闭环：

- `--help`、版本信息和清晰的命令错误；
- 列出实例，配置或选择 Minecraft 目录；
- 检测 Java 17/21，并支持手动指定 Java 可执行文件；
- 安装一个明确选定范围内的 Minecraft 版本；
- 显示下载进度、错误和日志；
- 启动一个本地实例，正确使用 Linux Java 可执行文件与游戏 natives；
- 用桌面会话启动游戏进程，使 Minecraft 自己创建游戏窗口；启动器本身仍在终端运行。

可采用的命令形态示例（最终命名应按现有 CLI 约定确认）：

```text
pcl --help
pcl instance list
pcl java scan
pcl install <version>
pcl launch <instance>
```

首期可以暂缓 TUI、完整加载器/整合包支持、联机大厅/EasyTier、Windows 快捷方式、任务栏集成、注册表扫描、系统主题集成以及 Windows 更新器流程。未实现的功能应返回明确的“不支持”或平台能力错误，不能悄悄失败。

### 认证与密钥

账号功能需在实现前明确首期支持范围。浏览器 OAuth 流程可能需要本地回调或设备码模式；凭据存储不能继续依赖只适用于 Windows 的 DPAPI/CNG。Linux 应优先集成系统凭据存储；若提供文件存储降级方式，应限制文件权限、明确风险且绝不把令牌写入日志。没有确定的安全存储策略前，可以先把登录作为阶段门，不要为了快速做出命令而明文保存凭据。

## 改动量与成本估计

以下是用于选路线的粗略估算，不是工期承诺。假设由熟悉 C#/.NET 的工程师实施，首期只支持 Linux x64、一个版本安装/启动闭环，且暂不实现大厅等非必要功能。

| 交付范围 | 大致改动范围 | 粗略投入 |
|---|---|---|
| Linux CLI MVP | 新增 CLI 入口、小型共享项目、必要的平台接口/实现和测试；按需抽取现有业务代码，避免迁移整个 UI | 约数十个源文件以内，约 1–3 工程师月 |
| 可选 TUI | 新增终端交互层和状态视图，复用 CLI 已调用的业务服务 | 数周至数月，取决于交互深度 |
| 完整追平 Windows 功能 | 多加载器、整合包、账号、Java 管理、更新、Mod/存档管理、联机等逐项迁移和验证 | 多个工程师月以上；需先按功能重新估算 |

最大的估算不确定性是 Minecraft 启动与下载路径对 WPF 类型、全局静态状态或 Windows 平台服务的间接依赖。建议先做一个技术切片：`扫描/指定 Java -> 安装一个版本 -> 启动游戏`。切片完成后，根据实际被带入的依赖和测试范围更新估算。

## 与另外两条路线的比较

| 路线 | Linux 形态 | 现有 Windows 体验 | 主要改动 | 上游更新跟进 |
|---|---|---|---|---|
| CLI，之后可选 TUI | 终端命令/文本交互 | 保持 WPF，不必改变首期 Windows 路径 | 抽取无 UI 业务用例，新增 Linux 平台服务和命令入口 | 对业务逻辑可直接共享；不需要同步另一套 Linux 页面，Linux 功能仍需单独验收 |
| Avalonia（PCL.Neo 路线） | 原生跨平台桌面 UI | 可保留 Windows WPF 客户端，但要维护第二套 UI | 迁移页面、控件、窗口和 WPF 耦合的 Core/UI 服务 | C# 业务逻辑抽离后较容易共享；上游 WPF 页面改动仍要移植 |
| Vue + Tauri/WebView（PCL.Proto 路线） | WebView 桌面 UI，也可维护 Web 前端 | 可保留 Windows WPF，但意味着并行维护前端 | 新建 Web UI，并重做/桥接现有 C# 业务后端；Tauri 常用 Rust 命令后端 | UI 行为需从 WPF 映射到 Vue；保留稳定 C# 后端/IPC 可改善业务更新，但带来桥接维护 |

**如果优先级是“Windows 不变、Linux 先可用、少改源码”，CLI/TUI 更适合。** 如果将来明确需要完整图形桌面版，Avalonia 是保留 C# 生态较自然的下一条路线；Tauri 更适合团队已有 Web 前端能力且愿意维护 Web/Rust 或 C# IPC 架构的情形。

## 上游更新跟进策略

CLI/TUI 不能自动跟上游，但可以避免因 Linux UI 重写导致的额外冲突：

- 记录每次同步所基于的 PCL-CE commit，保留可重复的上游合并流程。
- 尽量把平台中立的下载、Minecraft 元数据、实例模型、认证协议和启动参数逻辑放在共享项目；Windows 与 Linux 都调用同一份实现。
- 用行为测试固定共享服务的关键契约，例如命令参数生成、版本文件清单、下载校验与失败状态。
- 上游 WPF 页面、窗口、注册表或系统集成功能不会自动映射到 CLI。每次同步应标记哪些领域修复进入共享层、哪些桌面交互不适用于 Linux。

因此，**CLI/TUI 对共享业务更新的跟进速度可以接近一套代码，但它不会让全部上游功能无成本地同步到 Linux**。可复用边界和回归测试比前端技术名称更决定长期跟进速度。

## 主要 Linux 风险

- **Windows Core 依赖**：直接引用当前 `PCL.Core` 会把 WPF/Windows 目标带进 Linux 项目；必须按用例抽取，且持续检查共享项目依赖。
- **路径及大小写**：Linux 文件系统通常大小写敏感；检查文件名、目录拼接、配置迁移与资源定位，不能只在 Windows 测试。
- **Java 与 natives**：Linux 使用 `java`，并需选择 Linux 版游戏原生库；启动参数、可执行权限及架构需在实际 Linux 环境验证。
- **目录规则**：应用数据、缓存、配置和游戏目录要有清晰默认值，并尊重用户显式配置；避免把程序目录写入当成唯一存储策略。
- **进程与浏览器**：使用跨平台进程 API 和浏览器打开方式，避免 `cmd.exe`、PowerShell、`taskkill.exe` 等命令进入共享路径。
- **密钥与登录**：Windows 保护数据机制不能视为 Linux 凭据方案；需要单独选定凭据存储和 OAuth 回调策略。
- **测试环境**：Ubuntu 构建通过不足以证明 Minecraft 可正常启动；需在真实 Linux 桌面/用户会话中验证游戏进程与 natives。

## 测试与发布验收

1. 当前 Windows WPF 构建、现有测试和发布产物不回归。
2. 新 CLI 在 Ubuntu Linux x64 构建，并能在无图形界面的 CI 中运行参数解析和业务测试。
3. Linux 集成测试覆盖 Java 17/21、路径大小写、无权限目录、下载失败/取消、进程退出及日志脱敏。
4. 至少一种 Minecraft 版本在真实 Linux 桌面会话内完成下载并成功启动，包含实际 Linux natives。
5. 凭据错误、缺失平台服务及不支持的命令均有清晰提示；不会静默回退到 Windows 命令。
6. 评估 TUI 前先确认 CLI MVP 的 use case 服务不依赖终端渲染库，保证后续可无 UI 测试。

## 官方项目参考

- **PCL.Neo**：官方 README 将其定位为基于 Avalonia 重构的跨平台 PCL，目标包括 Windows、Linux 和 macOS：[PCL.Neo README](https://github.com/PCL-Community/PCL.Neo/blob/main/README.md)。其 `main` 项目文件使用普通 `net10.0` 和 Avalonia 依赖，而不是 WPF：[PCL.Neo.csproj](https://github.com/PCL-Community/PCL.Neo/blob/main/PCL.Neo/PCL.Neo.csproj)。旧主分支的 CI 在 Ubuntu 上构建 Linux x64/ARM64 AppImage：[PCL.Neo build workflow](https://github.com/PCL-Community/PCL.Neo/blob/main/.github/workflows/build.yml)。README 还提示主线开发暂缓并指向 `next`，因此依赖其实现前应确认采用的分支和实际维护状态。
- **PCL.Proto**：官方 README 说明项目使用 Vue3，并将其定位为可供 WebView 前端参考的原型；当前桌面端使用 Tauri、Vue3/Vite 和 Rust：[PCL.Proto README](https://github.com/PCL-Community/PCL.Proto/blob/main/README.md)。Tauri 配置把 `dist` 作为前端产物：[tauri.conf.json](https://github.com/PCL-Community/PCL.Proto/blob/main/src-tauri/tauri.conf.json)；Linux 窗口配置独立维护：[tauri.linux.conf.json](https://github.com/PCL-Community/PCL.Proto/blob/main/src-tauri/tauri.linux.conf.json)。Ubuntu CI 安装 WebKitGTK 等依赖并构建 Linux 桌面包：[PCL.Proto build workflow](https://github.com/PCL-Community/PCL.Proto/blob/main/.github/workflows/build-only.yml)。这条路线是 WebView 桌面架构参考，不是当前 WPF 项目可直接接入的 UI。

## 假设与范围

- Linux 目标是原生 CLI，而不是 Wine 下运行 Windows WPF；TUI 是可选后续项目。
- 第一阶段只保证 Linux x64；ARM64、发行版包矩阵和完整功能对齐不在 MVP 范围内。
- 上述成本以受控 MVP 为前提，必须在切片验证后更新。
- 本文仅是工程调研和计划，不构成法律意见，也不宣称当前项目已支持 Linux。
