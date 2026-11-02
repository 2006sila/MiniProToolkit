# MiniProToolkit

针对 Lecoo MINI PRO-AHP 的第三方 Windows 控制台，目标是替代 ImagesShow 和 ControlCenter，并提供任务栏硬件状态显示。

## 当前阶段与范围

项目处于阶段 5 功能完善和阶段 6 发布准备之间，见 [产品路线](docs/product-roadmap.md)。当前只控制 **Fan1、单套标定、单显示器**及一套任务栏/OSD 配置；Fan2 若有读数仅只读兼容，多风扇、多套标定、多屏/副屏/跨屏配置不属 backlog 或发布阻塞。

**当前已验二进制基线为 mode-fix 快照，尚非正式发布就绪。** 本轮仅整理文档与目录，不改源码/二进制，不重新打包；仓库文档新于该包，未进入现有 ZIP/Setup 或已安装副本。未来若发布包含新文档的包，须重新构建并验证，不能把本次文档更新声称为已经随包发布。

网络上传/下载与今日流量、键盘背光、厂商 Fn/触摸板明确不做，不含隐藏采集；Windows 应用全局快捷键不等于厂商 Fn，仍在范围内。`lite/` 子产品及 `scripts/package-lite.ps1` 已移除，主项目无 lite 依赖。

## 已实现的功能

以下是源码实现状态，不等于当前全量/实机/发布验收：

