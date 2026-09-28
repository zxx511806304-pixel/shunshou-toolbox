# 顺手工具箱 — 跨电脑迁移协议

> 版本 1.8.4 · 适用于 Windows 10/11 x64 · 2026-09-28

## 概述

本协议覆盖项目源代码、配置文件、环境依赖、运行时组件的完整迁移流程，支持「一键导出 → 传输 → 一键导入 → 自动校验」的全链路。

---

## 一、迁移前准备

### 1.1 源机器检查清单

- [ ] 项目可正常 `dotnet build`（0 警告 0 错误）
- [ ] 代码已提交并推送到 GitHub（`git status` 无未提交改动）
- [ ] [.gitignore](file:///d:/idear/app/.gitignore) 排除 `.tools/`、`dist/`、`runtime/`、`artifacts/`、`bin/`、`obj/`
- [ ] U 盘或网络传输空间 ≥ 100 MB（源码包约 6 MB，含锁文件约 7 MB）
- [ ] Git 已安装（`git --version`）

### 1.2 目标机器检查清单

- [ ] Windows 10 22H2 或更高 / Windows 11（x64）
- [ ] 可联网（需下载 .NET SDK、NuGet 包、运行时组件）
- [ ] winget 可用（`winget --version`），用于自动安装 Git
- [ ] 磁盘空间 ≥ 25 GB（源码 7 MB + SDK 300 MB + NuGet 3 GB + 运行时 540 MB + 构建产物 8 GB + dist 2.5 GB）

---

## 二、一键导出（源机器）

```powershell
cd d:\idear\app
pwsh -NoProfile -File scripts\Migrate-Project.ps1 -Mode Export -BundlePath D:\migration\shunshou-migrate.zip
```

**输出内容**：

| 文件 | 说明 |
|---|---|
| `manifest.json` | 全部源文件的相对路径 + SHA256 哈希清单 |
| `environment.json` | .NET SDK 版本、Git 版本、OS 信息、远端仓库地址、项目版本号 |
| `src/**` | 所有源代码、配置、脚本、文档（保持原始相对路径） |

**排除项**（由 .gitignore 规则决定，不进入迁移包）：

| 目录 | 原因 |
|---|---|
| `.tools/` | SDK 和工具缓存，可重新下载 |
| `bin/` `obj/` | 编译产物，`dotnet build` 重建 |
| `dist/` | 发布包，`Build-Portable.ps1` 重建 |
| `runtime/` | 运行时组件，`Download-*.ps1` 重建 |
| `artifacts/` | 验证证据和日志 |
| `.git/` | 版本历史，从 GitHub 拉取 |

---

## 三、传输方式

| 方式 | 适用场景 | 操作 |
|---|---|---|
| U 盘 | 离线迁移 | 拷贝 `shunshou-migrate.zip` 到 U 盘 |
| GitHub | 在线迁移 | 目标机器直接 `git clone https://github.com/zxx511806304-pixel/shunshou-toolbox.git`（替代迁移包） |
| 网络共享 | 局域网 | 将 zip 放到共享目录 |

---

## 四、一键导入（目标机器）

```powershell
# 方式 A：从迁移包导入
pwsh -NoProfile -File scripts\Migrate-Project.ps1 -Mode Import -BundlePath D:\migration\shunshou-migrate.zip -TargetPath E:\projects\app

# 方式 B：从 GitHub 克隆后导入（推荐）
git clone https://github.com/zxx511806304-pixel/shunshou-toolbox.git E:\projects\app
cd E:\projects\app
pwsh -NoProfile -File scripts\Migrate-Project.ps1 -Mode Import -BundlePath . -TargetPath .
```

**导入脚本自动执行**：

1. 解包迁移包到临时目录
2. 逐文件还原源码到目标路径（保持相对路径结构）
3. 逐文件校验 SHA256 哈希（与 manifest.json 比对）
4. 检测并自动安装 Git（通过 winget）
5. 下载 .NET SDK 10.0.401 到 `.tools/dotnet/`
6. `dotnet restore` 还原 NuGet 包
7. `dotnet build` 验证编译（Debug 配置）
8. 可选：运行 6 个 `Download-*.ps1` 脚本下载运行时组件
9. 可选：从 GitHub 拉取 Git 历史

---

## 五、仅校验（不还原）

```powershell
pwsh -NoProfile -File scripts\Migrate-Project.ps1 -Mode Verify -BundlePath D:\migration\shunshou-migrate.zip -TargetPath E:\projects\app
```

输出 `RESULT: PASS` 表示所有文件哈希匹配，迁移完整。

---

## 六、环境与依赖下载清单

以下组件不在迁移包中，需在目标机器上下载。导入脚本会自动处理大部分步骤；如遇问题可手动操作。

### 6.1 .NET SDK 10.0.401

| 属性 | 值 |
|---|---|
| 版本 | `10.0.401` |
| 下载地址 | `https://dotnet.microsoft.com/download/dotnet/10.0` |
| 直链 | `https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip` |
| 安装方式 | 解压到 `<项目根>\.tools\dotnet\` |
| 校验命令 | `.tools\dotnet\dotnet.exe --version` 应输出 `10.0.401` |
| 备注 | 不要用系统 PATH 上的 dotnet（可能无 SDK），必须用项目内路径 |

### 6.2 Git for Windows 2.55.0

| 属性 | 值 |
|---|---|
| 版本 | `2.55.0.windows.3` |
| 下载地址 | `https://git-scm.com/download/win` |
| 安装命令 | `winget install --id Git.Git -e --silent` |
| 校验命令 | `git --version` |
| 备注 | 自带 Git Credential Manager，首次推送自动弹浏览器授权 |

### 6.3 NuGet 包

| 属性 | 值 |
|---|---|
| 源 | `https://api.nuget.org/v3/index.json` |
| 还原命令 | `dotnet restore Shunshou.sln` |
| 校验 | 构建无错误即通过；锁文件在 `scripts/*-lock.json` |
| 备注 | 约 3.2 GB，首次还原约 3-5 分钟 |

### 6.4 离线运行时组件

由 6 个独立脚本按锁文件下载，支持断点续传（缓存于 `.tools/downloads/`）。

| 组件 | 脚本 | 锁文件 | 大小 | 来源 |
|---|---|---|---|---|
| FFmpeg n9.0.2 | `Download-Runtime.ps1` | `runtime-lock.json` | ~150 MB | BtbN/FFmpeg-Builds |
| OCR 模型 | `Download-OcrModels.ps1` | — | ~50 MB | ModelScope/RapidAI |
| Everything SDK | `Download-Everything.ps1` | — | ~5 MB | voidtools |
| 恢复引擎 | `Download-Recovery.ps1` | `recovery-runtime-lock.json` | ~400 MB | cgsecurity/LibreOffice |
| 7-Zip | `Download-7Zip.ps1` | `sevenzip-runtime-lock.json` | ~2 MB | 7-zip.org |
| 视频下载 | `Get-VideoDownloadRuntime.ps1` | `video-download-runtime-lock.json` | ~100 MB | yt-dlp/Python/Node |
| AI 修补模型 | `Build-SttnModel.ps1` | `sttn-export-deps-lock.json` | ~1 GB | PyPI/ONNX |

**一键下载全部**：
```powershell
pwsh -NoProfile -File scripts\Download-Runtime.ps1
pwsh -NoProfile -File scripts\Download-OcrModels.ps1
pwsh -NoProfile -File scripts\Download-Everything.ps1
pwsh -NoProfile -File scripts\Download-Recovery.ps1
pwsh -NoProfile -File scripts\Download-7Zip.ps1
pwsh -NoProfile -File scripts\Get-VideoDownloadRuntime.ps1
pwsh -NoProfile -File scripts\Build-SttnModel.ps1
```

### 6.5 构建工具（Zig + ILSpy）

| 工具 | 版本 | 用途 | 自动下载 |
|---|---|---|---|
| Zig | 0.15.2 | 编译原生启动器 | `Build-Portable.ps1` 自动处理 |
| ILSpy | 11.1.0 | 许可证元数据收集 | `Build-ComponentCatalog.ps1` 自动处理 |

---

## 七、构建与发布

导入完成后，按以下顺序构建：

```powershell
# 1. 构建便携包
pwsh -NoProfile -File scripts\Build-Portable.ps1 -Version 1.8.4

# 2. 构建安装包
pwsh -NoProfile -File scripts\Build-Setup.ps1 -Version 1.8.4

# 3. 构建恢复源码包
pwsh -NoProfile -File scripts\Build-RecoverySources.ps1 -Version 1.8.4
```

产物位于 `dist/` 目录。

---

## 八、跨系统兼容性

### 8.1 支持矩阵

| 操作系统 | 构建支持 | 运行支持 | 备注 |
|---|---|---|---|
| Windows 10 22H2 x64 | ✅ | ✅ | 最低支持版本 |
| Windows 11 x64 | ✅ | ✅ | 完全支持 |
| Windows 11 ARM64 | ⚠️ | ❌ | 未测试 |
| macOS | ❌ | ❌ | WPF 不支持 |
| Linux | ❌ | ❌ | WPF 不支持 |

### 8.2 版本差异处理

| 组件 | 版本锁定机制 | 兼容性保证 |
|---|---|---|
| .NET SDK | `Directory.Build.props` + 手动指定 10.0.401 | 10.0.x 向前兼容 |
| NuGet 包 | `*.csproj` 中 `<PackageReference Version="x.y.z"` | 精确版本，不浮动 |
| 运行时 | `scripts/*-lock.json` 含 SHA256 | 精确到字节 |
| 项目版本 | `Directory.Build.props` 中 `<Version>1.8.4</Version>` | 全局统一 |

### 8.3 路径管理

- 所有脚本使用 **相对路径**（`$PSScriptRoot` 或 `$repoRoot` 相对推导），不依赖绝对路径
- `dotnet` 命令统一使用 `.tools/dotnet/dotnet.exe`，不依赖系统 PATH
- NuGet 包缓存于 `.tools/dotnet-home/.nuget/packages/` 和 `.tools/nuget/`（硬链接）
- 构建输出到各项目的 `bin/`，发布产物到 `dist/`，均相对于项目根

---

## 九、验证清单

导入完成后逐项检查：

- [ ] `dotnet build src\Shunshou.App\Shunshou.App.csproj -c Debug -p:Platform=x64` — 0 警告 0 错误
- [ ] `git log --oneline -3` — 显示最近提交（如已拉取 Git 历史）
- [ ] `runtime\ffmpeg\bin\ffmpeg.exe -version` — 输出 n9.0.2
- [ ] `runtime\sevenzip\7z.exe` — 文件存在
- [ ] `pwsh -File scripts\Build-Portable.ps1 -Version 1.8.4` — 生成 `dist\ShunshouToolbox-1.8.4-win-x64.zip`
- [ ] 启动应用，托盘图标出现，剪贴板历史可记录文字/图片/文件
- [ ] GitHub Actions CI 全绿（push 后检查 Actions 页面）

---

## 十、常见问题排查

### Q1: `dotnet build` 报 "找不到 SDK"

**原因**：系统 PATH 上的 dotnet 无 SDK，或 `.tools/dotnet/` 不存在。

**解决**：
```powershell
# 确认使用项目内 SDK
& '.tools\dotnet\dotnet.exe' --version
# 应输出 10.0.401；如不存在，手动下载见 6.1 节
```

### Q2: NuGet restore 失败 "Unable to load service index"

**原因**：网络不通或 NuGet 源不可达。

**解决**：
```powershell
# 检查网络
dotnet nuget list source
# 尝试清理缓存后重试
dotnet nuget locals all --clear
dotnet restore Shunshou.sln
```

### Q3: FFmpeg 下载 404

**原因**：BtbN 滚动构建删除了旧标签。

**解决**：更新 `scripts/runtime-lock.json` 中的 Tag/Url/Sha256 到最新不可变构建（从 https://github.com/BtbN/FFmpeg-Builds/releases 查找 `win64-lgpl-shared-9.0.zip`）。

### Q4: XAML 编译错误 "XamlCompiler.exe exited with code 1"

**原因**：通常是 XAML 中使用了不存在的属性或类型。

**解决**：
- `FontIcon` 不支持 `Symbol` 属性（那是 `SymbolIcon` 的）；用 `Glyph="&#xXXXX;"`
- `x:Bind` + 本地类型在 clean build Pass 1 可能失败；用经典 `{Binding}`
- 排查方法：二分删除 XAML 内容定位出错行

### Q5: CI 失败 "Missing offline runtime: xxx"

**原因**：CI 工作流遗漏了某个下载脚本步骤。

**解决**：确认 `.github/workflows/windows.yml` 的 "获取离线组件" 步骤包含全部 6 个下载脚本。

### Q6: PowerShell 脚本中文报错

**原因**：PowerShell 5.1 以 GBK 读取 UTF-8 脚本，中文字符串解析错误。

**解决**：必须用 `pwsh`（PowerShell 7+）运行脚本，不要用 `powershell`：
```powershell
pwsh -NoProfile -File scripts\Build-Portable.ps1 -Version 1.8.4
```

### Q7: 迁移包校验有文件哈希不匹配

**原因**：文件在导出后被修改（如构建产物混入）。

**解决**：
```powershell
# 确认 .gitignore 正确排除 bin/obj/dist 等
git status --porcelain  # 应无输出
# 重新导出
pwsh -File scripts\Migrate-Project.ps1 -Mode Export -BundlePath D:\migration\shunshou-migrate.zip
```

---

## 附：迁移脚本参数速查

| 参数 | 必填 | 说明 |
|---|---|---|
| `-Mode` | ✅ | `Export` / `Import` / `Verify` |
| `-BundlePath` | ✅ | 迁移包路径 |
| `-TargetPath` | Import/Verify | 目标目录（默认当前目录） |
| `-SkipRuntime` | Import | 跳过运行时下载（仅还原源码+构建） |
