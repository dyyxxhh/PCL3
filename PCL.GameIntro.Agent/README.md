# Minecraft 启动片头预览

片头由独立进程播放。Minecraft 完成首次资源加载、关闭加载叠层并绘出标题菜单后，播放器才淡入显示 `>按下空格键进入游戏<`。在加载期间按住空格时，需要松开再按。进入游戏时画面淡出约 400 毫秒，片头音轨继续以原音量播放。正常交接中，播放器停止后才通知游戏恢复输入和音量。播放器意外退出或连接丢失时，游戏会解除输入和音量限制。

## 本地构建

需要 Python 3 和 JDK 9+。运行 Minecraft 1.21.1 需要 Java 21。构建脚本会验证 [ASM 9.8](https://repo.maven.apache.org/maven2/org/ow2/asm/asm/9.8/asm-9.8.jar) 的 SHA-256。

```sh
python3 PCL.GameIntro.Agent/build.py /absolute/asm-9.8.jar /absolute/PCL3_LaunchIntroAgent.jar
```

通过 MSBuild 参数 `-p:PclGameIntroClip=/absolute/PCL3_LaunchIntro.mp4` 和 `-p:PclGameIntroAgent=/absolute/PCL3_LaunchIntroAgent.jar` 指定片头资源。构建脚本会生成 agent 随附的 `.sha256` 文件。正式发布使用仓库中获授权的 [原始 MP4](<../PCL.GameIntro.Linux/PCL3_LaunchIntro.mp4>)（1080p H.264、立体声 AAC、约 64.9 秒，14,408,526 字节），不重新编码。发布流程与启动器分别验证 SHA-256 `84bc0cae7a5b47adb7726e8a263da52ff907f3841884e97905d752a25ca41029`；普通 CI 与本地构建默认不嵌入。

用包含资源的发布包或本地构建启动下表中的配置。资源存在时，受支持的客户端默认启用片头；设置 `PCL3_GAME_INTRO_PREVIEW=0` 可关闭。MCM 已内嵌 Linux 播放助手，不需要额外下载；开发测试可通过 `PCL3_GAME_INTRO_LINUX_HOST` 指定 [host.py](<../PCL.GameIntro.Linux/host.py>) 的绝对路径。播放层依赖 GTK 3、Python GI/Cairo 以及 GStreamer H.264/AAC 解码和音频输出插件。X11 需要合成器和 `xprop`；原生 Wayland 需要 GTK 3 的 `gtk-layer-shell` GI 绑定和支持 layer-shell 的合成器（如 Hyprland），无需 `DISPLAY`。Linux 窗口和全屏均可使用片头；Wayland 覆盖当前输出，由游戏报告焦点，覆盖层不接收键盘或鼠标。Windows 全屏、没有 layer-shell 的 Wayland 合成器、未知类指纹和需要交互的首次无障碍引导会走普通启动路径。

## 验证范围

| 客户端 | Linux x64 隔离 X11 | Windows |
| --- | --- | --- |
| 26.3 官方原版 | Java 25、SDL3/Vulkan，真客户端与新片头交接 | WPF 交叉编译 |
| 1.21.11 官方原版 | Java 21、GLFW/OpenGL，真客户端与新片头交接 | WPF 交叉编译 |
| 1.21.1 官方原版 | 真客户端，合成音视频全链路测试和私有片头卡顿测试 | WPF 交叉编译 |
| 1.21.1 + Fabric Loader 0.16.14 | 真客户端，合成音视频全链路、私有片头卡顿和首次引导回退测试 | WPF 交叉编译 |
| 1.21.1 + Forge 52.1.20 | 真客户端，合成音视频全链路和 MCM 私有片头首次启动测试 | WPF 交叉编译 |
| 1.21.1 + NeoForge 21.1.172 | 真客户端，合成音视频全链路和 MCM 私有片头首次启动测试 | WPF 交叉编译 |

1.21.1 的四种配置的合成媒体测试均在暂停 Java 时观察到播放器完成一轮并继续输出非静音 PCM。恢复 Java 后到达标题菜单，松开空格并重新按下才淡出。播放器停止解码和音频输出后发送 `RELEASE`。

Fabric 通过 `fabric.systemLibraries` 暴露单个桥接 JAR，保留 Knot 的类路径隔离。Forge 和 NeoForge 在 FML 显示早期窗口之前绑定，并验证 Minecraft 接管同一 GLFW 窗口。Linux X11 在首次 GLFW 缓冲区交换前，最多等待 350 毫秒让覆盖层确认已提交首帧。四种配置都验证了缺少确认时可以继续普通启动。窗口失焦会隐藏覆盖层，恢复焦点后重新显示；提示未绘出时按空格不会触发交接。该确认不等于实际显示器无闪现：最近一轮 Xvfb 测试中，覆盖层比游戏窗口首次映射晚约 12–28 毫秒。

[Linux 覆盖层测试](<../PCL.GameIntro.Linux/test_x11_focus.py>) 检查覆盖确认、保留游戏焦点、长按空格、淡出和错误 IPC 停止顺序。[Wayland 全屏测试](<../PCL.GameIntro.Linux/test_wayland_fullscreen.py>) 在关闭 XWayland 的 Hyprland 上用合成窗口和影片验证全屏覆盖、焦点、空格、淡出及 IPC 停止。同一环境也用原版 1.21.11（系统 Wayland GLFW）和发布用 MP4 实测全屏覆盖及焦点。OpenAL 增益恢复的实测使用空输出设备。Windows 本机播放及实际声卡交接仍待验证。类指纹基于上表的干净客户端，整合包和其他加载器版本需要分别验证。
