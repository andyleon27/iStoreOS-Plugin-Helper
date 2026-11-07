# Third-party notices — V1.1.1

本表根据 `src/iStoreOS.PluginHelper/packages.config`、`build/Common.props` 的真实运行时引用和已还原 NuGet 包内的 `.nuspec` / 许可证文件整理。本地 IPK / APK 支持新增 ZstdSharp.Port 0.8.8，原有依赖未升级或移除。包版本与程序集版本可能不同，现有 `App.config` 保留必要 binding redirects。

| Package / runtime DLL | Version | License | Purpose |
| --- | --- | --- | --- |
| ZstdSharp.Port / ZstdSharp.dll | 0.8.8 | MIT | 受限流式读取 APK v3 zstd 元数据；不执行包内容 |
| SSH.NET / Renci.SshNet.dll | 2026.0.0 + official PR #1830 | MIT | 统一 SSH、SFTP、SCP、PTY、监测及代理底层；.NET Framework 接收 MAC 状态修复 |
| BouncyCastle.Cryptography | 2.7.0 | MIT | SSH.NET 加密依赖 |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT | .NET Framework 异步接口兼容 |
| Microsoft.Bcl.Cryptography | 10.0.10 | MIT | 依赖使用的密码学兼容 API |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | 日志依赖使用的服务抽象 |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | SSH.NET 日志接口抽象 |
| System.Buffers | 4.6.1 | MIT | 缓冲区与内存池 API |
| System.Formats.Asn1 | 10.0.10 | MIT | 加密依赖使用的 ASN.1 编解码 |
| System.Memory | 4.6.3 | MIT | Span / Memory 等内存 API 兼容 |
| System.Numerics.Vectors | 4.6.1 | MIT | 向量 API 兼容依赖 |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT | 底层编译器 / 内存操作兼容依赖 |
| System.Threading.Tasks.Extensions | 4.5.4 | MIT | ValueTask 等异步 API 兼容 |
| System.ValueTuple | 4.6.2 | MIT | 元组 API 兼容依赖 |

其中 12 个包在实际 `.nuspec` 中声明 MIT；`System.Threading.Tasks.Extensions` 的包内 `LICENSE.TXT` 为 MIT。构建专用的 `Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3` 不进入用户 ZIP，其元数据未提供 SPDX 表达式，指向的 [Microsoft 官方许可证](https://github.com/Microsoft/dotnet/blob/main/LICENSE)已核对为 MIT。NuGet.exe 6.14.0 仅用于开发还原，也不进入用户 ZIP。

发布包包含 14 个运行时 DLL 及所需 config。`licenses/` 保留包内原始 LICENSE / THIRD-PARTY-NOTICES 文件；未附正文的包保留 `third-party/SSH.NET-LICENSE.txt`、`third-party/DOTNET-LICENSE.txt` 和 `third-party/ZstdSharp-LICENSE.txt`。ZstdSharp 许可证按 NuGet 固定修订 `2cd0c019693bc786a5fe5c3be94e107b24e7267e` 保存，MIT / Copyright (c) 2021 Oleg Stepanischev。MIT 文本要求保留版权和许可通知，所以这些文件随 DLL 分发。本表只做依赖摘要，不替代原始许可文本。

项目自有源码采用根目录 LICENSE 中的 MIT License。第三方组件仍按本表及各自 licenses/ 文件中的许可证分发；项目 LICENSE 不替代第三方许可。

## V1.1.1 固定 SSH.NET 补丁依赖

当前构建使用 `third-party/sshnet-2026.0.0-pr1830/lib/net462/Renci.SshNet.dll`，而非未经修复的 NuGet DLL。基线为官方 `2026.0.0` / `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e`，仅应用官方 [#1830](https://github.com/sshnet/SSH.NET/pull/1830) / `f099365c9d4cf2ade92b92c203bbb2b345d2cd74`，对应 [#1829](https://github.com/sshnet/SSH.NET/issues/1829)。改动为 `Session.ReceiveMessage` 的 ETM、非 ETM 两个分支在成功验证 MAC 后，对 .NET Framework 调用 Initialize；不修改算法实现、集合或优先级。

DLL AssemblyVersion / FileVersion 保持 `2026.0.0.1`，informational version 为 `2026.0.0.1+7b2fd3dbf2.pr1830.f099365c9d`，SHA256 为 `31c62881cf8375b47118ab11a620c85d8208ae86b93f418f58ff2c79605ad481`。使用固定 .NET SDK 10.0.201、net462、原上游强名称身份和锁定 NuGet 构建；运行时依赖版本未升级。可复现构建说明及许可文件见[补丁依赖目录](third-party/sshnet-2026.0.0-pr1830/README.md)。NuGet 2026.0.0 包保留为基线与测试负对照，不作为产品运行时 SSH.NET DLL。

`scripts/prepare-portable.ps1` 在正式打包时保留该目录的 LICENSE、THIRD-PARTY-NOTICES、来源清单和重建说明。V1.1.0 原始发布产物保持不变。

## 插件维护（自 V1.1.0 提供）

来源为 [slobys/openclash-auto-installer](https://github.com/slobys/openclash-auto-installer)，MIT License / Copyright (c) 2026 slobys。原始完整许可证保留在 [third-party/openclash-auto-installer-LICENSE.txt](third-party/openclash-auto-installer-LICENSE.txt)，并作为 EXE 内嵌资源。

当前产品通过原项目 menu.sh 提供终端维护入口。Windows 直接下载 upstream main 的原始 menu.sh 和 LICENSE，验证内容及两端 SHA256，连同 NOTICE 上传到本次私有任务目录后通过独立 PTY 执行；菜单及其子脚本的版本解析和插件操作由原始上游脚本处理。不 fork、改写或打包这些第三方脚本进入产品。

下载的实际 LICENSE 必须符合已知许可信息，不静默以内嵌副本替代上游变更。NOTICE 如实记录来源、下载 URL、Ref: main 及未解析 commit 的事实；明确 SHA 的菜单校验能力仍保留。清理受任务所有权和运行状态约束，未知状态不提前删除任务文件。

旧七插件页面及其独立脚本映射、版本扫描、卸载确认与软件源子窗口已删除，不再编译进产品。先前引用过的 9a576f1e67512c1f6c6d932191ba3c6a7fd27661 等修订及旧流程仅属于保留的历史开发报告，不是当前入口。上游脚本自行获取的其他内容仍受各自许可和行为约束。此次清理只进行离线测试，未连接设备、未执行真实插件操作、未覆盖此前发布附件。
