# 历史与诊断

主窗口的“历史”打开历史与事件窗口；“诊断”打开设置与诊断窗口；“显示源设置”切换 CPU 温度/功耗的主显示源（只影响显示与记录，不影响风扇控制）。

## 历史窗口

历史窗口默认按最近 24 小时加载 `%LOCALAPPDATA%\MiniProToolkit\history\telemetry.jsonl` 及编号轮转文件（默认 3 份），不访问硬件。“全部时间”读取全部归档，也可以手动打开一个或多个 JSONL 文件；外部文件不使用持久索引。保留/索引/单位面板及内存预算补丁已落地；`history-memory-budget.trx` 已落盘 Counters 为 87 executed / 87 passed / 0 failed，仅为定向 Core 证据。

提供：

- 本地时间起止筛选；
- 温度、实际 RPM、目标 RPM、duty、CPU 占用、CPU 封装功耗趋势；
- 1×/2×/4× 等缩放、平移滑块和全时段视图；
- 采样表格查看；
- 模式、控制、恢复、错误和风扇保护事件分类；
- 关键词筛选；
- 所选时间段采样导出为 CSV/JSONL；
- 当前筛选事件导出为 CSV。

历史 JSONL 损坏或写入未完成的行会跳过并显示警告。应用日志和 Guardian 日志会在读取本机历史时作为事件来源；日志文本可能包含本地路径，因此导出事件前应检查内容。

`HistoryChartScale` 与窗口将温度（°C/°F）、RPM、百分比、W 分为四个独立单位面板，共享时间/缩放/平移；按观测值在有界范围内调整量程。目标 RPM 优先使用曲线目标。°F 只转换绘图值，采样表、存储和导出仍为 °C；缺值和明显时间间隙断线，像素桶保留首/末/最小/最大点。CPU 功耗来自 Windows Energy Meter 的 RAPL Package 计数器（单位转换为瓦特），是处理器封装估算值，不是整机插座功率；平台未公开有效计数器时记录为空。历史窗口不是实时采集器，不会触发 WMI 读取，也不会改变当前风扇控制。

### 保留设置与文件级索引（已落地，未实机验收）

`TelemetryRetentionSettings` 默认单文件 10 MiB、3 份编号归档，不启用年龄清理；可配置 1–100 MiB、1–30 份、年龄留空或 1–365 天，保存在历史目录 `telemetry-retention.json`。年龄仅按归档文件 mtime，不逐行按采样年龄删除。清理只选 `telemetry.jsonl.N` / `telemetry.csv.N` 正整数编号文件，不清活跃文件、schema 备份或任意外部文件；窗口提供清理预览/确认，执行时复核长度/mtime，预览后变化则跳过，并拒绝链接路径。

`TelemetryFileIndex` 的 `telemetry-index.v1.json` 是本机文件级缓存，不是数据库。只有可信完整扫描、文件 stamp（长度/mtime/创建时间/首尾摘要）一致且时间 min/max 在筛选区间外时才跳过文件；缓存无效、文件变化或扫描有警告时回退扫描，不用首末行假设有序。默认总扫描预算为 500000 条，达到上限明确警告结果不完整并提示缩小时段；截断文件不标记 trusted，索引跳过的文件不消耗扫描预算。`history-memory-budget.trx` 87/87 通过不等于实机或全量验收。取消/关闭有取消门，不启动硬件读取。

## 可选 LibreHardwareMonitor 对照字段

每条 JSONL 记录在现有字段之后新增三个可选字段；旧记录和旧 CSV 继续可读（表头不匹配时按既有机制归档，不覆盖旧历史）：