- ACPI-WMI 模式读取/切换，原始 EC CPU 温度、真实 Fan RPM、EC 自动/满速；只走现有 `root\\WMI:PowerSwitchInterface`，不访问 EC MMIO、不改固件。
- 固定 RPM 标定前馈、有限反馈、误差/故障恢复状态；控制周期独立于显示，异常停写/恢复重试。单套 duty/RPM 标定编辑、预览、JSON 导入导出和版本化持久化。
- Guardian 服务租约与恢复、跨进程短持有锁/compare-and-clear、曲线租约门禁。固定 RPM 达 90°C 锁定 100%，只有显式恢复 EC 自动解除；普通故障不清锁。
- 安全温度曲线最低 40% duty、迟滞/斜率限制，默认节点 45/40–90/100、分级温度 70°C/80%、82°C/100%、90°C/100%。RPM/s 限制默认关闭、粘滞采样锚点已实现；旧 `RpmFeedback` 只增 duty、默认关闭，`LatchCurveEmergency` 也默认关闭。曲线状态行、独立曲线紧急 OSD、JSON-only 状态字段已接线。
- 任务栏顶层覆盖层（不 reparent Explorer）、显示项/排序/字体/颜色、真实 appbar 边方向与空矩形隐藏；挂载/定位事件驱动，值仍每秒采样。双击＝单击只开窗一次，右键有显示项/安全控制入口。托盘常驻、开窗/隐藏/真实退出和分阶段释放已实现。
- CPU/内存占用、Energy Meter 封装功耗、CPU 整体频率只读展示；可选 LHM CPU 对照/主显示源逐项回退、独立 GPU-only 温度读取。所有显示源设置均不改变风扇/保护真值：仍用原始 EC `CpuTemperatureC`。
- **Windows 效率等级 CPU 分组频率**：`Core/CpuClassFrequency.cs` + `Hardware/CpuClassFrequencyReader.cs`，按 CPU-set `EfficiencyClass`/group/logical-index 聚合平均/最小/最大 MHz 及样本数，不猜 Intel P/E；同构 `NotApplicable`，异构最多 256 counters、超限不截断估算。读取绝对 `Processor Frequency` MHz，**不是 effective clock**；MainWindow/JSONL/history 已接线。
- **主板/存储只读温度**：`AuxTemperatureSelector` + 独立 `LibreHardwareMonitorAuxTemperatureSensor`，只启用 Motherboard/Storage。任一组有效保持 5 秒节奏，两组均缺失才退避 5–320 秒；未知不造 0。显示/JSONL/history 已接线，不进 `CpuDisplaySource`、风扇/EC/Guardian 控制；新增频率分组/辅助温度保留 **CSV 31 列不变**。
- **OSD 事件模板**：`Core/OsdEventTemplates.cs` 的 16 事件 key 白名单、`osd.json` v3 偏好及管理 UI，兼容 v1/v2。结构化消息、主题/事件合并、自由 Hex/字体/九宫格外观和固定 glyph 已实现。安全事件（以及 Warning/Error）原标题、正文、图标、严重度不可删除/替换，duration 不可缩短；可追加有界脱敏说明，不加载图片/URI或执行内容。
- 默认 `Ctrl+Alt+1/2/3/A/F` 全局快捷键可重绑、持久化，拒绝保留/重复/不安全组合，注册失败按动作降级。进程完整路径/命令行子串/有超时正则自动化条件已实现，命令行仅驻内存；运行期状态板不落盘。另有去标识自动化执行历史 JSONL、UI 历史及匿名 CSV：只存稳定哈希 ID、时间、结果/原因码和耗时，1 MiB/3 份备份，最多读取尾部 500 行；哈希不是加密，不存名称、路径或条件。
- CLI 白名单动作经仅当前用户管道转发给已有主实例，受原安全门约束；机器级单实例、request-id 去重 32 条/60 秒、45 秒应答预算已实现。退出码 0/2/4/5/6/7/8；无主实例不启动新硬件控制实例。
- 历史/诊断/脱敏摘要及应用/Guardian 有界轮转事件。历史保留设置、文件级时间索引与独立单位趋势面板已落地：默认 10 MiB/3 归档，可选按归档 mtime 清理，不清活跃文件/schema 备份；可信 min/max 才裁剪文件，失效回退扫描；°F 仅展示，不改 °C 存储/导出。默认总扫描预算 500000 条，达到上限明确警告不完整/提示缩小时段；截断文件不 trusted，索引 skip 不耗预算。`history-memory-budget.trx` 已落盘 87/87、failed 0，仅为定向 Core 证据。
- 音量/麦克风/内建亮度读及显式写 API/按钮已实现，先 probe、只写一次并读回，超时不重试。音频控制与亮度复用有界 `SingleFlightOperationWorker`；超时不释放运行中槽位，late result 不串请求，Dispose 不取消底层调用。亮度真实 Read/Probe/Set 复用纯选择 policy，歧义 fail closed；真实 COM/WMI 写入与设备支持未验收，锁定键只读。
- 三个窗口 XAML 与任务栏设置动态热键输入已补可访问名称；8 项静态标记测试不等于人工读屏/键盘导航验收。
- 自包含 x64 发布、多道门禁与 Inno Setup 安装器已完成 mode-fix 构建及实际同版本升级；较早快照的安装/升级/修复/卸载正常流程也已通过，失败回滚及活动租约分支仍未验。

### 已实现、验收边界仍有限

- 曲线 `FanCurvePoint.TargetRpm` 全点可选（全有或全无）；独立 tracking 用标定前馈与有界增加/回收补偿，温度曲线/ramp duty floor 优先；与旧 `RpmFeedback` 互斥，紧急 100%，配置/editor 往返已接线。启动时标定不兼容会只移除 `TargetRpm`、保留温度/Duty 安全线并警告；`ApplyCurveSettings` 先按实际标定校验再保存。
- 自动标定 `Core/FanCalibrationSampling`、editor 与 `MainWindow.RunFanCalibrationAsync`：40→100、步长10；起始 0<T<65°C、过程 0<T<70°C（70 即中止恢复）；每点 3 秒 settle 后收集 5 个唯一稳定样本，极差≤150 RPM 取中位数；30秒/点、全程4分钟；cancel/失租约/Guardian/坏样本 fail closed。恢复成功才替换草稿，保存才应用，demo 不开放。详见 [标定](docs/fan-calibration.md)。

## 证据与剩余阻塞

证据位于仓库 `research/evidence/current-validation/`（不随包分发）：

