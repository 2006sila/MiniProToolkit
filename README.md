# MiniProToolkit v0.1.0

针对 Lecoo MINI PRO-AHP 的第三方 Windows 控制台与状态面板，目标是替代厂商 ImagesShow / ControlCenter，并在任务栏与桌面悬浮窗上提供硬件状态显示。本文件是主接手文档：源码包里位于版本根 `README.md`，绿色版里同一内容发布为 `docs\README.md`（内部链接按同级改写）。

**当前版本：0.1.0**（单一版本源为仓库根 `Directory.Build.props`：`Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion` 均为 0.1.0，发布脚本对该版本做三方一致性校验）。

> 许可证状态：主项目采用 **MIT**（仓库根 [`LICENSE`](LICENSE)，`Copyright (c) 2026 2006sila`），决定记录见 [`docs/license.md`](docs/license.md)。随包的 `THIRD-PARTY-NOTICES.md` 只覆盖第三方依赖（MPL-2.0 / MIT / Apache-2.0），不改变主项目的 MIT 条款。代码签名仍未决定。

---

## 1. 项目定位与支持边界

| 项目 | 内容 |
|---|---|
| 定位 | 本机（Lecoo MINI PRO-AHP，x64）CPU 温度 / 风扇转速 / 功耗的只读展示 + 风扇与系统模式的受控写入 |
| 目标平台 | Windows 11 x64；.NET 9；自包含 `win-x64` 发布 |
| 交付形态 | 源码包、绿色版 x64 payload zip、Inno Setup 安装包（Setup 当前 **NotSigned**） |
| 硬件覆盖 | 单 **Fan1**；单套 RPM/Duty 标定；主显示器任务栏覆盖层 |
| 运行权限 | 主程序清单声明 `requireAdministrator`（提权由清单在启动时决定，代码里没有运行期二次提权）；诊断窗口只显示当前是否以管理员身份运行。绿色版同样以管理员启动 |

**明确的支持边界（不是缺陷，是本轮范围）**

- 副屏/多屏任务栏：**不创建** overlay，也不注册 AppBar。只针对主任务栏工作区与 OSD/浮窗所在显示器的**工作区 clamp**。
- 多风扇（Fan2 若有读数仅只读兼容）、多套标定、跨屏配置、每显示器独立设置：不在范围内。
- 网络上传/下载、今日流量统计、隐藏采集、遥测上报：**不包含**。本产品不含遥测上报或网络吞吐功能，产品代码未引入 HTTP/Socket 客户端；本仓库所说的“遥测/历史”指写入 `%LOCALAPPDATA%` 的本地记录，不上传。
- 厂商 Fn 键、键盘背光、触摸板控制：**不包含**（程序自带的 Windows 全局快捷键 `Ctrl+Alt+…` 是另一套机制，不等于厂商 Fn 支持）。
- 主项目采用 MIT（根 `LICENSE`）、Setup 未签名；代码签名属发布者决策，不是本轮实现内容。

---

## 2. 主机与硬件实现

### 2.1 唯一硬件通道：ACPI-WMI

- 通过 `root\WMI` 命名空间中的 `PowerSwitchInterface`（实例 `ACPI\PNP0C14\IP3POWERSWITCH_0`）读写，仅使用该接口暴露的属性/方法：
  - 读取：原始 EC CPU 温度（`GetHwTemp`）、`GetFanControl` 返回的 Fan1 RPM（同一 packed 字段高 16 位是 Fan2 RPM，只读兼容）、系统电源模式（`GetPowerMode`：均衡 / 性能 / 静音）。
  - 写入：风扇 duty（`SetFanControl`，`FanNumber = 1`）、系统电源模式（`SetPowerMode`）。
  - 回读语义按通道不同，不要按“每次写入都读回校验”理解：
    - 风扇 duty 写入后固件**只回报 RPM、不回读控制归属**，所以 duty 的成功判据是“WMI 调用已发出 + 后续 RPM 闭环/可见反馈”，不是对 duty 的回读比对；恢复 EC 自动同样是**请求**语义（写 `FanDuty = 101`），代码随后只读一次 RPM 做可读性确认。快照里的 `FanControlMode` 是 App 自己记录的状态，不是设备回读值。
    - 系统电源模式写入是“写一次 + 有界只读确认”（1 s 预算、100 ms 轮询、不重试写入、不保留 provider 异常文本），超时 / 越界 / 读失败一律按失败处理。
