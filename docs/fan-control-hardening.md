# 风扇控制加固

本文记录较早加固轮次：将固定 RPM 的硬件读取、控制决策和界面显示拆开，并修复异常恢复状态与历史 CSV 兼容性。下文“本轮”仅指该历史轮次，不是当前文档/目录整理轮次。当前 mode-fix 全量 Release 1088/1088 与实际升级、较早快照四项正常安装生命周期证据及其未验分支见 [发布说明](release-packaging.md)；本次不改源码/二进制、不重打包，仓库新文档尚未进入现有包，后续随包发布需重建验证。

## 调度与读取

主窗口控制/遥测定时器以 1 秒调度，任务栏的 1–10 秒设置只限制任务栏显示刷新。托盘提示独立于任务栏开关更新。固定 RPM 会话还使用单调时钟限制反馈频率，额外的手动刷新不会在 1 秒内重复修正 duty。图表按时间戳绘制最近两分钟的数据。

`PowerSwitchHardware.ReadSnapshot()` 只读硬件，调用者需明确调用 `ApplyFixedRpmControl()` 才会执行固定 RPM 反馈或恢复。采样时间在读取开始时记录，耗时过长的读取不能被当作新鲜数据。

这仍是 WPF 调度与同步 WMI 调用，不是实时控制系统。UI 线程繁忙、WMI 阻塞、休眠等可能延迟调度；Guardian 通过租约过期提供独立进程恢复。完整睡眠/唤醒验收仍待执行。

## 固定 RPM 故障和恢复

`FixedRpmControlSession` 统一管理标定前馈、反馈、故障和恢复。出现以下情况时停止反馈，尝试请求 EC 自动并读取 RPM：

- Guardian 未运行或控制租约丢失/过期；
- WMI 返回错误、温度/RPM 缺失或越界、非有限温度；
- 采样超过 3 秒或时间戳明显在未来；
- 固定 RPM 模式下温度达到 90°C；
- 初始/反馈 duty 写入失败；
- 连续低 RPM、在 duty 上/下限持续偏离目标。

固定 RPM 达到 90°C 时立即写入 100% duty 并锁定紧急满速，停止反馈修正；温度回落也不会自动降速或交回 EC 自动，必须由用户显式请求恢复 EC 自动。紧急满速锁定（`IsEmergencyFullSpeed`）与“待恢复”是彼此独立的状态：Guardian 停止、租约丢失/过期、坏样本或过期样本、满速写入失败等普通故障都不会清除锁定，也不允许后续 tick 恢复闭环或写入更低 duty——有控制权时只重新写 100% duty，需要交回 EC 自动时照样请求 `FanDuty=101` 但保留锁定，只有显式“恢复 EC 自动”才清除。安全曲线原有的 90°C 满速策略保持不变。

自动恢复失败后不会重新启用反馈，只重试恢复；租约保留且不再续期，供 Guardian 重试。自动恢复成功后保留故障原因、请求/有效目标及最后 RPM 误差，直到显式恢复成功或成功切换新的控制模式。EC 自动恢复仅能确认请求与 RPM 读回，无法回读实际控制归属。

目标因标定范围钳制仅作为目标受限状态，不再写成保护故障。反馈通过枚举传递状态，不依赖中文状态文案判断。固定 RPM 的当前 duty、故障后停止状态在主窗口和任务栏中清楚显示；重复采样不重复写相同故障事件，快照中的故障字段仍持续保留。

## 租约与曲线

界面启用手动控制前要求 Guardian 运行，并先发布租约。租约只在完成控制步骤后续期，不再由独立 UI 心跳在 WMI 阻塞时持续续租；超过租约期限后不复活旧租约。安全曲线的首次接管同样由入口显式发布租约（界面开关或方案应用的 Curve 分支），曲线写入本身不再创建租约。

每个采样 pass 只读取一次租约有效性，固定 RPM 反馈和曲线分支共用同一个结论。`FanCurveLeasePolicy` 判定：Guardian 未运行或租约无效时曲线必须停止并请求 EC 自动，只有 Guardian 运行且租约有效才允许曲线写入；`FanCurveLeaseGate` 把该结论作为写入门禁交给曲线目标，租约无效时拒绝写入（不触碰硬件、也不写租约），由既有 `FanCurveController` 失败路径进入恢复自动 / 待恢复状态。因此 Guardian 恢复 EC 自动、清除或放任租约过期后，曲线无法通过写入 duty 重新夺回控制权。

固定 RPM 或曲线发现旧租约无效时停止控制。关闭曲线若恢复失败，曲线已禁用并保留待恢复状态，后续控制步骤重试恢复；不再输出曲线 duty。手动写入失败也尝试恢复，失败时保留租约。

