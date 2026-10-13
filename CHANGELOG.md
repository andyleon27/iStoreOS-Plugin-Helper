# CHANGELOG

## 1.0.0

发布日期：2026-10-13。

首个正式公开版本，项目自有源码采用 MIT License。产品与已验收 RC1 使用同一套稳定安装链路，未新增功能。

- Are-u-ok `.run` 通用安装流程与 iStoreOS 环境识别。
- SDK / Architecture 兼容检查，APK / OPKG 自动识别。
- QuickStart 动态软件源检测与软件源有限重试。
- SSH 断线恢复、SFTP 上传、SHA256 / Makeself 校验。
- Package Diff、独立安装验证、原软件源恢复与临时目录清理。
- 极简 UI 与独立高级诊断窗口。
- 正式 V1.0 版本、MIT 许可、便携 ZIP 与发布文档。

Real-world validation：PassWall 26.9.16、OpenWrt-momo 1.2.1。

## 1.0.0-rc1 — 2026-10-13

首个发布候选版本，冻结现有界面和安装流程，准备绿色便携发布包。

- 通用 Are-u-ok `.run` 安装流程与 iStoreOS 环境检测。
- SDK / Architecture 兼容性判断，网络、时间、空间及包管理器预检。
- QuickStart 动态软件源识别、实际 HTTP / HTTPS 探测与 package update 验证。
- 有限镜像自动重试与 SSH 断线恢复，重新核对受信任设备身份。
- SFTP 上传、SHA256 / Makeself 校验及作者原始安装器执行。
- 独立 Package 验证、Package Diff、临时文件清理和原官方源恢复。
- 极简主窗口与独立高级诊断、日志保存 / 复制窗口。
- 保留完整日志圆角修正及共用圆角按钮背景绘制 BUG 修复。
- 修正直接 Dispose 时会话凭据引用未释放的问题，保留具体连接失败与 Host Key 拒绝提示。
- 统一版本、完成发布审计并整理用户文档、依赖许可和 SHA256。

Real-world validation（用户报告，V0.3.2.6 稳定基线）：

- PassWall 26.9.16：真实安装通过。
- OpenWrt-momo 1.2.1：exit=0，`luci-app-momo 1.2.1-r1` 独立验证通过，Package Diff 与清理正常，OpenClash 保持正常。

本轮 RC 不重复执行真实安装，不增加插件商店、下载、自动更新、代理控制或卸载 / 回滚功能。

