# 架构与更新约定

## 结构

- `Shunshou.App`：WinUI 3 原生窗口、导航、选择文件、任务进度与取消、错误/结果展示。通过服务 API 调用核心，后续改 UI 不必重写转换引擎。
- `Shunshou.Core`：按领域拆分 CompressionService、ImageService、PdfService、OcrService、MediaService、FileService、UninstallService。处理服务接受 CancellationToken；转换保留原输入，卸载和重命名仅在用户明确操作后修改。
- `Shunshou.Deployment`：读取便携包清单、校验负载、保留用户数据并切换目录；与解压界面分离，使用生成目录测试故障恢复。
- `Shunshou.DesktopIntegration`：当前用户位置记录、桌面快捷方式和目录运行标记；测试可注入独立存储位置。
- `Shunshou.Setup`：独立 Windows Forms 解压窗口，嵌入完整便携 ZIP 和校验元数据，无联网更新或激活逻辑。
- `Shunshou.SmokeTests`：生成测试资料并验证真实 ZIP、PNG、PDF、DOCX、PPTX、音视频、文件名和内容。结果写入独立 artifacts 目录。
- `runtime`：构建阶段获取的 FFmpeg 和 OCR 模型，不提交 Git。随便携包完整分发，应用运行时不下载。
- `scripts`：可重复获取组件和制作便携包的入口。锁定来源、版本、SHA256；更新依赖需要重新测试。

## UI 依据

采用本机已有 `winui-design` skill，查阅了 [Microsoft WinUI Gallery](https://github.com/microsoft/WinUI-Gallery) 的 NavigationView、NumberBox、InfoBar 样例，并用 Microsoft `winapp find-ui` 检索并读取 NavigationView 控件代码。

GitHub 检索同时发现 [SudoCode76/winui3-skills](https://github.com/SudoCode76/winui3-skills)。参考其原生控件、异步和可访问性思路；没有自动安装来自第三方的执行脚本。初版遵循五分类导航、明确处理顺序、常用参数先展示、进度与取消固定可见的布局。

## 数据与资源

没有工具箱账号或联网激活。文件转换、OCR、AI 修补使用本地处理库和随包组件，媒体转换协议限制为本地文件/管道。视频和字幕下载通过独立 yt-dlp 进程访问用户指定的 HTTP(S) 站点；网页转 PDF 使用独立私密 WebView2 会话，网页在本机打印，不向转换服务上传文件。OCR 与 AI 模型随包携带。日志保存在本机，只记录异常用于排查。

ZIP 解压拒绝路径穿越、重复冲突和链接，每次最多 20,000 条目、5 GiB 展开大小。图像解码及长图带有明确资源边界，遇到限制提示用户而不自动降清晰度。

重命名采用临时名称分两阶段迁移，失败尝试回滚；写入包含内容哈希的撤销记录，文件被修改后拒绝盲目撤销。

## 0.1.1 文件输入、搜索与预览

`InputSelectionPolicy` 是文件选择与拖放的共同入口；WinUI 根节点接收文件事件，读取 StorageItems 时持有 deferral，异步结束后再次检查工具和忙碌状态。非法输入不会替换已有有效选择。实现依据 [微软拖放文档](https://learn.microsoft.com/en-us/windows/apps/develop/data/drag-and-drop)。

`FileSearch.cs` 提供磁盘发现和多范围名称遍历；不建立常驻服务，不要求管理员权限。搜索在后台线程运行，按批次报告结果、进度、取消和截断。搜索列表独立占据有限高度，使用 WinUI ListView 的虚拟化；没有嵌套在工具表单的 ScrollViewer 中。

`SearchPreviewService` 使用 [Windows 缩略图接口](https://learn.microsoft.com/en-us/uwp/api/windows.storage.storagefile.getthumbnailasync?view=winrt-26100)，本地图片缺少系统缩略图时由内置 Skia 解码。图像尺寸、有界编码缓存和并发分别限制；UI 只为可见行保留解码位图。非图像不调用文档应用生成预览，云占位文件、远程路径及目录链接不触发内容取回。选中预览拥有独立取消标记，快速切换不会被旧请求覆盖。

## 0.2.0 工具工作区与卸载

工具按钮直接展开；六类导航中新增软件卸载。通用输入队列与搜索结果使用有界、虚拟化的列表，避免放入外层 ScrollViewer。`InputSelectionWorkspace` 只保存内存草稿；图片转换与 OCR 共用图片批次，单输入工具只带入当前明确选择，其他文件仍在原工具保留。

`OcrWorkspace` 是独立的图片/文字编辑控件，识别 API 不负责自动保存。粘贴图片写入该会话私有临时目录，已接受的图片在会话结束前保留以供批次切换，退出只清理自身创建的图片。图像预览有尺寸上限，识别仍使用原图；TIFF 第一页通过现有 Magick 库有界解码后传入 OCR。编辑器内的 Ctrl+V 保持文本粘贴行为。

`UninstallService` 与 `IUninstallPlatform` 分离：真实平台枚举 HKCU/HKLM 卸载项和当前用户可卸载 Windows 包，测试平台替代注册表和进程执行。界面拥有应用选择与每项清理确认；提升权限只新开本程序窗口并选择对应软件，不自动执行动作。软件信息在运行卸载器前重新核对；卸载后保留已选择的软件快照用于残留扫描。

每次扫描保存候选项身份、目录结构、文件哈希和注册表快照。清理只接受当前服务生成的扫描对象及其中的条目 ID；全部备份成功后才删除选中内容。删除按已验证的文件执行，不用递归删除扫除新出现的未知文件。恢复仅从本程序备份目录读取带完整性验证的记录，不覆盖后来变化的内容。取消等待不杀死正在工作的第三方卸载器。

## 0.2.1 便携部署与快捷方式

完整 ZIP 保持原有主程序与运行库布局，单文件自解压 EXE 内嵌该 ZIP 和 SHA256 元数据。解压界面与部署核心分离，应用本体只接入当前目录运行标记、用户位置记录及首次启动快捷方式逻辑。桌面入口删除状态独立于程序文件清单，便携启动不会反复重建用户主动删除的入口。

部署先检查目标、负载与清单，再在相邻目录准备新版。按旧包清单区分程序文件和需保留的用户文件，完整保留 `data`，完成校验后才切换目录；运行中的同目录程序阻止更新。旧目录作为备份保留，部署记录用于再次运行包时检查未完成切换，不将更新后程序自身的运行错误等同于部署失败。

当前用户位置记录用于填入下次部署位置，不是系统卸载登记。旧 ZIP 目录首次接管可以手动选择；不通过遍历磁盘猜测软件位置。两种分发包都不含激活、后台联网更新或差分包逻辑。详细操作及恢复边界见 [分发与更新说明](distribution-and-updates.md)。

## 下一轮迭代

1. 使用真实文档与照片继续改善压缩质量选择和对比预览，尤其是文字截图。
2. 增强图片直接定大小、图片转 PDF、PDF 选页/旋转、音视频剪切。
3. 增加 7z / RAR 解压；Everything 索引集成已在 1.0.1 完成，后续继续改善首次启用体验。
4. 改善 Word / PPT 的布局恢复，单独评估商业引擎的授权和成本。
5. 在干净 Windows 10/11 电脑验证便携包，再进行正式售卖版本的代码签名与发布。

已购版本永久可用、无需激活的产品决定保持不变。后续功能与 UI 更新可通过完整 ZIP 或自解压 EXE 提供；更新前保存工作并退出旧进程，保留 data 目录与用户输出。组件增量和差分分发需单独实现及验证。