- **不访问 EC MMIO，不直接端口 I/O，不改固件/BIOS，不安装内核驱动**。所有写操作都经由 WMI 的公开方法。
- 读取失败、返回越界值、电源模式写后确认不符都视为“未知/失败”，不猜测、不合成数值（例如不把 0 °C 当成有效温度）。

### 2.2 用户态 App、Guardian 服务、Hardware、Core 的职责

| 组件 | 路径 | 职责 |
|---|---|---|
| `MiniProToolkit.App` | `src/MiniProToolkit.App` | WPF 界面（主窗、任务栏覆盖层、桌面浮窗、OSD、设置/编辑器/历史窗）、硬件写入的实际发起者、单实例与 CLI 管道端点、托盘与分阶段退出 |
| `MiniProToolkit.Guardian` | `src/MiniProToolkit.Guardian` | Windows 服务（`MiniProToolkitGuardian`）：按固定周期巡检主程序发布的控制租约（PID + 进程启动时间 + 模式 + 心跳），在租约过期、进程消失或 PID 被复用时请求恢复 EC 自动，并以 compare-and-clear 清除租约。它**不持有租约、不接管风扇**，也不向 App 暴露任何写入口 |
| `MiniProToolkit.Hardware` | `src/MiniProToolkit.Hardware` | WMI/EC 读数与写入封装、LibreHardwareMonitor 只读传感器（CPU 对照 / GPU / 主板与存储）、音频端点与亮度探测 |
| `MiniProToolkit.Core` | `src/MiniProToolkit.Core` | 纯逻辑与策略：安全曲线、标定、租约协议、遥测与历史索引、配置 schema、布局/主题/快捷键/自动化规则等（不直接 P/Invoke 硬件） |
| `MiniProToolkit.Core.Tests` | `tests/MiniProToolkit.Core.Tests` | xUnit 离线测试：策略、状态机、schema、索引、脱敏等；**不需要真实硬件** |

### 2.3 Guardian 租约与跨进程互斥（fail-closed）

- 租约：主程序在接管风扇前必须**发布并持有**有效控制租约；租约文件位于机器级数据目录（`%ProgramData%\MiniProToolkit\guardian-lease.json`），内容为一条 JSON 记录：所有者 PID、进程启动时间、控制模式、心跳时间。续约与清除都按**记录内容**比较，不看文件时间。
- 跨进程锁：同目录下的兄弟文件 `guardian-lease.json.lock`，用 `FileShare.None` 的只读句柄做互斥，有界等待（默认 300 ms，上限 5 s，10 ms 重试间隔）；App 与 Guardian 的读 / 写 / 续约 / 清除都在该锁内串行，持锁进程崩溃时由内核释放句柄。这个 `.lock` **只做互斥、不存持有人记录、也从不删除**（避开删除挂起竞态）；所以“锁文件存在”不代表“正在控制”，判断控制权只看租约记录。
- **compare-and-clear 针对的是租约记录，不是锁**：Guardian 只在磁盘记录仍等于它评估过的那一条（PID + 进程启动时间 + 模式 + 心跳）时才删除，并在锁内重读、再比对文件身份（长度 / 写入时间）后才删；记录已变、锁忙、读失败一律安全 no-op，新属主的租约不会被误删，已清除的租约也不会被写入路径“复活”。
- **fail-closed**：租约过期、文件读失败（区别于“不存在”）、所有者身份不符、锁不可用，全部按“不可写”处理；宁可停写并恢复 EC 自动，也不继续控制。
- 任何“读失败”都不会折叠成“无租约”这种可据此恢复的判定，避免凭未知状态做权限决策。

### 2.4 显示源与只读对照

- 显示温度/功耗可切换来源（原始 EC + Energy Meter / LHM 对照 / 逐项回退），GPU 温度与主板/存储辅助温度为独立只读通道。
- 显示源设置**只影响展示**，不改变风扇/保护真值：控制与保护始终使用原始 EC `CpuTemperatureC`。

---

## 3. 风扇控制安全状态机

### 3.1 接管条件

1. 用户显式发起（固定 RPM 或温度曲线），且界面/CLI 已完成确认。
2. Guardian 租约已取得并保持续约；跨进程锁可按需获取。
3. 前置读值有效（温度在合理区间、RPM 可读）。

