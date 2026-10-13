# iStoreOS 插件安装助手 V1.0

## 这是什么

Windows 下用于辅助安装本地 Are-u-ok `.run` 插件到 iStoreOS 的开源工具，项目自有源码采用 MIT License。

## 主要功能

- 自动识别设备 SDK、架构及 APK / OPKG，检查安装包兼容性。
- 检查网络、时间、空间和包管理器状态，探测 QuickStart 动态软件源并有限重试。
- SFTP 上传，SHA256 / Makeself 校验，执行作者安装包并独立验证结果。
- Package Diff、原官方源恢复、临时目录清理，以及独立高级信息 / 日志窗口。

## 使用方法

下载 `iStoreOS.PluginHelper-v1.0.0-win-x64.zip`，解压运行 `iStoreOS.PluginHelper.exe`。

**连接路由器 → 选择 / 拖入匹配的 `.run` → 点击“安装插件” → 等待结果。**

请从插件作者官方发布渠道获取安装包。安装框架通用，不局限于已实测插件。

## 已实测

- PassWall 26.9.16
- OpenWrt-momo 1.2.1

两者均通过同一套通用安装链路完成真实安装；正式打包阶段未重复安装。

## 系统要求

推荐 Windows 10 / Windows 11 x64，运行环境为 .NET Framework 4.8。Windows 7 SP1 x64 + .NET Framework 4.8 属兼容目标，尚未完成专项实机验收。

## 注意

- 插件安装脚本来自插件作者，工具不自动启用代理插件或修改代理配置。
- SSH 密码不保存到磁盘，关闭程序后释放会话引用。
- 软件未代码签名，Windows 可能显示 SmartScreen 提示。
- 保留解压目录中的 DLL、config 和许可文件。校验文件为 `SHA256SUMS.txt`。
- 如遇异常，打开“高级信息 → 完整日志”，保存日志用于排查。
