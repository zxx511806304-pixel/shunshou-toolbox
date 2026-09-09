# 第三方组件说明

本项目自有代码与下列独立组件分别适用各自许可。收费产品也必须保留第三方声明并履行许可证义务；不能移除随包许可文件。

| 组件 | 本次锁定版本 | 项目与许可 |
| --- | --- | --- |
| .NET | 10.0.12 runtime / 10.0.401 SDK | [dotnet/runtime](https://github.com/dotnet/runtime)，MIT，含第三方声明 |
| Windows App SDK / WinUI | 1.8.260317003 | [microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK)，开源仓库许可与分发 NuGet 许可不同；本包保留 NuGet 中的 **Microsoft Software License Terms** 及 NOTICE |
| Magick.NET | Q8-AnyCPU 14.17.1 | [dlemstra/Magick.NET](https://github.com/dlemstra/Magick.NET)，Apache-2.0；ImageMagick 及编解码器许可位于随包 Notice.txt |
| PdfPig | 0.1.16 | [UglyToad/PdfPig](https://github.com/UglyToad/PdfPig)，Apache-2.0 |
| PDFsharp | 6.2.4 | [empira/PDFsharp](https://github.com/empira/PDFsharp)，MIT |
| Open XML SDK | 3.5.1 | [dotnet/Open-XML-SDK](https://github.com/dotnet/Open-XML-SDK)，MIT |
| RapidOcrNet | 4.1.0 | [RapidOcrNet](https://www.nuget.org/packages/RapidOcrNet)，Apache-2.0；模型、ONNX Runtime、SkiaSharp 的来源与声明随包保留 |
| FFmpeg | n9.0.1-27-g9b0578816c-20260908，LGPL shared | [FFmpeg](https://ffmpeg.org/) / [BtbN builds](https://github.com/BtbN/FFmpeg-Builds)，本构建启用 version3、禁用 GPL；LGPL-3.0-or-later，外部库各有许可 |
| Microsoft Visual C++ Runtime | 14.44.35211.0，x64 | Microsoft 官方签名再分发包，按随包 Microsoft Software License Terms 使用；英文及中文原许可 RTF 位于 `licenses/Microsoft.VisualCpp` |

FFmpeg 是独立进程，通过命令行读取本地文件。分发包含未经修改的 ffmpeg、ffprobe、动态库、LGPL/GPL 许可全文及原始 doc 文档，不包含 ffplay。用户可以替换兼容版本；程序不限制对这些组件进行调试或修改以满足其许可条件。精确构建来源、各文件 SHA256 位于 `tools/ffmpeg/build-source.json`；固定资产来自 [autobuild-2026-09-08-23-15](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-08-23-15)，对应 FFmpeg 源代码提交为 `9b0578816c6f94514d330d4f2ae7e44a9fb42692`。固定下载失效或哈希不符时构建脚本会停止，不自动换成最新版本。

`licenses/nuget-license-index.json` 汇总还原出的包版本、作者、原许可信息与可获得的源码提交；各包原始 nuspec、声明的许可文件及第三方 NOTICE 保留在 `licenses/nuget`。部分条目仅是构建依赖，并不意味着其整个软件产品已被打包。标准 SPDX 许可文本补充包内的许可表达式，MIT 文件使用包声明的版权信息；这些收集结果不等于完成全部商业再分发审查。

正式向客户分发时，还应将 LGPL 组件及对应依赖的必要源码和构建资料，与二进制从同一分发渠道提供并核对各自义务。本预览包记录了来源、许可证和校验值，没有宣称已归档所有第三方完整对应源码。

运行 Windows PDF 等系统 API 不意味着本软件获得 Microsoft Office、WPS、WinRAR、Everything 或商业 PDF SDK 的再分发权。本初版没有打包这些产品。
