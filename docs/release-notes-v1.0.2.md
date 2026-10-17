# iStoreOS 插件安装助手 V1.0.2

V1.0.2 主要完善 ARM64 / OPKG 环境下的真实安装兼容性，并优化坏软件源场景下的安装体验，同时保持原有 x86_64 / APK 安装链。

## 主要变化

- 修复精简 OpenWrt / BusyBox 环境因缺少 `stat`、`nohup` 等工具导致安装前失败的问题。
- 安装器日志与结束状态通过受信任 SFTP 直接读取，不再依赖 Runner / Poll 中的 `base64` / `dd`。
- 安装期间复用单条 SFTP 监测连接，断线时可重新连接同一 session，不重复执行 `.run`。
- 软件源使用 QuickStart 动态候选，不维护自定义镜像数据库。
- 当前源异常时使用 Canary、延迟筛选和 FULL 验证选择可靠镜像；健康当前源保持不变。
- 保留真实 `apk update` / `opkg update` 作为安装前最终 Gate。
- 临时换源安装完成后恢复安装前原始官方 distfeeds。
- Package Snapshot 保留完整数据用于验证，日志只输出数量、真实变化项及阶段 Timing Summary。
- 保留 SHA256、Makeself、Package Diff、独立主 package 验证及临时目录安全策略。

## 实机验证

ARM64 / OPKG：N1，iStoreOS 24.10.8，SDK 24.10，`aarch64_cortex-a53`。

- PassWall 26.10.16：SuccessVerified，Timing Total 64418 ms。
- OpenClash 0.47.156：SuccessVerified，Timing Total 52139 ms。
- daede 1.15-r6：SuccessVerified，Timing Total 61968 ms。

以上安装的监测 SFTP connection count 均为 1。不可用的 Tencent Cloud 当前源被识别后，工具从动态候选选择 CERNET，通过真实 opkg update，安装后成功恢复原始 Tencent Cloud distfeeds。

x86_64 / APK：iStoreOS 25.12.5，SDK 25.12，`x86_64`。

- daede 1.14.7-r23：SuccessVerified，Timing Total 22669 ms。
- 监测 SFTP connection count 为 1；当前 CERNET 健康，没有切源，source modification=False、source restore=False。

本次通过通用 OpenWrt / BusyBox / 架构语义适配改善 ARM64 / OPKG 环境兼容性，并在 N1 aarch64_cortex-a53 上完成真实安装验证；不保证所有 ARM 设备或固件兼容。

## 验证

- 611 tests passed / 0 failed。
- 离线 Runner Smoke 12 / 12。
- Release x64：0 warning / 0 error。
- 正式化阶段仅调整版本显示、发布文档和打包元数据，安装行为与已实机验收的测试基线一致，没有再次执行真实 `.run`。

性能数据仅代表本次测试环境，不作为其他设备或网络环境的性能保证。

## 下载

- `iStoreOS.PluginHelper-v1.0.2-win-x64.zip`
- `SHA256SUMS.txt`

解压后运行 EXE，并保留 DLL、config 和许可证文件。运行环境为 Windows x64 + .NET Framework 4.8；Windows 7 SP1 x64 为兼容目标，尚未完成专项实机验收。