任一条件不满足即拒绝接管，保持 EC 自动。

### 3.2 固定 RPM

- 使用当前标定（duty → RPM 表）做**前馈**：由目标 RPM 反查起始 duty。
- 叠加**有界反馈**：按 RPM 误差微调 duty，调整量和调整速率都有上限，误差持续超限按坏样本/故障处理而不是无限加力。
- 达到稳定带后维持；RPM 连续坏样本有容忍阈值，超过阈值停写。

### 3.3 温度曲线

- 安全曲线最低 **40% duty**，带迟滞与斜率（速率）限制，避免震荡与阶跃。
- 默认节点：45 °C → 40%，…，90 °C → 100%（默认模板另含 70 °C/80%、82 °C/100% 分级）。
- 可选 RPM tracking：由标定点做前馈，并按有界增/回收补偿；与旧 `RpmFeedback` 互斥。
- **90 °C emergency latch**：任一采样达到 90 °C 即锁定 100% duty，普通故障/降温/重启控制循环都不解除；只有**显式恢复 EC 自动**才解除该锁定。
- 提交曲线前先做**原子 CAS 续约**（租约内容未变才续期）；若租约已过期/被清，则放弃本次写入，**绝不复活已清除的租约**。

### 3.4 失败处理与退出恢复

1. **停写**：检测到异常立即停止新的 duty 写入（保留最后一次有效读数与故障原因供界面显示），之后只保留“恢复 EC 自动”这一条写路径。
2. **有界重试**：恢复请求按控制节拍有界重试——固定 RPM 路径每个控制周期（1 s）最多发一次 `SetFanControl(duty=101)` + 读回 RPM，不累积重试次数、不升级写入强度、不在每个采样/刷新里重复发 WMI 写；曲线路径每次控制通过程只请求一次 EC 自动，失败则保留恢复状态由下一轮再试。
3. **退出恢复**：应用退出按固定阶段执行——`ControlRecovery`（停定时器与电源通知、等采样闸门、请求 EC 自动并清理租约）→ `HardwareRelease`（释放遥测记录器与 `PowerSwitchHardware`，不可逆）→ `UiTeardown`（托盘、覆盖层、OSD、快捷键）。`HardwareRelease` 之前的失败会恢复会话继续运行，之后的失败只能走完退出、绝不重新开始采样。
4. **App 不会停止 Guardian 服务**：App 只查询服务是否在运行（`MiniProToolkitGuardian`），停止 / 安装 / 卸载服务属于安装器与维护者的操作，运行期不会自动执行；因此服务侧恢复是 Guardian 自己按租约轮询完成的，不是 App“释放服务”。

### 3.5 验收边界

以上状态机由 `Core` 的单元测试与离线门禁覆盖，**未在真实硬件上做完整热学验收**：≥2 小时 soak、多档稳定性、真实 90 °C 触发、睡眠/唤醒与登录启动序列仍需发布者或维护者在实机完成。不要把单元测试/门禁通过等同于硬件验收。

---

## 4. 显示层

### 4.1 主题与外观边界

- **Obsidian** 是本轮的视觉主题（颜色、字号、圆角、控件状态模板），资源统一用 `Obsidian` 前缀命名；它只做视觉，不含任何业务、数值、命令或事件逻辑。
- 挂载方式是**按窗口合并，不是全局**：主窗合并 `Themes/Obsidian.xaml`；普通功能窗（设置、历史、方案、诊断、曲线、标定…）合并 `Themes/Obsidian.Controls.xaml`（该子字典再合并基础色）。`App.xaml` 只保留一条 Button 边距/内边距样式，不写任何隐式主题样式。
- OSD 弹窗、任务栏覆盖层与桌面悬浮窗**不套用**这套普通窗口皮肤：它们的背景 / 前景 / 不透明度 / 字体 / 位置由用户配置在运行时写入（OSD 的 theme + appearance 解析、任务栏覆盖层的设置应用、悬浮窗自身设置），全局隐式样式会污染这些自定义覆盖层，所以刻意不做全局化。
- 因此新增样式时**不要**写进 `App.xaml`：那会一次性污染所有覆盖层与用户外观。

### 4.2 任务栏覆盖层

