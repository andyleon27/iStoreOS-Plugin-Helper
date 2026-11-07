# iStoreOS 插件安装助手 V1.1.1

Windows 下用于辅助安装本地 `.run`、`.ipk`、`.apk` 软件包，并通过原项目菜单终端维护 iStoreOS 插件的开源工具。

当前版本：**V1.1.1（1.1.1）**，AssemblyVersion / FileVersion 为 **1.1.1.0**。V1.1.1 是兼容性修复版本，保留 V1.1.0 的本地安装、插件维护终端和电脑代理功能。项目自有源码采用 **MIT License**。

## V1.1.1 主要修复

- 部分精简 OpenWrt / ImmortalWrt 固件没有 SFTP subsystem 时，本地 `.run` / `.ipk` / `.apk` 共用安全 SCP fallback，不需要额外安装 `openssh-sftp-server`。SFTP 可用时继续使用原路径；认证、Host Key、网络或超时失败不会触发 fallback。
- 已明确确认设备无 SFTP 时，安装监测通过已有受信任 SSH 增量读取 stdout / stderr / exit.code；远端大小、SHA256、包快照、独立结果验证和清理继续执行。未知退出状态不会判为成功。
- 基于 SSH.NET 2026.0.0 合入官方 [Issue #1829](https://github.com/sshnet/SSH.NET/issues/1829) / [PR #1830](https://github.com/sshnet/SSH.NET/pull/1830) 的已合并修复，解决部分 Windows / .NET Framework 环境的 `MAC error`。patched DLL 可重复构建。
- 增强 SSH 错误分类和 Server / KEX / HostKey / Cipher / MAC / Compression 协商日志；未加入 ssh-dss、MD5 MAC、截短 MAC 等弱算法，也未改变现有算法优先级。

## 验收范围

| 能力 | 当前结论 |
| --- | --- |
| 本地 `.run` 安装 | **已完成 N1 实机验收** |
| 本地 `.ipk` 安装 | **已完成 N1 实机验收** |
| 本地 `.apk` 安装 | **已完成离线测试和真实样本解析，尚未实机安装验收** |
| 插件维护终端 | **已完成 N1 实机验收** |
| 电脑代理模式 | **已完成 N1 实机验收** |

上表实机结论沿用 V1.1.0 的用户确认。V1.1.1 RC1 已完成真实用户测试，用户确认此前主要问题基本解决；没有据此扩展未提供的逐设备、包格式或故障场景验收结论。本次正式版本只更新必要版本信息并重新构建、离线回归和打包，未重新操作设备。历史 APK 设备运行 `.run` 的成功记录，不等同于本地 `.apk` 已通过实机安装验收。

## 本地安装

首页支持单文件选择和拖入，自动识别设备 OPKG / APK：

- `.run`：保留原有文件名 SDK / 架构规则、Makeself 内容和完整性校验、作者原始安装器执行链。
- `.ipk`：读取包内名称、完整版本、架构和安装大小；仅允许 OPKG 设备，使用 `opkg install`。
- `.apk`：指 apk-tools 软件包，非 Android APK；读取包内元数据，仅允许 APK 设备，使用 `apk add`。

原生包严格匹配设备架构；IPK `all` 和 APK `noarch` 分别作为无关架构，不套用 `.run` 专用 ARM64 映射。支持 gzip/tar、ar IPK，APK v2 gzip 与 APK v3 ADB（未压缩、deflate、zstd）；解析有大小、路径和解码窗口边界。

安装流程复用网络 / DNS / 时间 / 空间与忙状态检查、QuickStart 软件源检测、真实 package update、受信任 SFTP（明确无 subsystem 时使用 SCP）、本地 / 远端 SHA256、前后包快照和独立结果验证。三种本地包均须通过远端存在性、大小和 SHA256 检查；原生包的目标包名、完整版本和独立存在性查询均通过才确认成功。依赖、固件 ABI、签名和额外空间继续由设备包管理器校验，不自动添加 `--force-*` 或 `--allow-untrusted`。

当前源异常时才选择可用的官方镜像，成功后保留选中的可用源，失败沿用恢复流程。安装器派发后不取消 / 重复派发；未知状态保留任务目录和安装互斥，重连后优先监测原任务。共用日志统一使用“安装包 / 安装器”，专属校验及真实命令仍准确显示。

## 插件维护终端和电脑代理

首页只有新的“插件维护”终端入口，旧七插件按钮页及其专属扫描 / 动作链已删除。终端支持键盘输入、回车、Ctrl+C、复制粘贴、滚动、UTF-8、最终输出排空及窗口 / PTY 尺寸同步。

SSH 未连接时入口灰色。连接后后台优先检测设备 GitHub Raw 直连；直连不可用时检测 Windows localhost HTTP 代理。直连可用则启用普通入口，代理可用则标记“电脑代理”，两者均不可用时保持禁用。点击时再次复核；检测、安装和维护之间互斥，GitHub API 仅作诊断。

菜单准备直接下载 upstream main 的原始 `menu.sh`、`LICENSE`，保留内容、两端 SHA256、SFTP、私有任务目录和原始许可信息；菜单自己的版本解析及插件操作交给原脚本。main 可变化，日志如实记录未解析 commit，SHA256 用于本次字节传输核对。

直连路径使用设备网络。电脑代理路径使用独立 SSH #2：`N1 127.0.0.1:17890 → Windows localhost HTTP 代理`；Windows 菜单下载绑定所选代理，临时代理环境仅作用于本次菜单及子进程。退出 / 关闭后等待 SSH #2 清理并确认端口释放，首页 SSH #1 保留；不修改永久代理、系统配置或软件源来实现隧道。

首页 SSH 按钮在“连接 / 断开”间切换，安装或维护运行期间禁止主动断开。断开清除当前设备、认证引用与维护可用状态。

## 发布文件与使用

正式下载：[GitHub Release V1.1.1](https://github.com/andyleon27/iStoreOS-Plugin-Helper/releases/tag/v1.1.1)。本地发布目录：`artifacts/release/v1.1.1/`，包含：

- `iStoreOS.PluginHelper-v1.1.1-win-x64.zip`
- `SHA256SUMS.txt`

ZIP 校验值以同目录 SHA256SUMS.txt 为准。升级时完整解压到新目录，避免旧版 DLL 混入。

1. 校验 ZIP 后解压到独立目录，运行 `iStoreOS.PluginHelper.exe`。
2. 保留 EXE、config、14 个运行时 DLL 和许可证文件。无需安装 Visual Studio、Python、Node.js、PowerShell 或额外 SSH / SCP 客户端。
3. 输入路由器地址、用户名和密码，点击“连接”，核对检测到的设备身份及环境。
4. 从可信作者渠道获取与设备匹配的安装包，选择 / 拖入后检查格式、版本、架构和兼容状态，点击“安装插件”。
5. 安装开始后不要关闭工具或断开设备电源。完成后自行在 LuCI 查看和配置插件；工具不自动启用插件。
6. 使用“插件维护”进入原项目菜单，按菜单提示操作；出现失败或未知状态时保存日志，先检查原任务，不重复安装。

本地未签名 / 未受信任签名的 APK 可能被设备拒绝；当前本地 APK 尚未实机安装验收。`.run` 和软件包安装脚本可能修改系统与依赖，应提前备份。

## 验证、运行环境和诊断

V1.1.1 最终验证：**1431 passed / 0 failed**；Release x64 **0 warnings / 0 errors**。覆盖首次安装、IPK/APK、软件源、SFTP/SCP、无 SFTP SSH monitor、MAC、Host Key、电脑代理、终端菜单、退出 / 断线和清理边界。正式构建及完整回归结果见 [V1.1.1 最终验证报告](docs/V1.1.1-release-final-验证报告.md)。实机覆盖范围以上述用户确认为准，离线测试不扩展实机结论。

推荐 Windows 10 / 11 x64 + .NET Framework 4.8。Windows 7 SP1 x64 是兼容目标，尚未完成专项实机验收。程序未代码签名，可能显示 SmartScreen 提示。

- 密码默认隐藏，不写入磁盘和日志；当前会话在内存保留认证供 SSH / SFTP / SCP 使用。
- 首次连接在本次运行中信任 SSH 指纹；后续连接、SFTP、SCP 与代理 SSH #2 必须通过现有 Host Key 信任检查。
- 首页 SSH 已断时不能保证确认远端端口释放；未知菜单 / 安装状态保留任务。关闭终端或隧道不证明所有远端派生操作已停止。
- 高级信息提供完整日志、Package Diff、结果与复制 / 保存功能。分享前检查其中设备地址、源 URL 和路径等诊断信息。

发布说明：源码工作区见 [V1.1.1 Release Notes](docs/release-notes-v1.1.1.md)，便携包见 `RELEASE-NOTES.md`；最终检查见源码中的 `docs/V1.1.1-release-final-验证报告.md`。历史开发报告保留在 docs 中，其旧版本号或待执行步骤不代表当前验收状态。

项目自有源码采用 [MIT License](LICENSE)。第三方依赖见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与随包 licenses/；上游菜单及其下载内容按各自许可证分发。

## SSH 依赖与源码构建

所有正式 SSH、SFTP、SCP、PTY 和电脑代理连接引用同一固定 SSH.NET 2026.0.0 + 官方 #1830 patched DLL，SHA256 为 `31c62881cf8375b47118ab11a620c85d8208ae86b93f418f58ff2c79605ad481`。完整算法支持及兼容性边界见[审计矩阵](docs/V1.1.1-SSH-algorithm-matrix.md)。

普通源码构建仍先运行 `scripts/restore.ps1`，再运行 `scripts/build.cmd`；构建会校验仓库内 patched DLL 的 SHA256 与强名称身份。重建该第三方 DLL 的 SDK、源码 commit、官方补丁、锁定依赖及命令见[SSH.NET 可复现构建说明](third-party/sshnet-2026.0.0-pr1830/README.md)。开发用便携 SDK 放在 `.tools`，用户运行助手仍只需要 .NET Framework 4.8。

V1.1.1 的改动不覆盖插件维护菜单准备中的 SFTP 上传：本次无 SFTP 兼容范围为本地安装链。仅允许库不支持的算法的定制服务器仍可能无法连接；未知错误保留真实诊断，不猜测算法问题或自动接受 Host Key 变化。