- `lhmCpuPackageTemperatureC` / CSV `LhmCpuPackageTemperatureC`：LHM 读取的 CPU Package 温度（°C，保留一位小数）。它与 EC `CPUT`（`CpuTemperatureC`）是同一物理封装的不同读取路径，存在几度偏移属正常现象；控制、保护和阈值仍只使用 EC 温度。
- `lhmCpuPackagePowerWatts` / CSV `LhmCpuPackagePowerWatts`：LHM 的 CPU Package 功耗（W，一位小数）。它与 Energy Meter 的 `CpuPackagePowerWatts` 可以并行比对，二者都不是整机墙插功耗。
- `LhmStatus` / CSV `LhmStatus`：短状态文本（已过滤本地路径），例如“LHM 正常（温度 CPU Package 58.3°C，功耗 CPU Package 15.4 W）”“LHM 仅温度可用（CPU Package 58.3°C，未找到 CPU Package 功耗传感器）”“LHM CPU 传感器已忽略：温度值无效（数值为 0 或超出合理范围）（可选驱动 PawnIO 未安装，本程序不会安装）”“LHM 未发现 CPU 传感器（可选驱动 PawnIO 未安装，本程序不会安装）”。成功时状态里带上已选传感器名称与单位，便于人工对照发现“温度被当成功耗”这类错配；LHM 不可用时值为空，状态说明原因。

温度合理区间为 **(0, 150] °C**（下界不含 0）。没有可选内核驱动时，LHM 仍会枚举出 CPU 温度传感器，但值恒为 `0.0 °C`；活着的 CPU 不会测出 0 °C，因此这类占位值按不可用丢弃，不会被当成“LHM 可用”。实测依据：`research/evidence/telemetry-crosscheck-last.json`（60 秒只读对照，未安装 PawnIO）。

LHM 为可选只读依赖（LibreHardwareMonitorLib 0.9.7-pre717，MPL-2.0）。`LibreHardwareMonitorCpuSensor` 实例在 `MainWindow` 构造时创建（演示模式下不创建，保持为 `null`），`Computer.Open()` 在首次后台采样时才发生。采样循环每秒继续运行，但 LHM 侧按退避重试：退避时长 = 基准延迟 × 连续失败次数（上限 5 倍），即打开失败 60–300 秒、更新失败 30–150 秒；未找到 CPU 传感器退避 120 秒，找到 CPU 传感器但无可用温度/功耗类别退避 60 秒；退避检查在每次进入采样时都会执行，成功一次即恢复正常节奏。这些情况下这三列为空且不会每秒重复 `Computer.Update()`，不影响 WMI 读取、Energy Meter 功耗、风扇控制或 Guardian。传感器选择先按 LHM 传感器类别（温度/功耗）过滤，再套用名称优先级与非 CPU 黑名单，因此 Package 温度不会被当成功耗，Package 功耗也不会被当成温度。实时趋势图按主显示源绘制温度/功耗曲线（图例直接写明当前主显示源），主窗口以单行显示另一个源的原始值：默认（EC/Energy Meter）时为“LHM 对照”，LHM 为主显示源时为“EC/Energy Meter 对照（风扇控制/保护真值）”，因此不会用任一源的值冒充另一个源；演示模式下该行显示“LHM 对照：演示模式不采集”，不会显示 `--°C / -- W` 以免被误认为 LHM 不可用。随包分发的第三方程序集及其许可证见 `THIRD-PARTY-NOTICES.md`（仓库根目录；payload 内位于 docs 同目录）。

## GPU 温度（LHM 只读）

GPU 芯片温度来自可选的 LibreHardwareMonitor GPU 组（`MiniProToolkit.Hardware/LibreHardwareMonitorGpuSensor.cs`，独立于 CPU 对照读取器的另一个 `Computer` 实例）。它是**纯展示/记录值**：不参与风扇曲线、固定 RPM、90 °C 保护、`SetFanDuty`/EC 写入、Guardian 租约，也不进入主显示源（`CpuDisplaySource`）的任何取值路径；`CpuTemperatureC`（EC）仍是控制与保护的唯一温度真值。历史Debug探测读到32°C，较后 `elevated-readonly-probe.json` 在其Release快照读到35°C；这些是可用性观察，不是准确性验收。