- 透明**顶层**窗口覆盖在任务栏之上：不 `SetParent` 到 Explorer/Shell_TrayWnd，不注册 AppBar，不改变工作区尺寸或边距。
- 位置和可见性由挂载/移动/尺寸事件驱动，数值仍按固定节奏采样；任务栏重建（Explorer 重启）后重新挂载。
- 交互：双击只触发一次“打开主窗”；右键菜单提供显示项与安全控制入口；不拦截任务栏原有交互区域。
- **布局模式**（由纯逻辑 `TaskbarItemLayout.Build` 算出计划，App 只把单元渲染成控件；显示顺序始终是配置顺序；宽度用“稳定宽度估算”——标签 + 单位 + 高位数值预留，不随实时读数抖动）：
  - **A · 双列堆叠**（默认，schema 无 `layoutMode` 时也走这个）：每列上下两项（第 0 项在第 1 项之上），列优先填充；前两项为第一组、其余为第二组，只在两组之间画一条 1 DIP 竖线（两侧各 10 DIP）；第 2 列起按 D 的规则加列（列间 12 DIP、无额外分隔线），最多 4 列 / 8 项。
  - **C · 单行分段**：所有项排成一行，前 2 项为第一组、其余为第二组，两组之间一条 1 DIP 竖线（组内约 6 DIP）；装不下时按固定优先级保留 CPU 占用 / 内存 / CPU 温度，其余进 `+N`。竖排任务栏退化为每项一行（无冒号）。
  - **D · 自适应多列**：只要可见项多于 1 个就固定两行，按保存顺序两两成列，列间 12 DIP、无分隔线，最多 4 列 / 8 项；列数只由稳定宽度估算 + 可用安全宽度 / 字号决定，不随实时读数抖动。
  - **E · 标签上、数值下**：每项一张小卡片（标签在上、数值在下，标签不带单位）；横排是一行卡片，竖排时前两项保持卡片、其余每项一行（无冒号）。
- **溢出与省略**：任何模式都不靠 WPF 裁剪静默丢项。估算几何放不进安全宽度 / 高度时，按固定优先级（CPU 占用 / 内存 / CPU 温度 → 风扇 / 功耗 / GPU → 目标 RPM / 目标 duty / RPM 反馈 / 频率 → 状态位与电源模式）从句末省去最不重要的项，并在末尾追加可见的 `+N` 标记（带可访问名与 tooltip，tooltip 列出每个被省略项的完整文本 / 细节）；竖排时 `+N` 单独占一行。安全宽度按 appbar 矩形的 40%（并受栏宽 / 栏高限制）预留，通知区与居中图标簇本轮**未被测量**，所以这个上限是保守预留，不是已验证的托盘避让。竖排任务栏上单个过宽标签仍会用省略号截断，但完整文本保留在单元格 tooltip 与可访问名里，项目本身不被丢弃。
- 显示项的增删与排序在设置里做（复选框只切显隐 + 行内拖拽手柄 / Ctrl+↑↓ 重排，至少保留一项）；文字色、背景色、字体家族 / 字号、布局模式、紧凑间距、刷新间隔（1–10 s）与到任务栏起点的有限 DIP 微调（每点 8 DIP、-200..200）可配置；**组边界 / 分隔线不是配置项**，由布局模式固定。整体开关关闭或任务栏矩形不可用（自动隐藏、Explorer 重启中）时覆盖层直接隐藏，不残留点击热区。

### 4.3 桌面悬浮窗

- 独立配置（尺寸、缩放、吸附边、背景不透明度、显示项与颜色），与任务栏设置互不影响。
- 鼠标穿透模式可切换；从穿透恢复到可交互不会丢失拖拽状态，位置吸附到屏幕工作区边界并在 DPI 变化后重新 clamp。
- 快捷键：`Ctrl+Alt+F9` 显示/隐藏桌面悬浮窗，`Ctrl+Alt+F10` 关闭悬浮窗鼠标穿透（即恢复可交互）。

### 4.4 共享快照

- 任务栏覆盖层与桌面悬浮窗由**同一次 1 s 采样 / 控制通过程**喂同一份 `HardwareSnapshot`（不各自另起采样定时器、不重复访问 WMI）；OSD 提示由同一过程的事件触发。历史窗口只读已落盘的遥测 / 诊断文件，本身不触发采样。

---

## 5. 遥测、存储与日志

### 5.1 数据格式