- `mode-fix-release-summary.json`、`mode-fix-install.json`：真实无 filter/无 SkipTests 的 Core Release **1088/1088，0 failed/0 skipped**；Debug **0 warnings/0 errors**；新 ZIP/Setup SHA-256 与 sidecar 相符、740 项 manifest 与实际安装全部匹配，同版本升级成功。详见 [发布说明](docs/release-packaging.md)。
- `mode-fix-live-validation.json` 的三模式请求均 exit0、目标读数稳定；其旧 harness 总结果 false 来自 LastAccess 元数据。独立 `mode-fix-cleanup-review.json` 确认模式验收与配置内容/权限恢复均 true，终态无 App、无 lease、Guardian Running。此前截图中的偶发 expected0/got2 根因未确证；一次写入后 1 秒只读轮询的硬化及 25 项定向测试、三模式成功不代表根治。
- `live-fan-control.json`、`live-tracking-recovery.json`、`live-guardian-crash.json`：较早 Core `FF335A8B…` 快照完成 Fixed 2500/default curve 各 20 秒、tracking 20 秒及真正 owner-crash 恢复。tracking 总结果 false 是首次无日志的 harness 阻断；后续 owner-crash passed=true。这些不是最新 mode-fix Core `0D635E40…` 上的重测。
- `live-installer-{upgrade,repair,uninstall,freshinstall}.json`：较早快照四项正常生命周期通过；mode-fix 另有实际同版本升级证据，不能外推失败回滚/活动租约分支。
- `elevated-release-demo-smoke.json`：管理员下真实 Release apphost/demo CLI 通过，早期 ERROR 740 不再是当前阻塞。Release 只读探测有 CPU 4201 MHz、LHM 59.6°C/13.6 W、GPU 35°C、Storage 38°C；主板/音频/亮度仍未知，读到数值不等于准确性或写控制验收。

剩余包括自动标定实机、至少 2 小时 soak、多档稳定性与故障/90°C、睡眠/登录启动、人工 UI/a11y、单屏 Shell/OSD/热键、自动化/IPC 完整矩阵和安装失败分支。主项目仍**无根 LICENSE**，版权主体与签名决策待定，Setup 为 **NotSigned**；第三方声明和已移除子产品的历史许可证都不代表主项目授权。历史计数与早期失败仅在专门历史区保留，不当作当前阻塞或新发布结果。

## 文档与目录

目录整理已移除 lite 子产品、其打包脚本、五个项目的 bin/obj 和 artifacts 重复副本；保留主产品源码、其他脚本、现有 dist 和全部 research 历史 evidence。清理构建缓存不等于重新构建或改变已安装二进制。

- [架构与显示/控制隔离](docs/architecture.md)
- [历史与诊断](docs/history-and-diagnostics.md)
- [方案自动化与安装器](docs/profile-automation-and-installer.md)
- [风扇控制加固](docs/fan-control-hardening.md)
- [阶段3](docs/phase-3-validation.md)、[阶段4](docs/phase-4-validation.md)

仓库内部状态清单 `docs/gap-list.md`、规划 `docs/console-product-plan.md` 与许可证决策文档不随包分发。`src/` 产品源码、`tests/` 测试、`scripts/` 开发/发布脚本、`research/` 原安装包/解包/证据与研究工具；文档描述不替代对应执行证据。

## 构建与运行（操作说明，不构成执行证据）

需要 .NET 9 SDK、Windows 11 x64：

```powershell
dotnet restore .\MiniProToolkit.sln
dotnet build .\MiniProToolkit.sln -c Debug
```

真实运行需管理员权限以访问本机 WMI 接口：

```powershell
dotnet run --project .\src\MiniProToolkit.App\MiniProToolkit.App.csproj -c Debug
```

不访问硬件的演示模式（不启用安全曲线/自动标定）：

```powershell
dotnet run --project .\src\MiniProToolkit.App\MiniProToolkit.App.csproj -c Debug -- --demo
```
