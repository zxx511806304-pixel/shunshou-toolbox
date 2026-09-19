# 第三方组件说明

## 1.0.2 屏幕录制

本地屏幕录制使用 [ScreenRecorderLib 6.6.0](https://github.com/sskodje/ScreenRecorderLib/tree/v6.6.0)，通过 Windows Desktop Duplication、Media Foundation 与 WASAPI 捕获并编码 MP4。NuGet 包 SHA256 为 `5e6c558d27fd4605bbafa608b8859c9b91841f56836d583391a0bd70f4c73641`；上游 MIT 许可正文随包保留于 `docs/licenses/ScreenRecorderLib-MIT.txt`。屏幕区域选择及快捷键使用 Windows 公共 API，不安装捕获驱动。

## 1.0.1 文件索引与 PDF 版式

文件搜索集成 Everything 1.4.1.1032 与官方 ES 1.1.0.38，使用本机 IPC 查询。保留原始 [Everything 许可](https://www.voidtools.com/License.txt) 和 [ES 源码许可](https://github.com/voidtools/ES)，文件位于 `app/tools/everything`；下载来源与 SHA256 由 `runtime-lock.json` 固定。PDF 版式转换沿用 PdfPig、PDFsharp、Open XML SDK 与 Windows PDF 渲染，无新增在线转换服务。LibreOffice 仅用于开发阶段的导出文件显示验证，不随应用分发。

## 1.0.0 视频、字幕与网页工具

网页预览及 PDF 保存使用 Microsoft Edge WebView2 SDK（随 WinUI 依赖），配合用户系统中的 Evergreen Runtime。运行时不打包进本产品，缺少时提供 Microsoft 官方安装页面。SDK 的实际版本及许可原文列入随包 NuGet 清单。原项目与文档：[WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) / [官方示例](https://github.com/MicrosoftEdge/WebView2Samples)。字幕下载复用下述 yt-dlp、CPython 及 FFmpeg 组件，没有新增在线翻译服务。

链接下载采用 yt-dlp 官方源码 zipimport 分发版（Unlicense）、随附 EJS 脚本及其 ISC / MIT 组件、CPython 3.13.15 和 Node.js 24.21.0。原始声明随包保留在 `app/tools/video-download/licenses`，版本、来源与校验值见 `runtime-lock.json` 和 [下载组件说明](docs/VideoDownloadComponents.md)。不包含官方 GPL PyInstaller 下载器 EXE。

水印处理复用现有 LGPL FFmpeg 的 crop、overlay、gblur、drawbox、removelogo 过滤器；未加入 GPL delogo、OpenCV 或非商业 AI 模型。邻域修补是基于周围像素的估算，不恢复原本被覆盖的真实细节。

AI 修补使用 MI-GAN（代码与权重 MIT）、STTN（上游 MIT 项目及作者发布的 checkpoint），通过隔离的 ONNX Runtime DirectML 1.24.4 进程运行。DirectML 二进制按其包内 Microsoft 许可分发，不将仓库的源码许可替代二进制许可。模型原链接、固定版本、哈希及许可依据见 [AI 组件说明](docs/AiInpaintComponents.md)，原文位于 `app/tools/ai-inpaint/licenses`。

程序的“关于顺手工具箱”从本次构建生成的 `docs/components.json` 展示来源与完整依赖。新增外部引擎须登记来源元数据；新增 NuGet 依赖由构建记录自动收录。

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
| DiscUtils.Ntfs / Core / Streams | 0.16.13 | [DiscUtils](https://github.com/DiscUtils/DiscUtils)，MIT；通过公开读取 API 解析 NTFS，许可随 NuGet 声明保留 |
| FFmpeg | n9.0.1-27-g9b0578816c-20260908，LGPL shared | [FFmpeg](https://ffmpeg.org/) / [BtbN builds](https://github.com/BtbN/FFmpeg-Builds)，本构建启用 version3、禁用 GPL；LGPL-3.0-or-later，外部库各有许可 |
| Microsoft Visual C++ Runtime | 14.44.35211.0，x64 | Microsoft 官方签名再分发包，按随包 Microsoft Software License Terms 使用；英文及中文原许可 RTF 位于 `docs/licenses/Microsoft.VisualCpp` |
| PhotoRec | 7.2，Windows x64 CLI | [CGSecurity](https://www.cgsecurity.org/)，GPL-2.0-or-later；未经修改的独立可执行文件，对应完整程序源码包含在 TestDisk 7.2 源码发行包中 |

FFmpeg 是独立进程，通过命令行读取本地文件。分发包含未经修改的 ffmpeg、ffprobe、动态库、LGPL/GPL 许可全文及原始 doc 文档，不包含 ffplay。用户可以替换兼容版本；程序不限制对这些组件进行调试或修改以满足其许可条件。精确构建来源、各文件 SHA256 位于 `app/tools/ffmpeg/build-source.json`；固定资产来自 [autobuild-2026-09-08-23-15](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-08-23-15)，对应 FFmpeg 源代码提交为 `9b0578816c6f94514d330d4f2ae7e44a9fb42692`。固定下载失效或哈希不符时构建脚本会停止，不自动换成最新版本。

`docs/licenses/nuget-license-index.json` 汇总还原出的包版本、作者、原许可信息与可获得的源码提交；各包原始 nuspec、声明的许可文件及第三方 NOTICE 保留在 `docs/licenses/nuget`。部分条目仅是构建依赖，并不意味着其整个软件产品已被打包。标准 SPDX 许可文本补充包内的许可表达式，MIT 文件使用包声明的版权信息；这些收集结果不等于完成全部商业再分发审查。

恢复组件的完整对应源码、依赖源码、补丁和构建资料另见下节。其他组件仍须按各自许可证核对分发义务；FFmpeg 等 LGPL 组件应提供相应源码与必要构建资料。恢复源码包不能替代其他组件的源码交付，也不表示本项目已归档所有第三方的完整对应源码。

## 恢复组件与对应源码

0.3.0 仅分发 PhotoRec 命令行程序，不分发 `testdisk_win.exe`、QPhotoRec 或 Qt。NTFS 原名恢复由本项目的托管读取器与 DiscUtils 负责。PhotoRec 安装在 `app/tools/recovery/bin`，保留上游程序，并使用以下固定依赖：

| 文件或依赖 | 固定来源版本 | 随包声明 |
| --- | --- | --- |
| `cygwin1.dll` | Cygwin 3.6.10-1 | GPL 与 Cygwin 原许可 / 例外条款，含 newlib 的独立声明 |
| `cyggcc_s-seh-1.dll` | libgcc1 14.4.0-1 | GCC 原许可与 GCC Runtime Library Exception |
| `cygiconv-2.dll` | libiconv2 1.19-2 | libiconv / libcharset 原许可 |
| `cygncursesw-10.dll`、`63/cygwin` | ncurses / terminfo 6.5+20240427-1 | ncurses 原版权和许可 |
| `cygjpeg-8.dll` | libjpeg8 3.1.4.1-1 | libjpeg-turbo / IJG 的原许可及声明 |
| `cygz.dll` | zlib0 1.3.2-1 | zlib 原许可 |
| `cygewf-2.dll` | libewf 20140608 | 保留与对应源码 RPM 匹配的原 DLL 及许可 |
| PhotoRec 内静态库 | ext2fs 1.45.3、ntfsprogs 2.0.0 | 完整源码及各自原始许可、构建资料 |

依赖版本、来源、SHA256 / SHA512 及每个二进制和源码的对应关系锁定在 [recovery-runtime-lock.json](scripts/recovery-runtime-lock.json)，安装副本的来源说明位于 `app/tools/recovery/build-source.json`。上游原始许可集中保留在 `app/tools/recovery/licenses`。程序启动使用独立进程、固定命令行、文件和输出流，不限制用户依据各组件许可复制、修改或再分发这些组件；工具箱收费不改变这些权利。

发布必须同时提供 **`RecoverySources-1.0.0.zip`**，从与二进制相同的下载位置以同等访问条件获取。源码包包括 PhotoRec / TestDisk 7.2 完整源码、对应 Cygwin 源码包、保留旧库的源 RPM、补丁、构建配方、许可、准备脚本与文件哈希清单。源码包不放入安装目录，运行软件无需下载或解压它；不能只保留上游链接替代本版本采用的源码交付方式。详见 [恢复源码说明](docs/RecoverySources.md)。不承诺从不同工具链重建出的文件与上游原二进制逐字节一致。

## 原生启动器的构建工具

根目录的启动器由本项目 C 源码使用 [Zig 0.15.2](https://ziglang.org/download/0.15.2/) 构建。Zig 编译器仅用于构建，不装入成品；涉及的 Zig 与 MinGW-w64 启动代码声明保留在 `docs/licenses/native-launcher`。启动器使用 Windows 系统 API 与系统 Universal CRT，不另打包一套 .NET 或 Visual C++ 可再分发运行库。

运行 Windows PDF 等系统 API 不意味着本软件获得 Microsoft Office、WPS、WinRAR 或商业 PDF SDK 的再分发权。本软件没有打包这些产品；Everything 与 ES 按前述各自许可随包分发。

## 卸载模块的研究参考

本轮参考 [BCUninstaller/Bulk-Crap-Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) 的功能流程与公开实现：列表、正常卸载、残留预览及恢复。BCU 根项目为 Apache-2.0，但其 ObjectListView 子目录另含 GPLv3+ 代码。本软件没有打包 BCU、UninstallTools 或 ObjectListView，也没有复制其源文件；当前卸载模块是基于 Windows 公共 API 独立编写的保守实现。以上仓库链接仅说明研究来源，不代表我们已包含其全部能力或取得其他产品的商业授权。
