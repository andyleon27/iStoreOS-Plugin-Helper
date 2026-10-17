# iStoreOS 插件安装助手

Windows 下用于辅助安装本地 Are-u-ok `.run` 插件到 iStoreOS 的开源工具。

当前公开版本：**V1.0.2（1.0.2）**。项目自有源码采用 **MIT License**。

本版完善精简 OpenWrt / BusyBox 环境下的 ARM64 / OPKG 安装兼容性：保留真实 package update Gate，动态选择可靠镜像，复用受信任 SFTP 监测连接，并在临时换源安装后恢复原始官方 distfeeds。原有 x86_64 / APK 安装链保持。

核心流程：**连接路由器 → 拖入插件 → 点击安装**。

## 功能

- 自动识别 iStoreOS 版本、SDK、架构及 APK / OPKG，判断安装包是否兼容。
- 检查网络、DNS、系统时间、存储空间及软件包管理器是否空闲。
- 从设备当前 QuickStart 获取软件源候选，实际探测 HTTP / HTTPS 并验证 package update。
- SFTP 上传，检查本地 / 远程 SHA256 和 Makeself 完整性，执行插件作者原始安装包。
- 对明确的官方源下载错误进行有限重试，SSH 中断后核对原设备身份并尝试恢复。
- 独立验证主 package，记录 Package Diff，清理本轮临时文件并尝试恢复临时修改的官方源。
- 极简主窗口，以及独立的高级信息、完整日志保存与复制窗口。

本版已实测：N1 / iStoreOS 24.10.8 / SDK 24.10 / aarch64_cortex-a53 / OPKG 上的 **PassWall 26.10.16、OpenClash 0.47.156、daede 1.15-r6**，以及 iStoreOS 25.12.5 / SDK 25.12 / x86_64 / APK 上的 **daede 1.14.7-r23**，均为 SuccessVerified。此前还真实验证过 PassWall 26.9.16 和 OpenWrt-momo 1.2.1。工具定位为通用 iStoreOS / OpenWrt 兼容辅助工具，实测列表不是设备白名单，也不代表所有第三方插件或固件均已实测。

## 下载

