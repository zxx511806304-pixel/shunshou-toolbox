# 离线 OCR 引擎

默认使用 RapidOcrNet 4.1.0 + PP-OCRv6 Small 多语言模型，能在未安装 Windows 英文 OCR 组件的电脑上识别中文、英文和数字。模型随便携包提供，应用运行时不下载、不上传、不联网。

如果未携带内置模型，才使用 Windows 本机已安装的 OCR 语言组件作为后备。模型存在但不完整时明确报错，避免静默降级。OCR 结果仍需使用者校对；内置模型并不保证任意截图百分之百准确。

## 版本与体积

| 组件 | 版本 | 许可 | Windows x64 实际文件大小 |
|---|---|---|---:|
| RapidOcrNet | 4.1.0 | Apache-2.0 | 80,896 B |
| PP-OCRv6 Small 检测模型 | RapidAI v3.9.2 模型分发 | Apache-2.0 / PaddleOCR | 9,929,594 B |
| PP-OCRv6 Small 识别模型 | 同上 | Apache-2.0 / PaddleOCR | 21,234,383 B |
| PP-OCRv5 行方向分类模型 | 同上 | Apache-2.0 / PaddleOCR | 1,018,508 B |
| PP-OCRv6 配套字典 | RapidOcrNet 发布提交 | Apache-2.0 | 74,947 B |
| ONNX Runtime | 1.29.0 | MIT，另附第三方声明 | 原生约 16.2 MB |
| SkiaSharp | 3.119.1 | MIT，保留原生依赖声明 | 原生约 11.4 MB |
| Clipper2 | 2.0.0 | BSL-1.0 | 103,936 B |

模型加 Windows x64 推理组件的新增总量约 60 MB，未使用 OpenCV。NuGet 下载可能含其他平台组件；发布时必须限定 `win-x64`，不要把全平台 `runtimes/` 原样打包。RapidOcrNet 默认自带的 v5 拉丁识别模型不供本应用使用，也不需要额外打包。

## 可重复获取与部署

先还原 NuGet，再运行 `scripts/Download-OcrModels.ps1`。脚本校验每个模型、字典、RapidOcrNet 许可与 NOTICE 的 SHA256，复制其他 NuGet 许可证。脚本默认从 `.tools/nuget` 或 `NUGET_PACKAGES` 读取依赖许可，也可传 `-NuGetRoot`。

构建目录为 `runtime/ocr/`，发布包目录为 `tools/ocr/`。模型目录已在 `.gitignore` 中排除。主程序根据自己的绝对路径寻找模型，不依赖打开软件时的工作目录。

模型及字典的精确 SHA256 保存在下载脚本中。RapidOcrNet 源码固定为 NuGet 4.1.0 所声明的提交 `e3f71c97ed106fec71aadb38a603a9382e444995`。检测与识别模型 SHA256 已与 RapidAI 官方模型清单核对。

## 验证记录

系统后备引擎在本测试机只装有 `zh-Hans-CN`，将 `Hello` 误识别为 `He 丨尾`，长图中的 `FIRST` 误为 `QST`。这些是实测识别质量问题，未通过降低断言当作修复。

内置 PP-OCRv6 Small 已用相同图片通过原始断言：`SHUNSHOU TOOLBOX`、`Hello 12345`、`文字识别 学习办公`、长截图顶部 `FIRST SCREEN 12345` 与底部 `LAST SCREEN 67890`。合成截图固定在测试 Fixtures 中，保证其他电脑无需安装特定字体即可复现。

## 上游来源

- [RapidOcrNet](https://github.com/BobLd/RapidOcrNet)，API、Apache-2.0 LICENSE 和 NOTICE。
- [RapidAI 官方模型清单](https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml)，模型下载与 SHA256。
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)，模型来源。
- [ONNX Runtime](https://github.com/microsoft/onnxruntime)、[SkiaSharp](https://github.com/mono/SkiaSharp)、[Clipper2](https://github.com/AngusJohnson/Clipper2)。
