# iStoreOS 插件安装助手 V1.0.1

V1.0.1 修复 ARM64 架构兼容性误判：插件文件名使用 `aarch64` / `aarch64_a53`，设备返回 `aarch64_cortex-a53` 时，现在能正确判断兼容。这是兼容性修复，不增加安装功能，也不针对设备型号或路由器 IP 建立特例。

- 支持 `aarch64_a53` ↔ `aarch64_cortex-a53`、`aarch64_a72` ↔ `aarch64_cortex-a72` 别名语义匹配，保留原始架构用于显示和日志。
- bare `aarch64` 可识别明确属于 ARM64 的设备家族。
- A53 / A72 继续严格区分，A72 插件不会放行到 A53。
- `aarch64_generic` 插件保持安全规则，只匹配 generic 设备。
- SDK 继续严格匹配，安装核心未修改。

按“插件 → 设备”判断：

| 插件标记 | 允许的设备架构 |
| --- | --- |
| x86_64 | x86_64 |
| aarch64 | aarch64，或以 aarch64_ 开头且后缀非空的设备架构 |
| aarch64_a53 / aarch64_cortex-a53 | Cortex-A53；另保留作者已知 A53 → aarch64_generic 规则 |
| aarch64_a72 / aarch64_cortex-a72 | Cortex-A72 |
| aarch64_generic | aarch64_generic |

A53 → generic 特例只属于 **Are-u-ok ARM64 compatibility rule**，不代表所有 OpenWrt 软件包具有同样 ABI 兼容性。A72 不放行到 A53 / generic，generic 包不放行到 Cortex-A53 / A72。原始架构仍用于显示和日志，SDK 必须一致。

验证结果：**386 passed / 0 failed**，原有 264 项测试全部保留，新增 122 项。Release x64：**0 warning / 0 error**。

用户已在 N1（iStoreOS 24.10.8 / SDK 24.10 / aarch64_cortex-a53 / OPKG）实机确认：OpenClash、daede、PassWall、SSR-Plus 四个指定文件均为 Compatible；A72 文件名反例为 Incompatible。本次仅验收兼容性，未执行真实 `.run`。

安装核心、SSH、软件源和 UI 样式保持，没有解析或解包安装器内部 APK / IPK。

附件为 `iStoreOS.PluginHelper-v1.0.1-win-x64.zip` 与 `SHA256SUMS.txt`。正式 ZIP 直接复制已实机验收的同一个测试 ZIP，没有重新构建、测试或打包，复制前后 SHA256 相同：

```text
bce9c60cfc3c2edb306af1edd944a6ad3ab4209ee0c372e03f59c4baf029f3e4
```

下载 ZIP 后解压运行 `iStoreOS.PluginHelper.exe`；UI 显示 V1.0.1。AssemblyVersion 为 1.0.0.0，FileVersion 为 1.0.1.0，InformationalVersion 为 1.0.1。运行环境仍为 Windows x64 / .NET Framework 4.8，Windows 7 SP1 x64 为兼容目标，尚未完成专项实机验收。
