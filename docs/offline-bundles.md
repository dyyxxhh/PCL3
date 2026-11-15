# MCM 完整离线包 v1

首期仅提供 **Linux x86_64 / glibc** 的导出、导入、只读 LAN 分发与显式离线启动。Windows WPF 未接入本功能，不能据此声称 Windows/Linux 已共享所有安装或启动逻辑。

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

`PCL.Linux.Cli/OfflineInstallServer.cs` 启动前验证整个包，将其中的 bootstrap 提取到仅当前用户可读的临时目录。服务只提供固定 GET/HEAD 端点：根路径和 `/install.sh` 返回脚本，`/mcm` 返回包内启动器，`/bundle.zip` 返回原 ZIP。不是通用文件服务器，不接受上传，也不把 URL 映射到磁盘路径。

仅接受回环、RFC 1918、链路本地和 IPv6 ULA 对端。脚本的下载地址由服务端本地 socket 的 IP/端口生成，不信任 HTTP Host 或转发头；实例名按 Bash 单引号规则转义。curl 不使用代理、不跟随重定向，限定 HTTP 和当前 LAN 地址。两次下载都先验证启动时固定的 SHA-256，才执行 MCM 或写入安装路径；导入又检查包内每个文件。服务期间原 ZIP 如有变化，客户端会因固定校验和不符而停止。

最多并行处理 8 个请求，每个请求最多 30 分钟。Ctrl+C 停止监听、等待请求清理并删除临时 bootstrap。TCP/HTTP 是明文、无身份认证，只适用于可信私网。校验和不能防止同一网络中的主动中间人替换脚本及校验值；不要将服务暴露到公网，不要在不可信网络中直接执行 `curl | bash`。

Bash 脚本使用目标机已有的 `bash`、`curl`、`coreutils`，不访问包管理器或任何外部下载站点。安装同样不覆盖已有不同版本 MCM；若游戏导入失败，已安装的 MCM 留在原位便于诊断，临时下载文件仍会清理。实例导入本身是事务；“CLI 安装 + 实例安装”不声称是跨文件系统事务。

## 回归

- `OfflineBundleTests.cs`：完整 Java/MCM 往返、加载器 profile、隐私排除、内部 Java 链接展开、原版哈希、路径攻击、损坏/缺失文件、平台不匹配、权限、取消回滚、不覆盖实例和输出。
- `OfflineInstallServerTests.cs`：本地 mock 服务、固定路由、Host 不可信、GET/HEAD/405、并发/取消、局域网边界、Bash 语法。
- `scripts/check_mcm_offline.py`：self-contained 发布产物端到端测试。临时 HOME/配置、假游戏、可探测版本的独立 Java fixture、无真实账户、无公网依赖；从 ZIP 提取的 MCM 导入与显式离线启动，以及 loopback `curl | bash` 安装、重复实例保护、已有相同启动器复用和服务期间损坏包拒绝。

这些测试不能替代不同发行版/GPU/内核上的真实桌面游戏验证。系统依赖与离线驱动准备方法见 Linux CLI README。