- **JSONL**（逐行 JSON，新增字段可选）+ **CSV**（固定 **31 列**，新增 JSON-only 字段不改变 CSV 列数）。追加写，按大小轮转。
- CSV 头部与 schema 不匹配时按轮转规则归档旧文件（`.bak`），**不删除历史归档**。

### 5.2 历史索引

- 历史索引 **v2** 带 `SummaryIntegrity` 摘要，用于判定 min/max 等摘要是否可信；不可信时回退为扫描，不返回错误的极值。
- 扫描预算有上限，达到上限会明确提示结果不完整，而不是静默截断。

### 5.3 坏配置与 fail-closed

- 配置文件损坏/字段非法时按 fail-closed 处理（使用保守默认或拒绝该功能），**不删除用户归档文件**，不做自动“修复即删”。
- schema 向前兼容旧版本；**未来版本（更高 schema 号）拒绝加载**并提示，不猜测语义。

### 5.4 日志与诊断摘要

- 应用日志（`%LOCALAPPDATA%\MiniProToolkit\miniprotoolkit.log`）与 Guardian 恢复日志都是**有界轮转**：单文件 2 MiB、保留 3 份旧文件、单条 32 KiB（超长条目在 UTF-8 字符边界截断并标记），所以一条超大异常消息也撑不爆磁盘；日志写入失败被吞掉，不影响监控与退出。
- 诊断事件簿（`history/diagnostic-events.jsonl`）是另一套有界追加日志：单条消息 8 KiB 截断、`category` ≤ 128 字节、`source` ≤ 256 字节、单行 ≤ 64 KiB，超限输入直接拒绝而不是静默写入；文件 2 MiB、保留 4 份。
- **脱敏边界**（`DiagnosticRedactor`）：只作用于明确经过它的文本——诊断窗口的摘要/状态行、历史导出的诊断事件、OSD/通知与系统指标里的状态文本、若干硬件探测失败状态。规则是把本地绝对路径替为 `[LOCAL_PATH]`、网络路径替为 `[NETWORK_PATH]`、`password/passwd/token/api_key/secret/authorization` 类键值与 `Bearer` 令牌替为 `[REDACTED]`。
- 这不是“所有日志都脱敏”的承诺：普通 `AppLog` 行不经过 `DiagnosticRedactor`（写入的是代码里构造的消息与不含路径的固定原因），遥测 CSV/JSONL 是数值 + 固定字段，其中自由文本字段（`Error`/`Event`/状态）不保证逐条脱敏；Guardian 通过 `ILogger` 写服务事件日志的消息里会带上租约文件路径（机器级数据目录，不是用户目录）。需要贴日志求助时请先自己检查再公开。

### 5.5 配置与数据位置（占位符，随环境解析）

