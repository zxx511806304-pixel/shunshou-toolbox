# 顺手工具箱图标

2026-09-10 用户确认的 S 形工具图案，青绿色底色、白色主体。界面、桌面快捷方式继续显示中文产品名；文件标识使用 `ShunshouToolbox`。

- `ShunshouToolbox-master.png`：由内置 imagegen 生成并按确认稿提取的正式 RGBA 母版，1254 × 1254，圆角外真实透明。
- `../../scripts/Generate-Icon.py`：仅将母版缩放、编码为应用 PNG 和包含 16、24、32、48、64、128、256 像素层的 Windows ICO；不会重绘图案。
- `../../src/Shunshou.App/Assets/Toolbox.png`：窗口内品牌图片。
- `../../src/Shunshou.App/Assets/AppIcon.ico`：主程序、窗口、桌面快捷方式及自解压程序共用图标。

生成方式：内置 imagegen，没有使用 CLI/API fallback。最终修正提示词：

> Precise background extraction for production app icon. Keep the teal rounded square and the white S inside it EXACTLY unchanged. Remove every gray checkerboard pixel outside the tile. Output genuine transparent PNG with alpha=0 outside the rounded tile, not a rendered checkerboard, not gray pixels, not a background image. The previous result had opaque checkerboard pixels; this must be corrected. Preserve all internal teal and white pixels and the original geometry. No mockups, no board, no text, no redesign. One standalone square icon with true transparent corners and narrow transparent margin. High quality clean edge.

复现 Windows 资源：安装 Pillow 后运行 `python scripts/Generate-Icon.py`。构建直接使用已提交资源，不调用生成服务，成品运行完全离线。