Guardian 清理租约使用 compare-and-clear：只有磁盘上的记录仍与它评估过的那条完全一致（PID + 进程启动时间 + 控制模式 + 心跳）时才删除，否则是安全 no-op。这样应用在恢复期间重启并写入新租约时，旧清理不会删掉新主的所有权。应用侧按 PID 清理的旧入口（`ClearOwned(int)`）保留，但同样在租约锁内重新读取再比较；它不能削弱记录级 API，Guardian 恢复仍必须用 expected 重载。租约拒绝与恢复失败的消息只包含状态描述，不带文件路径或原始命令行。

租约文件的读-比较-替换/删除现在有跨进程互斥：`GuardianLeaseStore` 用租约同目录的 `<lease>.lock` 锁文件实现短持有锁（`FileMode.OpenOrCreate` + `FileShare.None`）。`WriteActive`（临时文件 + 原子替换）、`ClearOwned(expected)`、`ClearOwned(int)` 与 `Clear()` 都在锁内完成，App 与 SYSTEM Guardian 因此不会在“比较完成、删除之前”插入一条新记录。选锁文件而不是命名 Global Mutex，是为了避免显式 ACL：锁文件继承租约目录的权限，谁有权发布租约就有权拿锁；Guardian 作为 SYSTEM 不会代建该目录，以免先于应用创建出应用不可写的 ACL。锁只在同步的文件操作期间持有，锁内不做 WMI/EC、网络、await；持锁等待有界（默认 300ms，10ms 重试间隔，构造可调，上限 5s），释放在 `using`/`finally` 中完成。

锁失败一律走安全分支：`WriteActive` 取锁超时则不写租约、抛出受控 `IOException`，由既有控制失败路径（曲线 `FanCurveController` 的恢复自动、界面/方案应用失败提示、心跳 `RefreshLease` 的日志）处理，绝不带病写硬件；`ClearOwned` 取锁超时则安全 no-op 返回 false，租约保留到下一轮 Guardian 轮询重新决策，不会因为读锁失败误删活跃租约。进程崩溃留下的锁文件不会永久阻塞：独占句柄随进程释放，锁文件本身不做删除，避免 delete-pending 竞态。`Read()` 刻意不加锁（替换是原子 rename），因为“读不到”必须保持为“当前没有租约”的安全语义，而不是“锁忙”。

这里不是应用与 Guardian 之间的跨进程原子硬件锁。`FanCurveLeaseGate` 的结论来自 pass 开始时的那一次租约读取：单次 pass 内的极慢 WMI 调用仍可能在写入前让租约过期（写入按完成控制步骤续期）。Guardian 与应用同时恢复、长时间 WMI 调用以及休眠后的实际行为仍需目标机验收。

## 曲线状态与 OSD 可观测性

同一 pass 的租约结论（`FanCurveLeaseDecision`）与曲线决策（`FanCurveDecision.Level` / `Reason`）现在都进入纯逻辑 `FanCurveStatusFormatter`（`MiniProToolkit.Core/FanCurveStatus.cs`），主窗口只负责显示与开关副作用。状态分类 `FanCurveStatusKind` 至少区分：

- `Normal` / `Warning` / `PreEmergency` / `CurveEmergency`：曲线正常、预警级、紧急前级、曲线紧急满速（默认 70/80、82/100、90/100，门禁与阈值不变）；
- `SensorInvalid`：曲线引擎自己判定温度无效/过期并恢复 EC 自动（唯一会被显示为“传感器无效”的状态）；
- `GuardianUnavailable` / `LeaseInvalid` / `RecoveryPending`：安全门拒绝（Guardian 已停止、租约无效/过期、恢复未完成），显示“已交还 EC 自动/正在重试”，**不再**冒用“传感器异常”文案；
- `ApplyFailed`：duty 写入失败导致曲线停止。

曲线紧急事件与固定 RPM 的紧急锁存是两件不同的事：新增的 `OsdEvents.CurveEmergencyFullSpeed`（标题“安全曲线”，合并键 `osd-curve-emergency`）只在曲线从非紧急状态进入 90°C 紧急**且该 pass 真正持有曲线控制**时触发一次，由 `FanCurveEmergencyTracker` 去重（持续处于紧急不重复弹窗，回落后再次进入会重新提示）。它不会读写 `_hardware.IsEmergencyFullSpeed`，也不复用固定 RPM 的 `osd-emergency` 键与文案：固定 RPM 紧急是锁存的（温度回落也不降速，必须显式恢复 EC 自动），曲线紧急只是当轮 100% duty 请求，温度回落即按曲线重新降级。预警/前级不产生 OSD，只更新状态行，避免刷屏。

