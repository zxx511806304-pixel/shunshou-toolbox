# 链接下载组件

链接下载使用独立进程运行的 yt-dlp 2026.08.19、CPython 3.13.15 和 Node.js LTS 24.21.0，并调用本软件现有的 FFmpeg 处理分离的音视频轨道。链接解析和下载需要网络；本地处理不需要网络。

本软件分发的是 yt-dlp 官方 zipimport 源码包，许可为 Unlicense，内含的 meriyah 为 ISC、astring 为 MIT；没有分发官方 GPL PyInstaller 版 `yt-dlp.exe`，也没有分发 mutagen 或 curl_cffi。CPython 的 PSF 许可和依赖许可、Node.js 的 MIT 许可与第三方声明、yt-dlp 的原文许可均位于 `app/tools/video-download/licenses`。`runtime-lock.json` 记录来源、版本和发布方校验值；`build-source.json` 记录实际分发文件的校验值。

- yt-dlp：<https://github.com/yt-dlp/yt-dlp/tree/2026.08.19>
- CPython：<https://www.python.org/downloads/release/python-31315/>
- Node.js：<https://nodejs.org/dist/v24.21.0/>

下载器仅处理用户粘贴的单个 HTTP(S) 视频链接，不读取浏览器登录信息，不加载用户的下载器配置或插件，不自动获取并执行远程组件。软件启动时不会访问视频平台。不同平台会调整访问规则，需要登录、地区受限或尚不受支持的链接可能无法解析；这类失败不会发布未完成文件。

开发者通过 `scripts/Get-VideoDownloadRuntime.ps1` 获取和校验固定版本；用户无需安装 Python、Node.js 或下载器。更新下载组件时应重新验证许可、来源校验和受影响的平台，不能直接修改某个版本号而沿用旧校验值。