从 [V1.0.2 GitHub Release](https://github.com/andyleon27/iStoreOS-Plugin-Helper/releases/tag/v1.0.2) 下载 `iStoreOS.PluginHelper-v1.0.2-win-x64.zip`，解压后直接运行 EXE。无需 clone 源码或自行编译。发布附件中的 `SHA256SUMS.txt` 可用于核对下载文件。

架构匹配保留原有语义规则：`aarch64_a53` / `aarch64_a72` 分别归一到 Cortex-A53 / A72，bare `aarch64` 可以识别 ARM64 设备家族，具体 A72 包不能因此放行到 A53。`aarch64_generic` 保持安全匹配规则，SDK 仍严格匹配；工具保留设备和插件原始架构显示。

验证：**611 passed / 0 failed**，离线 Runner Smoke **12 / 12**，Release x64 **0 warning / 0 error**。详见 [正式验证报告](docs/V1.0.2-验证报告.md) 与 [Release Notes](docs/release-notes-v1.0.2.md)。本次 N1 和 x86 真实安装均只使用一条 SFTP 监测连接；健康当前源不主动切换，临时换源后恢复原始 distfeeds。完整快照用于 Package Diff 和独立验证，日志只输出数量、真实变化项和阶段 Timing Summary。

## 使用方法

1. 从 GitHub Releases 下载本工具 ZIP，解压并打开 `iStoreOS.PluginHelper.exe`。
2. 从插件作者官方发布渠道下载与设备 SDK 和架构匹配的 `.run`。
3. 输入路由器地址、用户名和密码，点击“连接”。默认地址为 `192.168.100.1`，可自行修改。
4. 将 `.run` 拖入窗口，或点击“选择文件”。确认显示“当前设备兼容”。
5. 点击“安装插件”。这个操作会执行所选作者安装包，请先确认文件来源。
6. 等待安装完成；安装器开始后不要关闭工具或断开路由器电源。完成后自行在 LuCI 查看并配置插件。

无需安装本工具。保留解压目录中的 EXE、DLL、config 和许可证文件，可整体移动。工具运行不需要 Visual Studio、Python、Node.js、PowerShell、外部 SSH / SCP 或现代 .NET Runtime。

## 支持范围

- 推荐：Windows 10 / Windows 11 x64，运行环境为 .NET Framework 4.8。
- 兼容目标：Windows 7 SP1 x64 + .NET Framework 4.8，尚未完成专项实机验收。
- 路由器侧：iStoreOS 与适配设备的 Are-u-ok `.run`。工具自动识别 APK / OPKG、SDK 和架构。

不承诺兼容所有 OpenWrt 分支、第三方 `.run` 或自定义固件。兼容提示只检查文件名中的 SDK / 架构；实际上传后还会校验文件内容。

当前版本是未代码签名的绿色软件，Windows 可能显示 SmartScreen 提示。

## 安全说明

- SSH 密码默认隐藏，可临时显示；不保存到磁盘，也不写入日志。当前会话会在内存保留认证信息供自动重连，关闭窗口后释放引用。
- 首次连接在本次运行内信任 SSH 指纹（TOFU），指纹显示在高级日志。重连和 SFTP 必须匹配原受信任指纹；变化时拒绝连接。不会修改系统 `known_hosts`。
- 仅在需要时临时切换官方源，并保存原配置完整内容；流程结束后尝试恢复。恢复失败会明确提示并保留备份信息。辅助 / 自定义源不由本工具编辑。
- 不自动启用 PassWall、PassWall2、OpenClash 或 Momo，不修改代理配置或模式。
- 不自动卸载已有 package，也不自动回滚依赖。Package Diff 只记录真实变化，正常依赖版本变化不等于安装失败。
- 最终安装行为由插件作者提供的 `.run` 执行，安装器可能修改依赖或默认设置。请使用可信来源并按需要预先备份。

高级日志可包含路由器地址、源 URL、package、SHA256、目录和返回码。分享日志前可检查这些诊断信息。

## 常见问题

**无法连接路由器**：检查地址、网络、SSH 是否开启及用户名 / 密码。指纹变化时先核对设备，不要忽略身份提示。

**插件提示不兼容**：重新下载对应 SDK 和架构的安装包；只有文件名信息不足时也可能显示无法判断。

**所有软件源暂时不可用**：检查路由器网络和时间，稍后再次尝试。工具不会无限重试或替你修改 DNS。

**安装包校验失败**：停止使用当前文件，从作者官方渠道重新下载。若设备缺少校验能力，按提示保存日志排查。

**空间不足或 package manager 正忙**：清理空间或等待其他软件包任务结束后重试。

**安装完成后找不到插件**：刷新 LuCI，查看“高级信息 → 安装过程”的独立验证结果；本工具不会自动启用插件。若提示验证不完整，保存日志进一步确认。

**如何查看日志**：点击底部“高级信息 ›”，切到“完整日志”，使用“保存日志”或“复制日志”。窗口关闭期间仍保留本次会话记录。

**安装状态未知或原源恢复失败**：先保存日志。再次尝试会先核对旧安装流程，避免重复执行尚未结束的安装器；不要主动删除未确认结束的远程文件。

## 免责声明与许可

这是第三方辅助工具，插件内容、依赖和安装脚本由插件作者负责。遇到异常请保存高级日志用于排查。

项目自有源码采用 [MIT License](LICENSE)。第三方依赖按照各自许可证分发，见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

发布说明见 [V1.0.2 Release Notes](docs/release-notes-v1.0.2.md)，变更记录见 [CHANGELOG.md](CHANGELOG.md)，验证结论见 [V1.0.2 验证报告](docs/V1.0.2-验证报告.md)。历史版本与测试阶段记录继续保留在 docs 中。