- 采集：`IsGpuEnabled = true`，CPU/MSR、主板/SuperIO、存储/NVMe、网络、内存、控制器、电池、PSU、功耗表全部关闭并固定为 `false`，因此主板/NVMe/网络传感器既不会被枚举也不会泄漏到显示字段。只接受 LHM 的 `Temperature` 类别并按 NVIDIA/AMD/Intel GPU 硬件类型筛选，名称优先 `GPU Core`/`GPU Temperature`；数值为 0 或超出合理范围时丢弃。只读调用，不安装 PawnIO、不写 MSR/EC、不做 WMI 读取。
- 采样节奏：与其他可选读取器一样跑在既有后台 `Task.Run` 采样轮次里（UI 线程从不打开硬件），每次采样轮次调用一次读取器。成功一次后 `MinimumSampleInterval = 5 s` 内返回缓存值（GPU 轮询比 CPU 对照更贵、温度变化慢）；打开/更新失败沿用 CPU 读取器的退避策略（基准延迟 × 连续失败次数，上限 5 倍），未发现 GPU 传感器退避 120 秒、无可用温度传感器退避 60 秒，成功一次即恢复。
- 生命周期：实例在 `MainWindow` 构造时创建（演示模式下不创建，保持 `null`，因此不访问任何硬件）；`Dispose()` 是终态且没有重新打开 API，所以睡眠恢复与退出被中止后只置重建标志，由下一次后台采样轮次（持有刷新门）负责 `Dispose` → 置空 → `new`，退出序列在 `ReleaseOptionalReaders` 中释放它。重建失败只让 GPU 行降级显示（`--` + 短状态），不重试刷屏，也不影响 LHM CPU 对照、CPU 频率、WMI/EC 或风扇控制路径。
- 记录：每条 JSONL 追加 `gpuTemperatureC` / `gpuSensorName` / `gpuSensorStatus`（**仅 JSONL**）。CSV 表头与列数不变，所以写入 CSV 时不触发 schema 归档；旧 JSONL 缺这三个键时读为空值。状态文本经共享 `DiagnosticRedactor` 过滤并截断，不含本地路径或凭据。
- 展示：主窗口“实时状态”区新增一行 `GPU（LHM 只读，不参与控制）`，显示温度、传感器名称和脱敏状态；读取不可用时显示 `--` 加状态原因。历史窗口采样表新增只读列“GPU °C（LHM 只读）”（单元格 tooltip 为传感器名称）与“GPU 传感器/状态”，因为 CSV 表头不变，CSV 导出不含 GPU 列。任务栏显示项追加 `GpuTemperature`(12)，默认不加入任何既有设置文件，用户可在既有“任务栏设置”里勾选与排序；任务栏悬停提示补充 GPU 温度、传感器与状态。
- 演示模式不创建 GPU 读取器：主窗口该行显示“GPU（LHM 只读）: 演示模式不采集（未初始化 LHM，不访问硬件）”，不显示 `--` 以免被误认为读取失败。

## 主板/存储辅助温度（已实现，未目标机验收）

`AuxTemperatureSelector` + 独立 `LibreHardwareMonitorAuxTemperatureSensor` 只启用 Motherboard/Storage 组，按真实硬件范围/Temperature类别和名称优先级选择有效传感器，未知为空而不是0。CPU/GPU读取器关闭这些组仅描述各自实例，不代表整个产品不支持主板/存储温度。

任一组有效均保持5秒采样，只有两组均缺失才指数退避5–320秒，单组缺失不拖慢另一健康组。MainWindow/JSONL/history已接线：`motherboardTemperatureC`、`motherboardTemperatureSensorId`、`motherboardTemperatureSensorName`、`storageTemperatureC`、`storageTemperatureSensorId`、`storageTemperatureSensorName` 及读取状态（以源码序列化字段为准）。只读展示/记录，不进 `CpuDisplaySource`、风扇曲线/固定RPM/90°C/EC/Guardian控制。**CSV仍为31列**，旧JSONL缺字段读为空；没有为新增值伪造0。

## 主显示源与历史字段

主窗口“显示源设置”可在两种主显示源之间切换，保存在 `%LOCALAPPDATA%\MiniProToolkit\display-source.json`（缺失该文件的旧安装保持默认）：

- `EC 温度 + Energy Meter 功耗`（默认）：主窗口、任务栏、实时趋势和历史新采样显示 EC 温度与 Windows Energy Meter CPU 封装功耗。
- `LibreHardwareMonitor（LHM）`：上述显示值改用 LHM 温度/功耗。

