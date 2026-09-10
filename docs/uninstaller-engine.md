# 软件卸载实现与验证

## 采用范围

软件卸载模块采用自有实现，使用 Windows 注册表、原软件登记的 EXE 卸载程序和 Windows PackageManager API。没有打包 BCU、Geek 的程序、DLL 或界面代码，也没有引入 GPL ObjectListView。

研究固定到 BCU 提交 [30da609384c98ba6e35c6530129541ea4ff3970a](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/tree/30da609384c98ba6e35c6530129541ea4ff3970a)。参考其“已安装列表 → 原卸载器 → 独立残留候选 → 用户确认”的流程，以及准确安装路径、置信度、其他软件共享关系的处理思路。没有复制其扫描算法；本版的范围更小，便于逐项验证。

- [BCU 根 Apache 2.0 许可证](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/blob/30da609384c98ba6e35c6530129541ea4ff3970a/Licence.txt)
- [UninstallTools 项目依赖](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/blob/30da609384c98ba6e35c6530129541ea4ff3970a/source/UninstallTools/UninstallTools.csproj)：直接引用 KlocTools 和 ObjectListView，不能只看根许可证就把整个 DLL 当作无额外依赖的 SDK。
- [安装目录扫描参考](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/blob/30da609384c98ba6e35c6530129541ea4ff3970a/source/UninstallTools/Junk/Finders/Drive/InstallLocationScanner.cs)
- [软件注册表扫描参考](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/blob/30da609384c98ba6e35c6530129541ea4ff3970a/source/UninstallTools/Junk/Finders/Registry/SoftwareRegKeyScanner.cs)

## 已实现

- HKCU/HKLM 卸载项，32/64 位视图；保留独立产品标识，合并内容相同的共享视图记录。
- 当前用户的非系统签名 Main MSIX/商店应用列表；不列出框架、资源包、系统签名组件。
- 读取名称、版本、发布者、登记大小、图标路径和安装目录。大小缺失时不递归扫描程序目录制造启动延迟。
- 启动明确的本机 EXE，独立传递参数；MSI 的 /I 登记转为卸载 /X。不使用 cmd /c，拒绝脚本宿主、远程或歧义路径。需要管理员权限时由应用界面引导系统 UAC。
- 等待卸载父进程结束后重新枚举安装列表；退出码为 0 但仍在列表中时，不报告卸载完成。取消等待不会强杀正在修改系统的安装程序。
- 扫描原软件登记的专属安装目录、与软件名/安装目录准确同名的 AppData 子目录、准确的 SOFTWARE\软件名 设置键及可选的原卸载记录。卸载器缺失时仍可走“检查残留 → 逐项选择清理”。
- 用户选择的全部候选先完成内容备份、原件复核和再次软件归属复核，再开始删除；未选择项不动。HKLM/Program Files 等高权限项在权限不足时整批零删除。
- 删除普通文件使用已校验并锁定的文件句柄，目录只做非递归空目录删除。发生变化或新增文件就停止该目录，并保留备份。
- 备份包含文件原始字节、文件名、空目录、文件属性和修改时间，以及注册表值类型、原始字符串/数组/二进制数据和子键。备份清单带完整性校验；恢复只接受工具箱自身备份目录中的有效记录，不覆盖用户后续修改。

## 明确边界

这不是完整的 BCU 嵌入，也没有宣称 Geek/BCU 等价覆盖。不会模糊匹配全盘路径、删除共享公司目录、移除系统服务/驱动、COM 注册、计划任务或系统关联项。只清除卸载列表记录并不代表已卸载程序。Store 应用由系统管理删除，不对 WindowsApps/Packages 做手工残留清理。

为了保证完整备份，包含目录链接、注册表链接、云占位/加密文件、NTFS 附加数据流的候选会跳过；目录超过 2 GB 或 20,000 个条目也跳过。备份不是完整系统还原点：不保存 ACL、目录创建时间、安装程序事务或软件原始安装包。恢复适用于本模块已经备份并清理的残留，不能撤销第三方卸载程序的全部操作。

## 验证证据（2026-09-10）

UninstallTests.RunAsync 全部通过：

- 引号与空格、MSI、脚本/UNC/URL/换行拒绝、带 .exe 的歧义目录解析。
- 注入执行器模拟卸载成功、仍在列表、卸载命令变化、取消和 Store API 分支；没有运行真实卸载程序。
- 合成文件夹和内存注册表：预览零删除、只删除所选项、原件变化保护、全部备份后删除、空目录和注册表类型恢复、拒绝覆盖后来内容、清单篡改拒绝。
- 系统/共享根、其他软件安装目录、扫描后新增注册表共享关系、备份期间新增共享关系、高权限预检。
- 真实 NTFS 附加数据流夹具验证整目录保留。实际目录 junction 回归覆盖安装根、子目录、预览后新增链接三种情况：拒绝清理、首次删除前停止，目标文件 SHA256 不变。没有启用 Windows Developer Mode。

只读枚举在沙箱内得到 36 个桌面项目、0 个 Store 项目；在当前用户正常进程上下文通过实际 WindowsUninstallPlatform.ListAsync 得到 **47 个桌面项目 + 58 个 Store 项目 = 105 个唯一项目**。前者由执行环境隔离造成，不是把后者当作实现失败而隐藏功能。未卸载或清理主机上的任何真实软件。

## Windows 官方接口

- [卸载注册表字段](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)
- [CommandLineToArgvW 参数解析](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw)
- [当前用户包枚举](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuserwithpackagetypes)
- [当前用户包卸载](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.removepackageasync)
- [ShellExecuteEx / 系统提升](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw)