遥测只新增非破坏性的 JSON 字段 `CurveStatus`（`FanCurveStatusKind` 名称）与 `CurveReason`（Core 给出的级别/租约原因，不含路径、命令行、网卡信息）；CSV 表头与列数不变，因此不会触发旧文件归档。历史事件列表把曲线状态变化显示为一条“曲线状态：…”记录（错误/恢复/控制三档），同一状态重复出现不重复记录。

## 退出与失败恢复阶段

退出流程按可逆性分三段，阶段由纯逻辑 `ShutdownSequence`（`MiniProToolkit.Core.ShutdownRecovery.cs`）记录，不引用 App/WPF/硬件类型：

- `ControlRecovery`（可恢复）：停表/退订电源事件、等待采样 gate、关闭曲线、`RestoreAutomatic`、清理租约、释放可选 LHM/频率读数器。此处任何失败（含 Guardian/恢复自动失败）都不被吞掉：取消退出、恢复采样并提示用户，热键/电源事件/计时器会被重新确认（热键重新注册并核验，失败时给出安全提示，不假装已恢复）。
- `HardwareRelease`（不可恢复）：`TelemetryRecorder.Dispose()` 与 `PowerSwitchHardware.Dispose()`。阶段在第一次 Dispose 之前记录，因此部分是释放（例如遥测已关、硬件 Dispose 抛异常）同样按已释放处理。
- `UiTeardown`（不可恢复）：任务栏窗口、热键、OSD、关闭信号、托盘。

`ShutdownSequence.DecideFailureAction` 只在 `ControlRecovery` 阶段返回“恢复会话”；一旦硬件已释放，后续 UI 拆除异常只记录安全摘要并继续关闭进程 —— 不会调用恢复路径，也就不会用已释放的硬件和已清空的租约继续采样。阶段只能单向推进，重复/迟到的调用不会把流程退回可恢复阶段。

### 退出请求门（`ExitRequestGate`）与停止信号可重试

`MiniProToolkit.Core/ExitRequestGate.cs`（`ExitRequestSource`、`ExitFailureNotice`、`ExitRequestGate`）是纯状态：线程安全的互斥闩 + 请求来源记录 + 失败后重新武装，不引用 App/WPF/硬件/等待句柄类型。App 侧用一次 `RegisterWaitForSingleObject(..., executeOnlyOnce: false)` 常驻监听 `Local\MiniProToolkit.Shutdown`，单次语义由门闩承担：

- **同时只有一个退出序列**：托盘/窗口/任务栏（`ExitRequestSource.User`）与安装/卸载停止信号（`ExitRequestSource.ShutdownSignal`）都要先 `TryBeginExit`，已被占用即拒绝，不会出现两个并发 `Close`/`OnClosing` 或重复拆除。
- **信号可重试**：旧实现用 `executeOnlyOnce: true`，第一次退出失败后 `ResumeAfterFailedExit` 不会再注册等待，脚本第二次 `Set` 就没人接。现在监听常驻、失败恢复时只 `Rearm()` 门闩，所以失败后的下一次停止信号照常生效；正在退出时到达的信号会被记为一次性待重试（`Rearm()` 返回 true），恢复后按信号来源重试**一次**，再次失败即放弃，不会循环。
- **信号来源只写日志/诊断，不弹模态框**：`ShowExitFailureNotice` 对 `ExitFailureNotice.LogOnly` 只写 `AppLog` + 诊断事件（诊断文本带“仅记录，不弹窗”后缀）。`Stop-MiniProToolkitGracefully` 只 `Set` 一次并等 15 s，阻塞式 `MessageBox` 会把恢复失败变成安装/卸载失败，所以该来源下（或本次退出期间收到过停止信号）失败一律不弹窗；用户手动退出仍保留提示。恢复失败本身不被吞掉：仍按原语义取消退出、恢复采样、`RestoreAutomatic`/租约失败会阻止关闭并记录。
- **取消退出后恢复托盘语义**：`ResumeAfterFailedExit` 复位 `_closing`/`_trayExitRequested` 与来源，取消后再次关窗回到“隐藏到托盘、继续采样”，不会误走完整拆除；计时器只在停止时才启动、电源事件只在退订后才重订、热键只在未注册时才重新注册并核验（窗口钩子每次 `HwndSource` 最多挂一次），不会重复启动。
- **成对释放**：`TeardownShutdownSignal()` 先 `BeginTeardown()`（之后到达的信号是空操作，不会向已关闭的 Dispatcher 排队），再 `Unregister(等待句柄)` 并有限等待（2 s）正在执行的回调结束，最后才 `Dispose()` 事件句柄 —— 不会 dispose 仍在被等待使用的 event。

