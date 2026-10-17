# Third-party notices — 1.0.1

本表根据 `src/iStoreOS.PluginHelper/packages.config`、`build/Common.props` 的真实运行时引用和已还原 NuGet 包内的 `.nuspec` / 许可证文件整理。没有升级或移除依赖。包版本与程序集版本可能不同，现有 `App.config` 保留必要 binding redirects。

| Package / runtime DLL | Version | License | Purpose |
| --- | --- | --- | --- |
| SSH.NET / Renci.SshNet.dll | 2026.0.0 | MIT | SSH 连接、命令执行及 SFTP 上传 |
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

发布包包含 13 个运行时 DLL 及所需 config。`licenses/` 保留包内原始 LICENSE / THIRD-PARTY-NOTICES 文件；未附正文的包保留现有 `third-party/SSH.NET-LICENSE.txt` 和 `third-party/DOTNET-LICENSE.txt`。MIT 文本要求保留版权和许可通知，所以这些文件随 DLL 分发。本表只做依赖摘要，不替代原始许可文本。

项目自有源码采用根目录 LICENSE 中的 MIT License。第三方组件仍按本表及各自 licenses/ 文件中的许可证分发；项目 LICENSE 不替代第三方许可。
