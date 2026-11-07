# iStoreOS 插件安装助手 V1.1.1

发布日期：2026-11-07（Asia/Taipei）。V1.1.1 是兼容性修复版本。

## 主要修复

- 修复部分精简 OpenWrt / ImmortalWrt 固件没有 SFTP subsystem 时，本地插件无法上传的问题。SFTP 不可用时自动安全使用 SCP，不需要用户额外安装 `openssh-sftp-server` 或修改 SSH 配置。
- 仅在明确确认 SFTP subsystem / channel 不可用时切换；认证失败、Host Key 异常、网络断开、连接超时不会触发 SCP fallback。`.run` / `.ipk` / `.apk` 共用上传链，继续检查远端文件存在、大小和 SHA256。
- 无 SFTP 环境下，安装日志与退出状态通过已有受信任 SSH 增量读取 stdout / stderr / exit.code，保持完整安装验证、包快照、独立结果验证和清理。未知状态不会被当作成功。
- 修复部分 Windows / .NET Framework 环境连接 SSH 时出现 `MAC error` 的问题。基于 SSH.NET 2026.0.0，仅合入官方已合并的 [Issue #1829](https://github.com/sshnet/SSH.NET/issues/1829) / [PR #1830](https://github.com/sshnet/SSH.NET/pull/1830) 修复；patched DLL 可重复构建。
- 增强 SSH 错误分类及 Server / KEX / HostKey / Cipher / MAC / Compression 协商日志，便于定位不同设备的连接兼容问题。未协商字段不会被猜测，不记录密码、私钥或会话密钥。
- 未为了兼容性加入 ssh-dss、MD5 MAC、截短 MAC 等弱算法；不改变现有 SSH 算法优先级，保持既有 Host Key 信任规则。

patched SSH.NET DLL SHA256：

```text
31c62881cf8375b47118ab11a620c85d8208ae86b93f418f58ff2c79605ad481
```

## 保留的功能

- 本地 `.run`、`.ipk`、`.apk` 单文件选择与拖入；识别 OPKG / APK，检查包信息与设备兼容性。
- 软件源检测、安装前后包快照、安装结果验证、日志保存及断线后的原任务监测。
- 原始 `menu.sh` 插件维护终端、键盘输入、复制粘贴、UTF-8 和 PTY 尺寸同步。
- 设备 GitHub Raw 直连优先；电脑代理通过独立 SSH #2 临时隧道运行，结束后清理，保留首页 SSH #1。

## 验证范围与使用

RC1 已完成真实用户测试，用户确认此前主要问题基本解决。本地 `.run` / IPK、插件维护终端及电脑代理沿用已完成的 N1 实机验收记录；本地 APK 已完成离线测试和真实样本解析，尚未实机安装验收。没有将用户总体反馈扩大为所有设备、固件和故障场景均已验证。

无 SFTP fallback 适用于本地安装链；插件维护菜单准备仍使用现有 SFTP 上传。仅允许当前库不支持的算法的定制 SSH 服务器仍可能无法连接。Windows 7 SP1 x64 是兼容目标，尚未完成专项实机验收。

内部版本 `1.1.1`，程序集 / 文件版本 `1.1.1.0`。下载 ZIP 和 `SHA256SUMS.txt` 后校验，完整解压到新目录运行 `iStoreOS.PluginHelper.exe`，保留全部 14 个运行时 DLL 与许可证，避免混用旧依赖。推荐 Windows 10 / 11 x64 + .NET Framework 4.8。

正式版本完整离线回归：**1431 passed / 0 failed**；Release x64：**0 warnings / 0 errors**。测试保留 `.run` / IPK / APK、SFTP、SCP fallback、SSH monitor、Host Key、PTY 菜单与电脑代理等覆盖。

正式文件：`iStoreOS.PluginHelper-v1.1.1-win-x64.zip`、`SHA256SUMS.txt`。

源码中的构建、完整回归、ZIP 内容与解压启动检查记录见 [V1.1.1 最终验证报告](https://github.com/andyleon27/iStoreOS-Plugin-Helper/blob/v1.1.1/docs/V1.1.1-release-final-%E9%AA%8C%E8%AF%81%E6%8A%A5%E5%91%8A.md)。第三方固定来源和可复现构建文件随源码提供，ZIP 内保留许可证及来源说明。
