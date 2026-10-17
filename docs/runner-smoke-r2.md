# 开发专用 r2 Runner Smoke

> 历史测试阶段说明。V1.0.2 的 ARM64 / OPKG 与 x86_64 / APK 真实安装验收均已完成，最终结论见 [正式验证报告](V1.0.2-验证报告.md)。正式化阶段无需再次执行真实安装或设备 Smoke；下文保留原测试操作记录。

该工具仅位于测试程序集，不是用户 UI 功能，不进入便携 ZIP。不安装插件、不执行 APK / OPKG 操作、不读写软件源。请先完成 N1 OpenClash 真实安装验收，再在 x86_64 / APK 设备运行 Smoke。

在项目目录的终端运行：

```powershell
.\tests\iStoreOS.PluginHelper.Tests\bin\x64\Release\iStoreOS.PluginHelper.Tests.exe --runner-smoke
```

交互输入设备地址、用户名、密码。密码不显示、不作为命令参数或保存到磁盘。连接沿用产品 SshService 检测与 Host Key Trust Store；SFTP 工厂通过产品原 CreateTrustedTransfer 创建，不接受任意指纹。凭据的会话内存生命周期与产品相同，关闭测试进程后结束。

Smoke 通过原 IPluginInstallSession / SshPluginInstallSession 调用创建专属 GUID session、上传明确标识的合成脚本、产品 Launch、产品 SFTP Poll、ExitCode 与 Cleanup。脚本只输出 70400 个 x 和 RUNNER_STDOUT_OK、向 stderr 写 RUNNER_STDERR_OK，然后退出 0；大于 65536 字节的输出用于验证真实 offset。不会接收或读取用户 `.run` 文件。

通过时显示 `SMOKE PASS`、stdout / stderr 字节数、exit=0、polls≥2、starts=1、cleanup=True。请保存终端结果。若已派发但结束未知，仅保留当前 session 并报错，不自动重新派发或提前清理；不要直接重复运行不明状态的任务。

测试程序里的交互入口会通过反射复用产品内部 SSH session 和原受信任 SFTP 工厂；该入口不改变产品 API 或用户操作流程。自动 Smoke 使用 fake transport，离线 shell 使用本地文件适配器；两者均不声称已经验证真实 SFTP 子系统。x86 实机 PASS 是发布前单独要求。
