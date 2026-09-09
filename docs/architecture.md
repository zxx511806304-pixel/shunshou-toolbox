# 架构与更新约定

## 结构

- `Shunshou.App`：WinUI 3 原生窗口、导航、选择文件、任务进度与取消、错误/结果展示。通过服务 API 调用核心，后续改 UI 不必重写转换引擎。
- `Shunshou.Core`：按领域拆分 CompressionService、ImageService、PdfService、OcrService、MediaService、FileService。每次任务接受 CancellationToken；后台执行；保留原输入。
- `Shunshou.SmokeTests`：生成测试资料并验证真实 ZIP、PNG、PDF、DOCX、PPTX、音视频、文件名和内容。结果写入独立 artifacts 目录。
- `runtime`：构建阶段获取的 FFmpeg 和 OCR 模型，不提交 Git。随便携包完整分发，应用运行时不下载。
- `scripts`：可重复获取组件和制作便携包的入口。锁定来源、版本、SHA256；更新依赖需要重新测试。

## UI 依据

采用本机已有 `winui-design` skill，查阅了 [Microsoft WinUI Gallery](https://github.com/microsoft/WinUI-Gallery) 的 NavigationView、NumberBox、InfoBar 样例，并用 Microsoft `winapp find-ui` 检索并读取 NavigationView 控件代码。

GitHub 检索同时发现 [SudoCode76/winui3-skills](https://github.com/SudoCode76/winui3-skills)。参考其原生控件、异步和可访问性思路；没有自动安装来自第三方的执行脚本。初版遵循五分类导航、明确处理顺序、常用参数先展示、进度与取消固定可见的布局。

## 数据与资源

没有账号、联网激活、上传接口或遥测。应用只调用本地处理库和随包附带的 FFmpeg。媒体输入协议限制为本地文件/管道。OCR 模型离线携带。日志保存在本机，只记录异常用于排查。

ZIP 解压拒绝路径穿越、重复冲突和链接，预览版每次最多 20,000 条目、5 GiB 展开大小。图像解码及长图带有明确资源边界，遇到限制提示用户而不自动降清晰度。

重命名采用临时名称分两阶段迁移，失败尝试回滚；写入包含内容哈希的撤销记录，文件被修改后拒绝盲目撤销。

## 下一轮迭代

1. 使用真实文档与照片继续改善压缩质量选择和对比预览，尤其是文字截图。
2. 增强图片直接定大小、图片转 PDF、PDF 选页/旋转、音视频剪切。
3. 增加 7z / RAR 解压，以及 Everything 索引集成；保持模块可选，避免常驻负担。
4. 改善 Word / PPT 的布局恢复，单独评估商业引擎的授权和成本。
5. 在干净 Windows 10/11 电脑验证便携包，再进行正式售卖版本的代码签名与发布。

已购版本永久可用、无需激活的产品决定保持不变。后续功能与 UI 更新通过新便携包提供；更新前退出旧进程，保留 data 目录与用户输出。
