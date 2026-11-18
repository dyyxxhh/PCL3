# MCM 完整离线包 v1

本说明的 v1 包继续支持 **Linux x86_64 / glibc**。Windows 新增 v2 包适配、版本导出页入口及 PowerShell/CMD 局域网安装脚本。Windows 离线包内的启动器固定使用自带运行时的压缩单文件版；框架依赖版导出时会按当前版本与架构从 GitHub Release 下载并校验该产物（缓存在 `PCL\Offline\`），因此首次导出需要联网。Windows 文件布局和 Java 选择仍由 Windows 前端处理，不能据此声称 Windows/Linux 已共享所有安装或启动逻辑。Windows 使用与验证限制见 `windows-offline-install.md`。

## 格式

ZIP 由 `PCL.Core.Shared/OfflineBundleService.cs` 处理，不是 `.mrpack` 或 CurseForge manifest 包。领域逻辑放在 `net10.0` 共享项目中；CLI 命令、终端诊断、HTTP 服务和 Bash 适配留在 Linux 前端。

- `mcm-offline.json`：UTF-8 JSON，camelCase 属性，`format = "mcm-offline"`、`formatVersion = 1`。
- `instance/`：可移植实例内容，导入时去掉此前缀写入新的命名实例。
- `instance/runtime/offline-java/`：完整 Java home，Linux Java 入口固定为 `runtime/offline-java/bin/java`。
- `bootstrap/mcm`：内置 .NET Runtime 与原生运行时库的单文件 MCM 发布产物。不依赖目标机预装 .NET；不包含发行版系统库或驱动。

主要清单字段：

```json
{
  "format": "mcm-offline",
  "formatVersion": 1,
  "targetRuntime": "linux-x64",
  "createdAtUtc": "2026-11-15T00:00:00Z",
  "instance": {
    "name": "Travel",
    "gameVersion": "1.21.1",
    "loader": "fabric",
    "loaderVersion": "0.16.0",
    "launchVersion": "fabric-profile"
  },
  "javaMajorVersion": 21,
  "javaExecutable": "runtime/offline-java/bin/java",
  "launcherPath": "bootstrap/mcm",
  "files": [
    {
      "path": "instance/mods/example.jar",
      "size": 123,
      "sha256": "...64 hex characters...",
      "executable": false
    }
  ]
}
```

示例的 `files` 被省略，不能作为有效离线包使用。每个文件必须有且只有一条清单项，ZIP 除清单以外不得含未声明条目。即使当前解析器能识别其他目标标记，导入/导出仍显式要求两端都是 `linux-x64`。未来 Windows 前端需实现本机运行时、bootstrap 和平台依赖的实际支持，不能只改清单中的 `targetRuntime`。

## 完整性与事务

- 导出不联网；检查已经合并的启动元数据、客户端 JAR、classpath、Linux native JAR、已解压旧 LWJGL natives、资源索引及对象。有已知 SHA-1 的文件必须匹配。缺失或损坏时拒绝导出，不用 URL 清单代替实际文件。
- 必须包含版本正确且可在来源机运行的完整 Java home 和运行中的 self-contained MCM。CLI 会自动发现兼容的系统 Java 或 `JAVA_HOME`，不要求先通过 MCM 下载另一份 Java；Java home 及可执行文件祖先中的链接先解析为真实路径。来源 CLI 的普通 `dotnet run` / framework-dependent 部署不允许导出。
- 导出先写临时 ZIP，再检查完整清单，最后以不覆盖方式移动到输出文件名；异常或取消删除本次临时 ZIP，不改已有文件。
- 导入在目标 `instances/` 下的 `.migration-offline-<随机值>` 中处理；检查所有条目的 SHA-256、本地游戏闭包及 Java 版本，再生成新的 `mcm-instance.json` 并原子移动目录。失败/取消删除本次临时目录；不覆盖已有实例或合并到已有目录。
- 不复制账户元数据；导入后不是需要在线修复的 legacy `Imported` 实例。使用 `launch --offline` 时禁止所有启动器下载/刷新分支，并要求用户明确选择离线账户。游戏、模组本身的联网行为不属于启动器禁止联网模式的进程沙箱。

## 限制与隐私

- ZIP 文件数上限 200,000，单个展开文件上限 8 GiB，总展开数据上限 64 GiB，清单展开上限 64 MiB。流式拷贝同时计数和哈希，不信任 ZIP 声明大小。
- 只允许显式列出的游戏目录/选项和 Java runtime；禁止相对越界、绝对路径、反斜杠、控制字符、Windows 保留文件名、重复/大小写冲突、文件/目录冲突、符号链接及特殊设备条目。不解压 ZIP 目录条目。
- 拒绝输出路径及祖先的符号链接。导出游戏目录也拒绝链接；Java home 内部的普通文件链接以及 `conf`/`legal` 下的目录链接会展开为普通文件/目录；仅对已知 `/etc/java*openjdk`（或 `/etc/java/`）配置、公共 Java CA 证书库和 `/usr/share/licenses/` 等许可证路径允许有限的外部链接。其余外部链接、目录循环均拒绝。不导出系统手册、可选 Java 源码归档和 JMX 密码文件。
- 只保存可执行位语义，不导入 setuid/setgid 或任意 Unix 权限。导入的实例根和服务器临时目录限制当前用户访问。
- 存档、日志、账户、已知令牌文件和启动器配置默认排除。允许目录中的任意模组配置仍可能含有私密信息；导出者必须人工检查。没有密码/令牌的迁移协议，不提供登录身份离线转移。
- 校验和提供传输完整性，不提供数字签名、发布者认证或对包内 Java/MCM/模组的沙箱。导入和执行前必须信任来源。

## LAN 分发

`PCL.Linux.Cli/OfflineInstallServer.cs` 启动前验证整个包，将其中的 bootstrap 提取到仅当前用户可读的临时目录。服务支持单范围 `Range: bytes=...`（有效请求返回 `206`，无效/多范围返回 `416`），并只提供固定 GET/HEAD 端点：根路径和 `/install.sh` 返回脚本，`/mcm` 返回包内启动器，`/bundle.zip` 返回原 ZIP。不是通用文件服务器，不接受上传，也不把 URL 映射到磁盘路径。

仅接受回环、RFC 1918、链路本地和 IPv6 ULA 对端。脚本的下载地址由服务端本地 socket 的 IP/端口生成，不信任 HTTP Host 或转发头；实例名按 Bash 单引号规则转义。curl 不使用代理、不跟随重定向，限定 HTTP 和当前 LAN 地址。两次下载都先验证启动时固定的 SHA-256，才执行 MCM 或写入安装路径；导入又检查包内每个文件。服务期间原 ZIP 如有变化，客户端会因固定校验和不符而停止。

最多并行处理 8 个请求，每个请求最多 30 分钟。Ctrl+C 停止监听、等待请求清理并删除临时 bootstrap。TCP/HTTP 是明文、无身份认证，只适用于可信私网。校验和不能防止同一网络中的主动中间人替换脚本及校验值；不要将服务暴露到公网，不要在不可信网络中直接执行 `curl | bash`。

Bash 脚本使用目标机已有的 `bash`、`curl`、`coreutils`，不访问包管理器或任何外部下载站点。安装同样不覆盖已有不同版本 MCM；若游戏导入失败，已安装的 MCM 留在原位便于诊断，临时下载文件仍会清理。实例导入本身是事务；“CLI 安装 + 实例安装”不声称是跨文件系统事务。

## 回归

- `OfflineBundleTests.cs`：完整 Java/MCM 往返、加载器 profile、隐私排除、内部 Java 链接展开、原版哈希、路径攻击、损坏/缺失文件、平台不匹配、权限、取消回滚、不覆盖实例和输出。
- `OfflineInstallServerTests.cs`：本地 mock 服务、固定路由、Host 不可信、GET/HEAD/405、并发/取消、局域网边界、Bash 语法。
- `scripts/check_mcm_offline.py`：self-contained 发布产物端到端测试。临时 HOME/配置、假游戏、可探测版本的独立 Java fixture、无真实账户、无公网依赖；从 ZIP 提取的 MCM 导入与显式离线启动，以及 loopback `curl | bash` 安装、重复实例保护、已有相同启动器复用和服务期间损坏包拒绝。

Windows v2 清单使用 `formatVersion = 2`、`layout = "pcl-windows"`，目标为同架构 `win-x64` 或 `win-arm64`，Java 入口为 `instance/runtime/offline-java/bin/java.exe`，启动器为 `bootstrap/PCL3.exe`。共享归档校验器可检查 v2 包；Windows 导入适配实现同架构校验、暂存验证及版本目录安装。Windows 前端导出页已接入完整包与 PowerShell/CMD 局域网安装命令；显式离线启动和桌面端到端验证仍需补齐。不要把 Linux `mcm offline` 包当作 Windows 离线包使用。

## 5. Windows 包 v2（接入中）

v2 保留逐文件 SHA-256 清单，但使用 Windows 的版本目录和 Java 可执行文件布局。包来源必须是可信的 Windows 同架构电脑；校验和只检测传输损坏，不验证分发者身份。Linux 安装器拒绝 Windows 布局，Windows 安装器拒绝 Linux 包和架构不匹配的 Windows 包。

来源机通过导出页启动只读 LAN 服务后，可直接复制 PowerShell 或 CMD 命令。命令下载并校验 PCL₃ 自带运行时压缩单文件启动器与完整离线 ZIP，再调用不启动 GUI、不联网的导入入口。它不要求 Bash、curl、管理员权限或永久调整执行策略。安装脚本的 PowerShell 5.1/7、CMD 执行及真实游戏启动必须在 Windows 环境验收，Linux 交叉构建不能替代。

当前 Windows 归档和导入适配位于 `PCL.Core.Shared/OfflineBundleService.Windows.cs`；普通目录映射、Java 运行能力和已验证的合并版本 JSON 应由 Windows 端准备。启动器、Java、Windows 原生 DLL、显卡驱动及 OpenGL 能力仍需目标电脑提供或验证；离线包不能替代系统驱动。

客户端按已知文件长度分段并行下载（默认 4 连接，`MCM_DOWNLOAD_CONNECTIONS=1..8`；不足 4 MiB 使用单连接），每 250 ms 汇总一次已下载字节并向 stderr 显示百分比、速度和 ETA。检查每段 HTTP 状态与长度，再按顺序合并，最后验证完整文件 SHA-256。分段失败回退单连接一次；取消时先停止并等待后台 curl，再删除临时文件，不留下继续写入的后台下载。

服务器日志使用请求编号关联开始/完成，记录客户端 IP、安全路由、状态、字节数、耗时和中断状态；不回显不受信任的 Host、User-Agent、查询字符串或任意 URL 路径。日志默认输出到终端。日志不证明导入或游戏启动成功；它只反映 HTTP 传输，客户端下载完成后的哈希和导入另行报告。
