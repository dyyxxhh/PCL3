# Minecraft 启动片头预览

片头由独立进程播放。Minecraft 完成首次资源加载、关闭加载叠层并绘出标题菜单后，播放器才显示「按空格键进入游戏」。在加载期间按住空格时，需要松开再按。进入游戏时画面淡出约 400 毫秒，片头音轨继续以原音量播放。正常交接中，播放器停止后才通知游戏恢复输入和音量。播放器意外退出或连接丢失时，游戏会解除输入和音量限制。

## 本地构建

需要 Python 3 和 JDK 9+。运行 Minecraft 1.21.1 需要 Java 21。构建脚本会验证 [ASM 9.8](https://repo.maven.apache.org/maven2/org/ow2/asm/asm/9.8/asm-9.8.jar) 的 SHA-256。

```sh
python3 PCL.GameIntro.Agent/build.py /absolute/asm-9.8.jar /absolute/PCL3_LaunchIntroAgent.jar
```

通过 MSBuild 参数 `-p:PclGameIntroClip=/absolute/PCL3_LaunchIntro.mp4` 和 `-p:PclGameIntroAgent=/absolute/PCL3_LaunchIntroAgent.jar` 指定私有预览资源。构建脚本会生成 agent 随附的 `.sha256` 文件。当前预览接受 5,209,693 字节的 H.264/AAC 转码，其 SHA-256 为 `e46a7af3098ee66b91d57a9f72b44153a65fb18d6a9f37bb8d63ea118b942692`。启动器会验证解包的资源；CI 与普通本地构建默认不嵌入，v0.4.3 正式发布会显式打包已授权资源。

用包含资源的构建（或 v0.4.3 官方发布包），将 `PCL3_GAME_INTRO_PREVIEW` 设为 `1` 后启动下表中的配置。MCM 已内嵌 Linux 播放助手，不需要额外下载；开发测试可通过 `PCL3_GAME_INTRO_LINUX_HOST` 指定 [host.py](<../PCL.GameIntro.Linux/host.py>) 的绝对路径。播放层依赖 X11 合成器、GTK 3、Python GI/Cairo、GStreamer H.264/AAC 解码和音频输出插件，以及 `xprop`。独占全屏、原生 Wayland、未知类指纹和需要交互的首次无障碍引导会走普通启动路径。

## 验证范围

| Minecraft Java 1.21.1 | Linux x64 隔离 X11，Java 21 | Windows |
| --- | --- | --- |
| 官方原版 | 真客户端，合成音视频全链路测试和私有片头卡顿测试 | WPF 交叉编译 |
| Fabric Loader 0.16.14 | 真客户端，合成音视频全链路、私有片头卡顿和首次引导回退测试 | WPF 交叉编译 |
| Forge 52.1.20 | 真客户端，合成音视频全链路和 MCM 私有片头首次启动测试 | WPF 交叉编译 |
| NeoForge 21.1.172 | 真客户端，合成音视频全链路和 MCM 私有片头首次启动测试 | WPF 交叉编译 |

四种配置的合成媒体测试均在暂停 Java 时观察到播放器完成一轮并继续输出非静音 PCM。恢复 Java 后到达标题菜单，松开空格并重新按下才淡出。播放器停止解码和音频输出后发送 `RELEASE`。

Fabric 通过 `fabric.systemLibraries` 暴露单个桥接 JAR，保留 Knot 的类路径隔离。Forge 和 NeoForge 在 FML 显示早期窗口之前绑定，并验证 Minecraft 接管同一 GLFW 窗口。Linux 在首次 GLFW 缓冲区交换前，最多等待 350 毫秒让覆盖层确认已提交首帧。四种配置都验证了缺少确认时可以继续普通启动。窗口失焦会隐藏覆盖层，恢复焦点后重新显示；提示未绘出时按空格不会触发交接。该确认不等于实际显示器无闪现：最近一轮 Xvfb 测试中，覆盖层比游戏窗口首次映射晚约 12–28 毫秒。

[Linux 覆盖层测试](<../PCL.GameIntro.Linux/test_x11_focus.py>) 检查覆盖确认、保留游戏焦点、长按空格、淡出和错误 IPC 停止顺序。OpenAL 增益恢复的实测使用空输出设备。Windows 本机播放、原生显示及实际声卡交接仍待验证。类指纹基于上表的干净客户端，整合包和其他加载器版本需要分别验证。