对应单测：`tests/MiniProToolkit.Core.Tests/ShutdownSignalExitTests.cs`（失败后可重新武装、并发只进一个、运行中信号记为一次重试、信号来源不弹窗、拆除后不再受理/不再重试）。以上单测仍只是纯状态验证；该历史加固轮次**未**在目标机做安装/卸载真实生命周期验收，不否定后来各自快照的正常流程通过，也不代表失败分支已验。

## 历史 CSV

追加遥测前检查 CSV 表头。表头与当前列不同，会将原文件完整保存为 `telemetry.csv.schema-<时间>-<唯一值>.bak`，随后创建带当前表头的新文件，避免在旧表头下追加新列。兼容的 CSV 继续追加，10 MiB 文件轮转规则保留。格式归档单独保留，未自动清理，可由后续历史管理功能处理。JSONL 保持逐条自描述格式。

## 曲线安全字段的存储与编辑器

`FanCurveSettings` 末尾的可选字段 `RpmFeedback`（基于单套标定的 RPM 反馈保护参数，默认 `null`，启用后只允许增加 duty）与 `LatchCurveEmergency`（90 °C 锁存，默认 `false`）是纯配置，方案存储（`FanProfileStore` 的 `profiles.json`）与 `fan-curve.json` 都原样透传，两者版本号都保持 2：字段缺失的旧文件加载为 `null`/`false`，未知成员由 System.Text.Json 忽略，因此读写新文件不会让任一方向的旧构建整体回退。只有**取值非法**（例如 `deadbandRpm: -5`）才按既有契约处理：严格 `Load` 抛出，启动用 `LoadOrDefault` 回退到内置模板并给出警告，不回退只因为“多了字段”。

曲线编辑器把这两项作为默认关闭的可选安全设置：`RpmFeedback` 启用时只开放死区、最低期望 RPM、失速比例、单次补 duty 与累计补 duty 上限（保守默认 150/600/0.5/2/12，`StallSampleCount=4`、`BadSampleTolerance=2` 不开放控件、按加载值原样保留），非法或越界输入直接拒绝保存；关闭时序列化为 `null`，锁存关闭时序列化为 `false`。编辑器只产生配置，不写硬件；闭环只能抬高 duty，锁存的释放复用既有 `RestoreAutomatic`（“恢复自动/停止曲线”），不新增硬件入口。

## 历史加固轮次验证范围

- Debug 构建 0 警告、0 错误（本轮 `dotnet build MiniProToolkit.sln -c Debug`）；Core 定向过滤测试 **726 passed / 0 failed / 0 skipped**（过滤运行 ≠ 全量运行）；静态枚举约 788 例（59 文件 / 540 Fact / 57 Theory / 248 InlineData，2026-11-01 15:31 本地）只是声明数；全量未重跑。旧的“Core 测试均 78/78 通过”是那轮的历史快照，不代表本轮口径。
- 使用模拟时钟和模拟硬件回调验证频率限制、故障停写、恢复失败重试、故障信息保留、输入异常、租约丢失、Guardian 停止、曲线停止失败及 CSV 格式归档。
- 租约原子性（`GuardianLeaseTests`）：记录级 compare-and-clear 只删它评估过的记录、全字段不一致均 no-op、已删除/目录不存在时安全 no-op；并发 writer/clear 竞争下新记录不被旧清理删除；锁被占用时 `WriteActive` 有界失败且不改动旧记录、`ClearOwned` 有界 no-op 保留租约；崩溃遗留的锁文件不阻塞后续发布与清理。以上均为纯 Core 线程内测试，不启动真实 Guardian 或硬件。
- 曲线状态与事件（`FanCurveStatusTests`、`OsdNotificationsTests`、`TelemetryHistoryTests`）：租约/Guardian 拒绝不会显示为传感器异常、分级状态映射、曲线紧急事件只按进入去重且不复用固定 RPM 合并键、遥测新增 JSON 字段不改变 CSV 表头。
- RPM/s 限制与采样锚点粘滞（`FanCurveRpmRateLimitTests`）：短于 `MinimumSampleSeconds` 的样本只观察、不移动采样窗口锚点，因此突发短样本不会把 RPM 抖动放大成升/降速决定；`RpmRateLimit` 默认 `null`，未启用时行为与旧版一致。只能证明纯 Core 逻辑，没有真实风扇反馈验收。
- WPF 演示自动化：任务栏设为 10 秒仍按秒采样、无硬件后端、故障恢复后提示保留、固定 RPM duty 显示、标定编辑器和方案管理回归。
- 本轮不执行真实风扇写入、Guardian 停止/替换或安装生命周期；以上结果不等同实机长期稳定性验收。
