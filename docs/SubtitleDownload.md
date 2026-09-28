# 网页字幕提取

顺手工具箱通过已经随软件提供的 yt-dlp 读取单个公开视频的已有字幕，按语言列出“网站字幕”和“自动字幕”，然后保存 SRT、VTT 或 TXT。中文网站字幕优先排序。同一种语言的两类字幕可分别选择，网页提供的其他语言轨道也按实际结果列出。

本功能需要联网获取网页及字幕，不上传本地文件，不读取浏览器登录信息，不下载视频正文，也不调用云翻译。没有字幕时显示明确的空结果。播放列表、正在直播和计划直播的链接不在范围内。字幕网址可能过期，读取结果超过 30 分钟需要重新读取。

下载使用最小化的选中轨道元数据，不把网站返回的文件路径或语言标签作为命令、输出路径或正则表达式。每次使用独立临时目录，结束或取消时清理；输出使用新文件名，保留原文件。取消通过相同的进程树终止机制停止下载组件。字幕下载限 32 MB、处理限 3 分钟，未知总长度也会在下载进度达到上限时停止。SRT/VTT 保留字幕时间轴，TXT 去除时间轴和文字样式标记。

依赖与原链接：

- [yt-dlp 字幕选项](https://github.com/yt-dlp/yt-dlp#subtitle-options)：获取网站已有字幕及自动字幕。
- [yt-dlp 元数据输入](https://github.com/yt-dlp/yt-dlp#filesystem-options)：复用已经读取的选中字幕信息。
- [FFmpeg](https://ffmpeg.org/)：字幕格式转换，与现有音视频组件共用。

已有组件清单沿用 yt-dlp / CPython / Node.js / FFmpeg 的固定版本和许可证，不新增下载运行时。

针对性验证命令：

```powershell
.tools/dotnet/dotnet.exe run --project tests/Shunshou.Subtitle.Tests -c Release -- .
.tools/dotnet/dotnet.exe run --project tests/Shunshou.Subtitle.Tests -c Release -- . --limits-only
```

测试使用真实随包的 yt-dlp 和 FFmpeg，覆盖人工与自动字幕区分、SRT/VTT/TXT 实际导出、中文、时间轴、恶意语言标识、无字幕、拒绝播放列表与直播、非法网址、输出路径和取消。通过本地 HTTP 页面及字幕夹具测试网页读取与下载，不把离线夹具测试表述为所有视频平台均可用。
