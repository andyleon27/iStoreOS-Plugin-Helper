# V0.3.1 图标资源

所有图标来自用户提供的同一套母版，裁切为 128×128、32-bit RGBA 透明 PNG，运行时按控件统一缩放到 24 / 26 / 36 / 38 px。圆形或圆角裁切限制在原图标区域，去除整版图外围的杂散透明边缘，不重新绘制图形。

| 母版实际文件 | 独立图标 |
| --- | --- |
| `docs/ui/assets/icon-sheets/v0.3.1-icons-system.png` | app-logo, connect, router, system, sdk, architecture, package-manager, quickstart |
| `docs/ui/assets/icon-sheets/v0.3.1-icons-actions.png.png` | software-source, shield-ok, refresh, plugin, upload, file, folder, log |
| `docs/ui/assets/icon-sheets/v0.3.1-icons-status.png.png` | success, warning, error, copy, eye-show, eye-hide |

两个母版实际带有双 `.png` 后缀，已直接读取，没有修改原文件或请求另行提供路径。app-logo 使用母版中的路由器 / 插件图标，并生成同源 `app-logo.ico`。

全部 PNG 和 ICO 通过项目 EmbeddedResource 嵌入 EXE，无运行时外部图标目录依赖。UiResources 统一加载、缩放、缓存和释放；加载失败返回 null，保留文字控件，密码切换按钮额外提供“显 / 隐”文字 fallback。未使用外部下载、系统替代图标或主题库。

重新裁切：在项目根运行 `scripts/extract-ui-icons.ps1`（仅开发素材处理）。坐标与遮罩在该脚本中记录，预览生成到 `artifacts/icons-v0.3.1.png`。图片裁切不属于程序运行依赖。
