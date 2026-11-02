# 风扇 duty/RPM 标定

当前只维护 Fan1 的单套版本化前馈标定，供固定 RPM 与曲线可选 tracking 使用。手工编辑、预览、导入、导出、保存本身不写风扇；**自动采样入口会接管风扇**，不能再笼统称整个窗口“不写硬件”。自动标定和显式曲线 RPM tracking 已实现；自动标定尚未实机验收，tracking 有较早快照的短时实机证据，但不是完整安全验收。

## 手工编辑与持久化

先结束控制并恢复 EC 自动，打开“RPM 标定”，编辑 duty/RPM 点。添加点使用插值，仍需替换为实测值；图形预览不是可达转速保证。导入/恢复默认/编辑均留在草稿，取消丢弃；导出只写选定文件，不应用运行配置；保存才应用。

校验：2–64 点，duty 为0–100整数且严格递增，RPM为0–10000整数且非递减；需覆盖40–100%且末点100%；40% RPM>100，100% RPM更高。JSON需版本、点列表及每点完整字段，未知版本或无效导入直接拒绝，不悄悄替换默认。

默认表 `(20,1200)`、`(50,2536)`、`(80,3520)`、`(100,4083)` 在40–100%内插值约2091–4083 RPM，实际受模式/温度/BIOS/环境影响。

`%LOCALAPPDATA%\MiniProToolkit\fan-calibration.json`：

```json
{
  "version": 1,
  "points": [
    { "dutyPercent": 40, "rpm": 1800 },
    { "dutyPercent": 70, "rpm": 3000 },
    { "dutyPercent": 100, "rpm": 4500 }
  ]
}
```

同目录临时文件原子替换，旧文件备份为 `.bak`。缺失加载默认；损坏/未知版本/无效点回退默认并警告，不自动覆盖原文件。控制仍接管或恢复未完成时不得应用标定，保存失败回退内存旧控制器。

## 自动实测采样：已实现，待实机验收

`Core/FanCalibrationSampling.cs` 是纯状态机；`FanCalibrationEditorWindow` 与 `MainWindow.RunFanCalibrationAsync` 负责进度/取消、串行硬件门、Guardian/租约及恢复。**不是已实机或已最终验收**。

- 起始必须在 EC 自动、无控制/恢复/紧急占用，Guardian可用，原始EC温度 **0<T<65°C**。
- duty固定为 **40、50、60、70、80、90、100%**。过程温度必须 **0<T<70°C**，**70°C即中止并请求恢复EC自动**，不是等到90°C才停；温度不取显示源/LHM。
- 每点先至少 **3秒 settle**，再收集 **5个唯一时间戳**新鲜样本；RPM极差 **≤150** 时取中位数。重复时间戳不计入稳定窗口、不冒充新样本；坏样本/过期/倒退拒绝。
- **每点30秒、全程4分钟**。cancel、失租约、Guardian停止、无效温度/RPM、读写失败等fail closed，停止进一步标定写入并走恢复；同步硬件调用在途时不因超时而提前释放控制所有权。
- 只有采样完成且**恢复EC自动成功**才返回结果替换editor草稿；失败/取消/恢复失败不替换。此时仍未应用到配置，必须显式**保存**；取消草稿保留原标定。
- demo不创建/开放自动采样路径；手工保存可保存配置，但不产生硬件采样证据。

## 曲线显式目标 RPM

`FanCurvePoint.TargetRpm` 为整条曲线全点可选（全部提供或全部不提供），支持配置/editor往返。独立 `FanCurveRpmTrackingController` 以标定前馈加有界补偿，低于目标小步增加、高于目标只回收补偿；温度曲线/ramp duty floor优先，不得为追目标而降低安全散热。与旧默认关闭、只增duty的 `RpmFeedback` 互斥；90°C紧急直接100%。此实现有 Core 覆盖及较早 Core `FF335A8B…` 快照的 tracking 20 秒成功记录；不是最新 mode-fix Core `0D635E40…` 的风扇重测，也不代表多档/长期/故障验收。

启动时若当前标定与显式RPM曲线不兼容，仅移除 `TargetRpm` 并警告，保留温度/Duty安全线；`ApplyCurveSettings` 先以实际标定验证，再保存设置，避免持久化不可运行的显式目标。

## 当前二进制基线与文档边界

`research/evidence/current-validation/mode-fix-release-summary.json`、`mode-fix-install.json` 记录真实无 filter/无 SkipTests 的 Release 1088/1088、Debug 0 warning/0 error、ZIP/Setup hash 与 740 项已安装 manifest 全部一致及实际同版本升级成功。本轮仅文档/目录整理，不改源码/二进制、不重新打包；仓库新文档未进入现有包或安装副本。未来发含新文档的包需重建，详见 [发布说明](release-packaging.md)。

较早快照 `live-fan-control.json` 的 Fixed 2500/default curve 各 20 秒通过；`live-tracking-recovery.json` 的 tracking 20 秒成功，总 false 为首次无日志 harness 阻断；随后 `live-guardian-crash.json` 真正 owner-crash 恢复 passed=true。其 Core 均为 `FF335A8B…`，不能声称覆盖 mode-fix `0D635E40…`。单 Fan1、单套标定范围不变，lite 子产品及其打包脚本已移除，主项目不依赖它们。

## 历史定向证据（保留，不作为当前全量总数）

本次只读核对 `research/evidence/current-validation/` TRX Counters：`curve-rpm-targets.trx` **155/155 passed**、`curve-tracking-telemetry.trx` **195/195**、`curve-calibration-compatibility.trx` **15/15**、`auto-calibration.trx` **47/47**，均failed0。不同定向运行可能重叠，不相加为独立总数，不代表最终统一/真实Release/硬件验收。历史统一Debug `final-debug-build.log`：exit0、0warning0error；本次未运行构建/测试/脚本/App/硬件/服务或提权。

历史Core配置/控制器测试、无硬件WPF演示检查只保留原范围。真实自动标定、多档固定RPM/tracking稳定性、温度边界、取消/失租约/Guardian/恢复故障、文件对话框和至少2小时运行仍需目标机留证。当前已验包保持不动；本次新文档不是已随包发布内容。详见 [验证记录](phase-5-validation.md) 与 [发布说明](release-packaging.md)。