选择 LHM 时按指标逐项回退：LHM 温度不可用（未采样、驱动/PawnIO 缺失、权限不足、传感器无效或 0 °C 占位值）只把温度回退为 EC；LHM 功耗不可用时只把功耗回退为 Energy Meter。两项都不可用时提示“LHM 不可用，已回退系统源（温度用 EC，功耗用 Energy Meter）；请以管理员运行并确认 PawnIO/传感器可用。”，主窗口与任务栏悬浮提示中可见。设置文件损坏或枚举值未知时回退默认并显示警告（该警告会持续显示在主窗口显示源状态行，直到下一次成功保存显示源设置），不会阻止启动；设置对话框“取消”不改变当前源。设置文件缺少 `version` 字段时按当前版本兼容读取（只缺 `version` 不算错误）；版本号超出范围才回退并警告。

历史新增字段（JSONL/CSV 追加在原 LHM 三列之后）：

- `displayCpuTemperatureC` / CSV `DisplayCpuTemperatureC`：按主显示源落值的温度（LHM 可用时为其值，否则为 EC）。
- `displayCpuPackagePowerWatts` / CSV `DisplayCpuPackagePowerWatts`：按主显示源落值的 CPU 封装功耗。
- `displayCpuSource` / CSV `DisplayCpuSource`：采样时选择的显示源（CSV 中为 `EcEnergyMeter` / `LibreHardwareMonitor`，JSONL 沿用现有枚举写法为数值 `0` / `1`）。
- `displayTemperatureFromLhm` / `displayPowerFromLhm`（CSV `DisplayTemperatureFromLhm` / `DisplayPowerFromLhm`，`True`/`False`）：该指标实际是否来自 LHM；源为 LHM 且两者都为 `False` 即为逐项回退。
- `displaySourceStatus` / CSV `DisplaySourceStatus`：回退提示（未回退时为空）。

原始字段语义不变：`CpuTemperatureC` 仍是 EC 温度，`CpuPackagePowerWatts` 仍是 Energy Meter 功耗，`LhmCpuPackageTemperatureC` / `LhmCpuPackagePowerWatts` 仍是 LHM 对照值。历史窗口同时显示 EC °C / 显示 °C、Energy Meter W / 显示功耗 W、显示源与显示提示列；趋势图按每条记录的显示值绘制（旧记录没有显示字段时回退到原字段）；采样表与导出保留全部字段。切换显示源会记为一条控制类事件，消息使用中文源名称，例如“主显示源：EC 温度 + Energy Meter 功耗 → LHM（LibreHardwareMonitor）”。切换后内存中最近 2 分钟的曲线会立刻按新源重投影（纯计算：不重读硬件、不写盘、不触发控制），已写入的历史行保持不变。

风扇曲线、固定 RPM、90 °C 紧急满速、Guardian 租约与恢复、所有 `SetFanDuty` / EC 写入始终只读 `CpuTemperatureC`（EC）；显示源设置不参与任何控制或写入路径。

## CPU 频率

> 网络速率（上传/下载）与今日流量**明确不做**，不纳入当前产品：主项目不采集、不显示、不记录任何网络速率或今日流量。历史文件里如果还残留旧的网络列，会按下面的归档规则处理，新记录不再写这些列。键盘背光与厂商 Fn/触摸板控制同样**明确不做**，因此历史与诊断里也不会新增任何背光/快捷键/触摸板字段。

CPU 频率是**只读展示指标**：不参与风扇曲线、固定 RPM、90 °C 保护、`SetFanDuty`/EC 写入或 Guardian 租约，读取失败只让对应字段为空并带一条短状态，不影响 WMI/EC/LHM/风扇控制路径。

采样在既有后台 `Task.Run` 采样轮次中完成（`CpuFrequencyReader`），UI 线程从不直接打开性能计数器：

