# 0.5.1 定向验证记录

日期：2026-09-16。范围是本轮极简界面、视频下载/局部处理、两档本地 AI、组件来源与退出流程。没有重跑压缩、PDF、OCR、文件恢复或卸载的完整功能套件。

## 交付物

| 文件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| ShunshouToolbox-0.5.1-win-x64-setup.exe | 449560647 | `991e69145421ef005e717adbb56f85378a6da99781a561308a569660fee0944d` |
| ShunshouToolbox-0.5.1-win-x64.zip | 397644245 | `7af457a67dc58147cfd9f2d73e093004e4b6aa436bd254a6766a6bd13241a09f` |
| RecoverySources-0.5.1.zip | 126935471 | `3df7b716bca6da2db6f381b99727fd0c02b598e8b63370adc4e2624d35499f61` |

已先交付 0.5.0 候选包，随后针对运行中关闭窗口的取消边界修订为 0.5.1，并交付新的 EXE 与 ZIP。旧候选文件没有被覆盖。未自动运行安装或更改用户软件目录。

## 最终包验证

- `artifacts/v051-package-final/ui-results.json`：从最终根启动器进入实际 WinUI，浅色→深色→浅色、视频导航/拖入分流、播放器打开与导出、无效链接反馈、两档 AI 的实际 CPU 预览及忙碌状态恢复通过。检查包含正常窗口关闭；外部启动进程退出码为 0。
- 实际查看最终包的浅/深色界面、店铺、About 弹窗及安装包截图。About 使用对弹窗本身的渲染，不把底层窗口截图冒充弹窗检查。
- `artifacts/v051-package-gpu/results.json`：最终随包 MI-GAN 使用 `DirectML + CPU`，STTN 使用 `DirectML`，均完成两帧样本、音轨/时长/尺寸与完整解码检查，无回退。
- `artifacts/v051-package-shutdown/shutdown-results.json`：处理 60 秒生成样本期间正常关闭窗口，已观察到的 AI Runner 与两个 FFmpeg 进程全部结束，剩余进程数 0，窗口进程退出码 0。
- `artifacts/video-cancellation-v051/results.json`：暂停 UI 同步上下文后取消，真实 Core wrapper 启动的受控 Runner 与子进程 108 ms 内退出；此时 UI 续体尚未恢复。测试不加载 AI 模型。
- `artifacts/v051-package-final/integrity.json`：1151 个清单文件的 SHA-256 核验通过；解压根目录仅 app、docs、package-manifest.json、ShunshouToolbox.exe 四项。
- 静态原生依赖检查覆盖 569 个 PE 候选；独立 AI 进程同目录携带 Visual C++ 运行库，不依赖 app 父目录的 DLL 搜索路径。
- 最终 EXE 以截图模式启动并正常退出，未执行安装。嵌入 ZIP 与上表 ZIP 哈希一致。
- 组件来源目录包含 22 个功能条目、37 个不同包/版本依赖记录；独立 AI Runner 的依赖也自动收录，新增外部引擎缺少来源清单会中止构建。

## 本轮先前已完成的相关检查

- 常规视频四种处理、预览、旋转、奇数尺寸、无音轨、错误输入与取消：`artifacts/watermark-v040-full-package-r2/results.json`。检查使用当时的实际打包 Core/FFmpeg，后续常规处理仅补充同步取消注册。
- 下载使用真实 CC0 Wikimedia 视频验证解析、画质选择、完整文件和解码：`artifacts/v040-platform-result/platform-result.json`。下载组件随后保持同一版本；不能将一个网站样本视为所有平台均已验证。
- MI-GAN 与 STTN 的原文件哈希、有效像素变化、音频/时序、CPU/自动执行、错误区域/缺少模型及推理中取消：`artifacts/ai-v041-tests/results.json`、`artifacts/sttn-v041-wrapper/results.json`。路径中的 v041 是开发阶段命名，不是另一个发布版本。
- STTN 导出与原 PyTorch 最大绝对误差 `0.0001009032`；改变相邻帧后中心选区平均差 `0.0788439`，确认使用真实时序上下文。

## 修复与边界

播放预览后退出曾触发原生崩溃，已明确解绑/释放 MediaSource 与 MediaPlayer，使用正常 Close 路径验证。STTN 大图融合曾触发当前显卡执行超时，采用 ONNX Runtime 的禁用 DirectML 大图融合配置后，六帧及最终包样本执行通过；没有修改系统 TDR 或显卡驱动设置。

AI 只是估计被遮挡内容，工程样本不代表所有自然场景效果。没有覆盖所有显卡、网站、真实系统高对比度会话或干净虚拟机；静态依赖检查也不能替代干净系统实测。安装包未代码签名。本机构建的 NuGet 在线漏洞审计因网络/TLS 返回 NU1900，不能宣称在线漏洞审计通过。未运行远程完整 CI，也未合并 main。