| 用途 | 位置 |
|---|---|
| 用户配置（显示项、曲线、快捷键、OSD、浮窗、自动化等） | `%LOCALAPPDATA%\MiniProToolkit\` |
| 机器级数据（Guardian 租约、服务侧状态） | `%ProgramData%\MiniProToolkit\` |
| 遥测与历史（JSONL/CSV/索引） | 用户数据目录下的 `MiniProToolkit` 数据目录 |
| 应用日志 | 用户数据目录（有界轮转） |

程序只使用上述环境变量解析出的目录，不硬编码任何用户目录或盘符路径。

---

## 6. 接手地图

| 目录 | 职责 | 关键文件 / 类 |
|---|---|---|
| `src/MiniProToolkit.Core` | 纯策略与状态机，无 UI 依赖 | `FanCurveController.cs`（曲线/迟滞/应急锁）、`FanCurveLeaseGate.cs`（写前 CAS 续约）、`GuardianLease.cs`（租约协议/读状态）、`GuardianCurveWriteLeasePolicy.cs`、`FixedRpmControlSession.cs`、`FanCalibration*.cs`、`FanRpmFeedbackController.cs`、`FanCurveRpmTracking.cs`、`Telemetry.cs`（31 列 CSV/JSONL）、`TelemetryFileIndex.cs`（index v2）、`TelemetryHistory.cs`、`DiagnosticRedactor.cs`（脱敏）、`BoundedTextLog.cs`、`TaskbarDisplaySettings.cs` / `TaskbarOverlayLayout.cs` / `TaskbarGridLayout.cs`、`FloatingStatusLayout.cs`、`OsdEventTemplates.cs`、`HotkeySettings.cs`、`ToolkitCommandIpc.cs`、`ToolkitSingleInstanceLock.cs` |
| `src/MiniProToolkit.Hardware` | 硬件读写封装 | `PowerSwitchHardware.cs`（`root\WMI` 唯一通道）、`LibreHardwareMonitorCpuSensor.cs` / `...GpuSensor.cs` / `...AuxTemperatureSensor.cs`、`CpuClassFrequencyReader.cs`、`AudioEndpointReader.cs`、`BrightnessReader.cs`、`HardwareCapabilityProbe.cs` |
| `src/MiniProToolkit.App` | WPF 界面与写入发起 | `MainWindow.xaml.cs`（控制循环/状态卡/仲裁）、`TaskbarStatusWindow.xaml.cs`（覆盖层）、`FloatingStatusWindow.xaml.cs`（悬浮窗）、`OsdWindow.xaml.cs`、`ObsidianTheme.cs`、`GuardianLeasedFanTarget.cs` / `GuardianProcessIdentity.cs`（租约侧写入）、`AppCommandPipeServer.cs`（CLI 管道）、`StartupRegistration.cs`、`NativeMethods.cs` |
| `src/MiniProToolkit.Guardian` | Windows 服务 | `GuardianWorker.cs`（租约巡检与恢复）、`GuardianWorkerOptions.cs`、`Program.cs` |
| `tests/MiniProToolkit.Core.Tests` | 离线单元测试（xUnit） | 曲线/租约/遥测/索引/schema/布局/契约等 98 个测试文件；`tests/test-package-common-purge.ps1` 为打包脚本的离线 fixture |
| `scripts` | 开发与发布脚本 | `publish-release.ps1`（门禁 + 构建 + manifest + zip + `.sha256`）、`build-installer.ps1`（ISCC 编译 + MZ/大小/SHA256 校验）、`package-common.ps1`（包内生命周期共用逻辑）、`install/upgrade/repair/uninstall-package.ps1`、`validate-package.ps1`（包自检）、`check-ps-syntax.ps1`（AST 门禁）、`restore-ec-automatic.ps1`。维护者本机专用、需私有证据目录的验证台（如 `env-check.ps1`、`test-release-gates.ps1`、`validate-curve-*.ps1`、`validate-guardian-recovery.ps1`、`scripts/probes/`）不在源码包内，也不参与构建与发布；如需执行请自行准备对应输出目录 |
| `installer` | Inno Setup 脚本 | `MiniProToolkit.iss`（`/DAppVersion`、`/DPayloadRoot`、`/DInstallerOutput` 由构建脚本注入；输出名 `MiniProToolkit-Setup-x64-v<版本>`） |
| `docs` | 实现与验证文档 | [架构与显示/控制隔离](docs/architecture.md)、[风扇控制加固](docs/fan-control-hardening.md)、[标定](docs/fan-calibration.md)、[历史与诊断](docs/history-and-diagnostics.md)、[性能评审](docs/performance-review.md)、[方案自动化与安装器](docs/profile-automation-and-installer.md)、[产品路线](docs/product-roadmap.md)、[发布与打包](docs/release-packaging.md)、[阶段3](docs/phase-3-validation.md)、[阶段4](docs/phase-4-validation.md)、[阶段5](docs/phase-5-validation.md)、[第三方声明](THIRD-PARTY-NOTICES.md) |

### 开发验证边界（勿越界自证）

- **不要**在无该机型的开发机上声称完成：真实 EC 读数准确性、真实风扇写入、90 °C 触发、睡眠/唤醒、登录启动、Explorer 重启后的覆盖层行为、GUI 可访问性人工验收。
- **不要**在未获授权时安装/卸载 Guardian 服务或安装包，也不要提权执行安装器。
- 可以安全做的：`dotnet build` / `dotnet test`、脚本 AST 检查、包自检 `validate-package.ps1`、离线 fixture。
- 单实例/管道/CLI 的“真实主实例转发”必须在真实主实例存在时测，否则按无主实例路径（退出码语义）验证。

---

## 7. 构建、测试与发布

**环境要求**：Windows 11 x64、.NET 9 SDK；构建安装包还需 Inno Setup 6（`ISCC.exe`，脚本会自动探测常见安装位置，也可用 `-CompilerPath` 指定）。

```powershell
# 恢复与构建
dotnet restore .\MiniProToolkit.sln
dotnet build .\MiniProToolkit.sln -c Release
dotnet build .\MiniProToolkit.sln -c Release -warnaserror