- CPU 频率：`Processor Information\% Processor Performance`（失败退回 `Processor\% Processor Performance`）配合 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\~MHz` 的基准频率换算 MHz。首个样本只建立基线，0/NaN/超范围读数一律记为未知（`-- MHz`）而不是 0 MHz。只读注册表，不写 MSR/EC。
- 读取器生命周期（CPU 频率）：`CpuFrequencyReader.Dispose()` 是终态且没有重新打开 API，所以退出序列必须释放它。**退出成功不需要重建**；退出被中止（等待采样门超时或 EC 恢复失败）后 `ResumeAfterFailedExit` 只置一个重建标志，由下一次后台采样轮次（持有刷新门）负责 `Dispose` → 置空 → `new`，UI 线程从不直接触碰读取器。重建失败只让频率行降级为未知并带短状态“CPU 频率读取器未初始化（不影响温控）”（任务栏悬浮提示可见），不重试刷屏，也不影响 LHM 对照或 EC/风扇控制路径；重建标志不会被退出成功路径设置，所以正常退出不会多做一次读取器构造。

历史新增字段（JSONL 与 CSV 都追加在显示源列之后；旧 CSV 表头不匹配时仍按既有机制整体归档，不覆盖旧历史；旧 JSONL 缺这些键时读为空值）：

- `cpuFrequencyMhz` / `cpuFrequencyPercentOfBase` / `cpuFrequencyBaseMhz` / `cpuFrequencySource` / `cpuFrequencyStatus`（CSV 同名）：当前频率、相对基准百分比、基准频率、来源（`ProcessorInformationPercent` / `ProcessorPercent` / `AbsoluteMhz` / `Unavailable`）与状态文本。
- `curveStatus` / `curveReason`（**仅 JSONL**）：产生该行时那一轮控制的风扇曲线状态摘要（`FanCurveStatusKind` 名称）与 Core 给出的级别/租约原因。两个字段都是可选追加项：**CSV 表头不变**，所以不触发 schema 归档；旧 JSONL 缺这两个键时读为空；它们不包含路径、命令行或网卡信息。

主窗口“实时状态”区有一行 `CPU 频率`，单位 MHz（`MetricDisplayFormat` 统一格式化并固定不变文化）。历史窗口采样表与导出同时显示该列；**实时趋势图与历史曲线没有新增曲线**，避免与现有量程/布局冲突。演示模式不创建读取器，该行显示“演示模式不采集（不访问性能计数器）”。

任务栏显示项新增 `CpuFrequency`(11)，用户可在既有“任务栏设置”里勾选与排序；任务栏悬停提示补充频率。设置文件版本升到 4：默认显示项**保持不变**（新增项不自动加入），v1/v2 迁移只补 `CpuPackagePower`，v3 文件则完全保留用户当时的列表（用户刻意隐藏的项不会被加回）。若旧设置文件里存有本版本已不再支持的显示项（例如已删除的网络项），加载时只丢该项并保留其余选择，不会整份回退默认值。

### Windows效率等级分组频率（已实现，未目标机验收）

`Core/CpuClassFrequency.cs` + `Hardware/CpuClassFrequencyReader.cs` 按Windows CPU-set的 `EfficiencyClass` 与group/logical-index映射聚合逻辑处理器样本，不猜Intel P/E；同构为 `NotApplicable`，异构counter上限256，超限不截断估算。读取绝对 `Processor Information\\Processor Frequency` MHz，**不是effective clock**，也不以整体频率补造分组值。

MainWindow、JSONL `cpuClassFrequency` 和history只读列已有接线，含分组平均/最小/最大MHz、有效/期望样本数及状态；history格式化对无效/缺失数据保持未知。新分组字段不加入CSV，**CSV31列不变**，不新增趋势轴，不参与风扇/EC/Guardian控制。demo不采集。

曲线tracking另外追加JSON-only目标/实际/误差/状态等记录（如 `curveTargetRpm`、`curveTrackingState`），不改CSV31列表头；代码在最终检查中，定向记录不等于真实运行验收。

### 历史删除引发的 schema 变化

- **CSV**：表头删掉了 6 个网络列（`NetworkBytesSentPerSecond`、`NetworkBytesReceivedPerSecond`、`TodayTrafficBytesSent`、`TodayTrafficBytesReceived`、`TodayTrafficDate`、`NetworkStatus`）。在此旧表头上继续写会被 `EnsureCsvSchema` 整体归档为 `telemetry.csv.schema-<时间戳>-<guid>.bak`，旧历史不被覆盖；新文件只写当前表头。
- **JSONL**：新记录不再写这 6 个键；旧行里多出的这些键在反序列化时被忽略，记录仍可读。

## 诊断窗口

诊断页显示：

- 管理员权限；
- Guardian 服务状态；
- 租约存在性和可读的 PID、控制模式、心跳；
- 曲线、历史和日志路径；
- 软件程序集版本；
- 最近恢复相关信息；
- 只读可选能力探测（power-switch / brightness / audio；brightness 与 audio 只报只读读数，状态只会是 `未验证` 或 `不支持`）；
- 系统指示与显式控制：CapsLock/NumLock 仍只读，取键盘当前切换状态（UI 线程 `Keyboard.IsKeyToggled`）；内建屏幕亮度与默认输出音量/静音、默认麦克风静音在后台线程读取（CoreAudio/WMI 操作有界等待，窗口不会被卡住）。`AudioEndpointReader.SetOutputVolume` / `SetMicrophoneMute`、`BrightnessReader.SetBrightness` 及 `DiagnosticsWindow` 显式写入按钮代码已实现，纯逻辑测试通过，真实 COM/WMI 写入未验收，实际设备支持仍未确认。操作先 probe、只写一次并读回比对，超时不重试，不一致标为未确认；亮度仅针对唯一活动内建面板，不改 Fan/Guardian 控制路径。状态文本沿用 `DiagnosticRedactor` 脱敏并截断到 160 字符，缺失值显示“未知”而不是 0/false；
- 最近日志。

“复制脱敏摘要”与“导出脱敏摘要”只包含运行状态、版本、路径占位符和近期信息。摘要会过滤令牌、授权头、密码、API key、Bearer 值、本地路径和网络路径；它不是完整日志导出，也不包含原始凭据。分享前仍应人工检查事件文本。

“打开历史与事件”与主窗口历史入口相同。演示模式不访问硬件：能力探测显示为未开放，系统指示区只显示“演示模式不采集”的说明，不读键盘切换状态、亮度或音频端点。

系统指示读取使用 `WmiMonitorBrightness.CurrentBrightness` 与 CoreAudio 的 `GetMasterVolumeLevelScalar`/`GetMute`；CapsLock/NumLock 使用 `Keyboard.IsKeyToggled`。这些接口本身只负责读取；音量、麦克风静音与内建亮度的显式写入代码已实现、纯逻辑测试通过，但目标机读数、实际设备支持与真实 COM/WMI 写入仍未验收；锁定键仍只读。

## 本次证据边界

## 历史定向证据（保留，不作当前全量总数）

本次未运行App/硬件/构建/测试/脚本/服务/提权。已有 `research/evidence/current-validation/` TRX counters：CPU分组65、接线88、history硬化15，辅助温度62/新鲜度29，tracking遥测195，均failed0；各过滤运行可能重叠，不相加为独立总数。`final-debug-build.log` 为历史统一Debug证据：exit0、0warning0error。

历史 `readonly-system-probe.json` 的唯一只读重试已完成：7calls全部返回、host exit0约1.58秒；CPUclass两次NotApplicable（单效率等级16LP），CPUtotal首样本baseline3793MHz/current=null，audio输出/麦克风无可用值且controls probe=false，brightness无内建面板实例且probe=false。早期0调用依赖阻塞留在history；未setter/EC/LHM/服务/提权。只读已运行但多数不可用，不等于写入/温控验收。历史 `final-release-summary.json` 的 Release954/954 与ZIP/Setup只保留原快照范围；当前mode-fix基线及本轮文档边界见下文，不能把历史读数当当前源码/准确性验收。

## 限制

已有文件级索引、遥测归档保留与独立单位面板，不是数据库级索引、逐行年龄清理或完整 WMI/Guardian 结果数据库。真实硬件验收、睡眠唤醒和安装生命周期不由历史窗口替代。

`ProfileAutomationStatusBoard` 的逐 tick 命中/跳过状态仍只在内存；另有 `ProfileAutomationHistory` 执行日志 `history/automation-executions.jsonl`、自动化窗口历史及匿名 CSV。JSONL 仅 schema version、UTC 时间、稳定哈希规则/方案 ID、outcome/reason 枚举和耗时，不写名称、条件、进程路径/命令行或异常原文。1 MiB/3 份备份，跨活跃文件/备份最多读取尾部 500 物理行，忽略坏行/未知版本；UI 名称由当前配置在内存映射，导出只用匿名类型字段。哈希不是加密，不能承诺不可关联。

音频控制/亮度使用 `SingleFlightOperationWorker`：单后台线程、无排队，busy/timeout/fault/dispose 返回 fallback；超时不取消底层调用、不清运行槽位、不重试，late result 不交给其他请求，Dispose 不 abort/join 底层线程。`BrightnessPanelSelection` 是不做 I/O 的纯 policy，真实 Read/Probe/Set 复用，重复/坏值/不唯一活动内建面板 fail closed。相关已落盘 Counters：`control-worker-boundaries.trx` 45/45、`automation-history.trx` 84/84、`brightness-selection-fakes.trx` 57/57、`accessibility-markup.trx` 8/8，均 failed 0，不相加；后者仅静态标记检查，不是人工读屏。

只读证据：`runtime-eligibility.json` 非管理员、AMD Ryzen 7 8745H、接口元数据存在、Guardian Running；其中证书 harness 失败为历史；纠正后的同步只读 `code-signing-availability.json`/`.log` exit0，CurrentUser/My 与 LocalMachine/My 各有 0 个当前有效且显式 Code Signing EKU 证书、匹配 HasPrivateKey 数也各为 0，仅限这两个 store，不覆盖原 runtime 历史或推断所有证书来源。`readonly-lhm-probe.json` 仅现有 Debug：CPU 温度/功耗 null、GPU 32°C、主板/存储 null。`hosted-demo-smoke.json` attempt 2 纠正 harness 后 PASS：3 次 status exit0/demo=True、activate exit0、8 不同遥测时间戳、正常 shutdown exit0/无 kill；attempt1 是 harness 失败。宿主 `Environment.ProcessPath=dotnet`，不验证发布 apphost 身份/权限，该历史记录不验证apphost；后续真实管理员Release apphost/demo已通过，早期ERROR740不再是当前阻塞。有限真实风扇/正常生命周期证据见下文；睡眠/Explorer/长时验收仍缺。

## 当前快照、读数与剩余边界

`research/evidence/current-validation/mode-fix-release-summary.json` 与 `mode-fix-install.json` 确认无filter/无SkipTests的Core Release1088/1088、Debug0warnings/0errors，新ZIP/Setup hash与740项实际安装manifest全匹配，同版本升级成功。准确hash见 [发布说明](release-packaging.md)。本轮仅文档/目录整理，不改源码/bin、dist或已安装副本，不重新打包；仓库新文档尚未入现有包，未来发含新文档的包需重建验证。lite子产品和其打包脚本已移除，主项目不依赖它们。

`mode-fix-live-validation.json` 的三模式请求均exit0且稳定；旧总false因LastAccess元数据比较而保留。独立 `mode-fix-cleanup-review.json` 的模式验收与配置内容/权限恢复均true，终态无App/lease、GuardianRunning。一次setter后1秒只读poll、25项fake及三模式成功是硬化，不证明截图expected0/got2根因或已根治。

`elevated-release-demo-smoke.json` 通过真实管理员Release apphost/demo CLI；`elevated-readonly-probe.json` 的快照有CPU4201MHz、LHM59.6°C/13.6W、GPU35°C、Storage38°C，主板/音频/亮度未知。这不是校准/准确性、真实setter或完整UI/IPC验收。

较早Core `FF335A8B…` 的 `live-fan-control.json` Fixed2500/defaultcurve各20秒通过；`live-tracking-recovery.json` tracking20秒成功、总false为首次无日志harness阻断；后续 `live-guardian-crash.json` 真正owner-crash恢复passed=true。不是最新mode-fix Core `0D635E40…` 风扇重测。较早live-installer四正常流程通过不外推失败rollback/activelease分支。

剩余：自动标定实机、至少2小时soak、多档稳定性/故障/90°C、睡眠/登录启动、人工UI/a11y、单屏Shell/OSD/热键、自动化/完整IPC及安装失败分支。根LICENSE/版权主体/签名待定，Setup仍NotSigned；第三方notice不代表主项目授权。
