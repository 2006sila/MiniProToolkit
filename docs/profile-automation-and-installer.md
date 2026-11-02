# 自动方案、任务栏/OSD 与 Inno Setup 工程

> 当前已验二进制为mode-fix快照，见 [发布说明](release-packaging.md)：真实无filter/无SkipTests Release 1088/1088、Debug 0warning/0error、ZIP/Setup hash与740项安装清单匹配，实际同版本升级通过。本轮仅文档/目录整理，不改源码/bin、dist或安装副本、不重打包；仓库新文档未进入现有包，未来包含它们的发布须重建验证。lite子产品及其打包脚本已移除，主项目不依赖它们；单Fan1/单标定/单显示器范围不变。

## 方案自动切换

- 默认关闭；只有显式启用后才会周期性评估。
- 触发器：AC/电池、CPU 占用阈值、每日时间范围（支持跨午夜）、指定进程启动/退出。
- 进程触发可按进程名（例如 `notepad`）触发，也可选填**完整可执行路径**（盘符或 UNC 完整路径，拒绝相对路径与通配符）、**命令行子串**或**命令行正则**（不区分大小写、200 ms 超时；子串与正则只能填一个，无效正则保存即被拒）。路径/命令行条件只对进程启动/退出生效，并在 `ProfileAutomationProcessSampler` 里按需读取（只有声明了对应条件的规则才会去读 `MainModule.FileName` / `Win32_Process.CommandLine`）。进程清单只在内存使用，命令行最多缓存 256 条，**不写入日志、诊断、遥测或 OSD 文本**；读不到命令行时按“命令行不可用”跳过该规则。按 5 秒采样发现状态转换，启动时已运行的进程不视为新启动；进程启动/退出是瞬时事件，不套用持续防抖秒数。
- 同时匹配时先选优先级最高规则，同优先级按名称稳定排序；电源/CPU/时间条件必须持续满足配置的防抖秒数。
- 保存的暂停状态可从主窗口一键暂停/恢复。遇到 Guardian 不可用、WMI 错误、风扇故障、恢复未完成或紧急满速时，不自动应用方案。
- 自动应用调用与手动 Apply 相同的串行门、温度保护和回滚流程。无法得到电源/CPU 条件时对应规则不匹配；应用失败记入诊断，并允许条件稳定后重试。
- JSON 文件位于 `%LOCALAPPDATA%\MiniProToolkit\automation.json`，配置版本 1。配置缺失/损坏回退为关闭状态。方案改名/删除后，引用无效方案的规则需要修复后才能保存。
- 验证规则时使用 Core 单元测试；这不代表真实风扇策略已在硬件上验收。

## 任务栏与托盘

任务栏菜单提供性能模式、EC 自动恢复、满速、固定 RPM 档位及曲线入口。托盘提供相同风扇操作入口。手动风扇操作在 Guardian 未运行、恢复/故障状态、曲线接管或紧急满速锁定时禁用；EC 自动恢复入口保持可用。所有点击仍进入主窗口串行操作与恢复校验，不绕过控制门。

任务栏显示可配置字体族、字号、前景色、背景色、紧凑/竖排及刷新间隔。状态采样和控制周期仍独立。OSD 合并相邻同类事件，在当前支持的显示器工作区定位，并按该显示器的有效 DPI 调整尺寸。目标机器上的 Explorer 重启、当前单显示器 DPI/自动隐藏布局、旋转和任务栏重定位验收仍未完成；多屏混合缩放、多显示器、每屏独立设置和跨屏定位是历史/产品规划内容，超出当前范围，不是 backlog、发布阻塞或验收项。

## 持久诊断事件

- 应用事件：`%LOCALAPPDATA%\MiniProToolkit\history\diagnostic-events.jsonl`。
- Guardian 恢复事件：`%ProgramData%\MiniProToolkit\diagnostic-events.jsonl`。
- JSONL 单文件超过 2 MiB 时滚动保留四份；记录控制/方案结果、错误、恢复开始与恢复结果。
- 历史事件中心读取两个事件日志；诊断页面摘要合并本机可读的应用事件，脱敏后复制/导出。Guardian 事件文件可能受权限限制，诊断页遇到不可读时会显示读取信息；不要分享未检查过的完整事件导出。

## Inno Setup

源工程：`installer\MiniProToolkit.iss`。它使用现有发布目录，不自行发布/重打包：

```powershell
.\scripts\publish-release.ps1 -Version 0.1.0
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" `
  /DAppVersion=0.1.0 .\installer\MiniProToolkit.iss
```

发行目录需先包含完整的 `dist\MiniProToolkit-x64-v<版本>` payload。安装过程由 Inno 暂存 payload，再由 PowerShell 生命周期脚本分阶段备份并替换应用、Guardian、脚本、文档和清单。首次安装可选择随 Windows 启动；检测到 `install-state.json` 时默认升级，传入 `/REPAIR=1` 则走修复流程。安装/升级/修复脚本在启动新服务失败时尝试恢复旧载荷。卸载先在实际卸载开始前运行受保护的 `uninstall-package.ps1`；若发现活动租约或 Guardian 不能安全停止，卸载会中止并保留文件，用户应先按恢复流程处理。

`research/evidence/current-validation/live-installer-{upgrade,repair,uninstall,freshinstall}.json` 已记录较早快照四个正常流程实际通过；`mode-fix-install.json` 另确认最新mode-fix安装器实际同版本升级成功、740项已安装manifest全匹配、配置保留。早期正常流程不自动覆盖最新包的全部生命周期；升级失败回滚、活动租约/卸载失败阻断分支仍未验。临时载荷复制/回滚smoke也不等于这些失败分支验收。登录提权启动、完整服务/租约交错矩阵仍需留证。Setup仍NotSigned，根LICENSE/版权主体与签名决策待定。

控制验收边界：较早Core `FF335A8B…` 的Fixed2500/defaultcurve各20秒、tracking20秒成功，随后真实owner-crash恢复passed=true；tracking旧总false为首次无日志harness阻断。这不是最新mode-fix Core `0D635E40…` 的风扇重测。mode-fix三模式exit0且稳定，独立cleanup-review确认模式验收和配置内容/权限恢复true；旧harness因LastAccess比较false被保留。一次写后1秒只读轮询硬化不证明截图expected0/got2根因已解决。真实管理员Release apphost/demo CLI已过，完整自动化/IPC与UI/Shell/热键、自动标定/2小时soak/故障矩阵仍待验收。