# 测试（离线，不需要真实硬件）
dotnet test .\MiniProToolkit.sln -c Release
```

```powershell
# 绿色版（自包含 x64 payload + zip + .sha256）：不要跳过测试门禁
.\scripts\publish-release.ps1 -Version 0.1.0 -OutputRoot <output-root>

# 安装包（使用上面产出的 payload 目录）
.\scripts\build-installer.ps1 -Version 0.1.0 `
  -PayloadRoot <output-root>\MiniProToolkit-x64-v0.1.0 `
  -OutputRoot <installer-output-root>
```

- `<output-root>` / `<installer-output-root>` 由调用者自行指定（相对或绝对路径均可，脚本会解析），仓库不硬编码任何机器路径。
- `publish-release.ps1` 会先跑 PowerShell AST 门禁、Release 测试门禁、版本三方一致性检查、payload 拷贝保真/能力符号/manifest/zip 结构门禁与新鲜度门禁，任一红灯即不产出可发布包。**禁止**用 `-SkipTests` 生成对外发布件。
- **绿色版**：解压 `MiniProToolkit-x64-v0.1.0.zip` 即可运行 `App\MiniProToolkit.exe`（自包含，不装 .NET 运行时；清单要求管理员启动）。包内 `release-manifest.json` 列出全部文件与 SHA256，`scripts\validate-package.ps1` 可离线自检，`scripts\install-package.ps1` 等提供可选的生命周期脚本。
- **安装包**：`MiniProToolkit-Setup-x64-v0.1.0.exe` 需要管理员权限安装，会布置 Guardian 服务；当前 **Setup 未签名**（NotSigned），Windows SmartScreen 可能告警。安装/升级/修复/卸载的真实流程属于发布者验收项，本文档不代表已在本机安装验证。
- 演示模式（不访问硬件，不启用安全曲线与自动标定）：
  `dotnet run --project .\src\MiniProToolkit.App\MiniProToolkit.App.csproj -c Debug -- --demo`

---

## 8. 当前验证状态与限制

**已通过（发布门禁实跑，非本文档编辑轮的产物）**

- Release 全量构建与 `MiniProToolkit.Core.Tests` 单元测试通过（发布流程中作为门禁执行，未跳过）。
- PowerShell AST 门禁、payload 拷贝保真 / 能力符号 / release-manifest / zip 结构 / 新鲜度门禁、包自检 `validate-package.ps1` 全绿。
- 三个交付物（源码 zip、绿色版 zip、安装包 exe）版本一致为 0.1.0，SHA256 旁车文件与文件内容一致。
- 文档修订轮只重新生成源码包（包含本文档的最新文本）；绿色版 zip 与安装包是同一批已通过门禁的产品文件，字节与哈希未变，所以包内 `docs\README.md` 仍是上一轮发布的文本。

**仍需发布者/维护者完成（未验收，不得声称通过）**

- 真实 GUI 人工验收：布局 A/C/D/E 视觉与交互、可访问性（读屏/键盘导航）、多 DPI、Explorer 重启、睡眠/唤醒与登录启动。
- 实机 EC/风扇验收：读数准确性、真实写入、90 °C emergency latch 触发、≥2 小时 soak、多档稳定性与故障注入。
- 安装包生命周期：全新安装、同版本升级、降级/失败回滚、修复、卸载，以及活动租约/服务异常分支。
- 代码签名与 SmartScreen 表现；主项目许可证已按 MIT 定案（根 `LICENSE`，版权人 `2006sila`，2026）。
- 本轮**未**安装安装包、未启动/安装服务、未提权执行任何安装器；安装包仅做文件级校验（MZ 头、大小、SHA256、版本）。

---

## 9. 反馈与贡献约定

- 提交前请保证：`dotnet build -warnaserror` 与 `dotnet test -c Release` 通过；新增策略逻辑必须同时给出离线单元测试。
- 涉及风扇/温度/租约/EC 的改动，默认按更保守方向（fail-closed、更早停写、更低上限）取舍，并在 PR 说明为什么不会突破 90 °C 应急锁与租约约束。
- 文档更新不得引入本机路径、用户名或私有验证目录引用；命令示例一律使用相对路径或环境变量占位符。
