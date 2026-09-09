# 顺手工具箱

面向日常学习与办公的 Windows 离线工具箱。解压运行，不登录、不激活，文件在本机处理。当前为 **0.1.0 预览版**，功能与 UI 会继续迭代。

## 已实现

| 分类 | 预览版功能 |
| --- | --- |
| 压缩与打包 | ZIP / 文件夹按上传上限压缩，优先无损优化，再按选择降低图片质量或尺寸；ZIP 打包与解压 |
| PDF | 逐页 PNG、高清 PNG 长图、可编辑文字 Word / PPT、合并与逐页拆分 |
| 图片与文字 | JPG / PNG / WebP / BMP / TIFF 批量转换、比例缩放、多帧逐帧导出、离线文字识别 |
| 音频与视频 | MP3 / MP4 / WAV / FLAC / M4A 转换、提取音轨、MP3 / MP4 / M4A 目标大小压缩 |
| 文件整理 | 指定文件夹内按名称搜索文件/图片/文件夹、批量改名预览、按记录撤销改名 |

## 运行

取得 Windows x64 便携 ZIP 后，**完整解压**到可写文件夹，双击 `顺手工具箱.exe`。不要只复制单个 EXE。支持 Windows 10 2004（19041）及以上、Windows 11；预览版仅 x64。

生成的文件默认保存到“文档/顺手工具箱输出”，也可自行选择位置。转换不覆盖原件。批量改名会更改原文件名，执行前展示预览，恢复记录保存在软件目录 `data/rename-history`。

## 准确理解结果

- 20 MB 按 20,000,000 字节计算，并留 2.5% 余量。核验的是最终 ZIP/媒体文件实际大小。无法达标会明确提示，不删除文件或截短视频凑大小。
- ZIP 目标压缩会优化 JPG / PNG / WebP；其他格式原样保留。无损不能保证固定压缩比例。有损模式可能损失细节，需要检查结果。
- PDF 转 Word / PPT **以可编辑文字为主**，不是完整复原排版。无文字页会尝试本机 OCR；复杂表格、公式、图表仍需人工整理。
- PDF 长图保持所选 DPI。非常长的 PNG 可能超过某些看图软件的显示上限，建议同时保存逐页图片。
- 文件搜索当前扫描选中文件夹，不是 Everything 全盘索引。7z/RAR、卸载管理、更多格式和编辑工具尚未包含在本版。
- 不承诺全部格式可转换，也不把有损压缩称作无损；已损失的音质/画质不能通过转换恢复。
- MP3 / M4A / MP4 使用有损编码。初版转换主要音视频轨道；额外音轨和字幕不保留。FLAC 保留整数音频精度；浮点音频和当前编码器无法完整保留的 32 位整数音频会明确拒绝，建议使用 WAV。

## 开发

使用 .NET SDK 10.0.401、Windows x64。UI 与处理服务分别位于 `src/Shunshou.App`、`src/Shunshou.Core`；真实生成样本的集成测试位于 `tests/Shunshou.SmokeTests`。

```powershell
dotnet restore Shunshou.slnx
pwsh ./scripts/Download-Runtime.ps1
pwsh ./scripts/Download-OcrModels.ps1
dotnet build Shunshou.slnx -c Release
dotnet run --project tests/Shunshou.SmokeTests -c Release -- artifacts/smoke
pwsh ./scripts/Build-Portable.ps1
```

可用 `--compression`、`--images`、`--pdf`、`--ocr`、`--media`、`--files` 单独运行测试组。图片和媒体样本由测试生成，不使用用户文件。UI 自身支持 `--screenshot-dir <目录> --theme light|dark`，用于生成真实 WinUI 渲染图。

下载脚本校验固定 SHA256。FFmpeg 使用固定 autobuild 资产，Microsoft C++ 运行库从官方签名再分发包中提取，构建过程中不安装系统运行库。OCR 模型需要完成 NuGet 还原后单独下载；运行成品时无需联网。

便携构建同时携带 .NET、Windows App SDK、本地 C++ 运行库、FFmpeg 和 OCR 模型，并生成文件哈希清单与静态 DLL 依赖报告。已有同版本输出时脚本会拒绝覆盖；使用 `-Version 0.1.1` 或指定新的 `-OutputRoot`。构建成功仍需要对最终目录实际启动和离线验证。

详情见 [架构与更新约定](docs/architecture.md)、[测试报告](docs/validation.md)、[第三方组件](THIRD-PARTY-NOTICES.md)。本项目自有代码未授予公开再分发许可；第三方组件遵循各自许可证。
