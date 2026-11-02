# Phase 5 Validation: Tray Workflow

> Current binary baseline is mode-fix: `research/evidence/current-validation/mode-fix-release-summary.json` and `mode-fix-install.json` verify unfiltered/no-SkipTests Core Release 1088/1088 (0 failed/skipped), Debug 0 warnings/errors, matching ZIP/Setup hashes and all 740 installed manifest files, plus actual same-version upgrade. Scope remains one Fan1, one calibration and one display/configuration. This documentation/directory-only round changes no source/binaries, dist or installation; these newer repository docs have not entered the existing package. A future package containing them requires rebuilding and verification. See [release-packaging.md](release-packaging.md) for exact hashes and snapshot boundaries.

## Current live evidence and limits

- `mode-fix-live-validation.json`: silence/performance/balance each exit0 and stable. Its old overall false is retained due to LastAccess metadata comparison; independent `mode-fix-cleanup-review.json` confirms mode acceptance and configuration content/permissions restoration true, no App/no lease/Guardian Running. The screenshot's intermittent expected0/got2 root cause is unconfirmed. One write followed by 1 s read-only polling and 25 fake tests is hardening, not a proven cure.
- Earlier Core `FF335A8B…`: `live-fan-control.json` passed Fixed 2500/default curve each 20 s; `live-tracking-recovery.json` recorded 20 s successful tracking but overall false from the first no-log harness blocker; subsequent `live-guardian-crash.json` actual owner-crash recovery passed. These are not fan retests on current mode-fix Core `0D635E40…`, even though mode hardening did not change fan policy.
- `live-installer-{upgrade,repair,uninstall,freshinstall}.json` verify four normal flows on an earlier snapshot; mode-fix separately passed same-version upgrade. Failure rollback and active-lease branches remain untested.
- `elevated-release-demo-smoke.json` passed actual elevated Release apphost/demo CLI, superseding historical ERROR 740. `elevated-readonly-probe.json` observed CPU 4201 MHz, LHM 59.6°C/13.6 W, GPU 35°C and Storage 38°C; board/audio/brightness remain unknown. Availability is not accuracy or setter acceptance.

## Current implementation status

The tray/open/minimize/close workflow, guarded fan/mode menus, ordered real exit and EC automatic recovery code exist. The taskbar is a top-level overlay, not reparented into Explorer. Placement follows the real appbar edge and hides on empty/inverted rectangles. Mounting/placement is event-driven (`TaskbarCreated`, display/DPI/settings messages), while values still refresh on the one-second sampling pass. Double-click means single-click and opens once. Real shell/DPI/auto-hide/Explorer behavior remains target-machine acceptance.

Implemented and Core-covered, not target-machine accepted:

- Configurable global hotkeys, bounded CLI request-id deduplication (32 entries/60 s), 45 s client budget, named-pipe command relay and machine-wide single instance; commands use existing safety gates rather than opening a second hardware controller.
- Opt-in profile automation including full executable path and command-line substring/timeout regex; command lines remain in memory, never logs/diagnostics/telemetry. The runtime status board is not persisted. Separate `ProfileAutomationHistory` execution JSONL/UI history/anonymous CSV now exist: stable hashed rule/profile IDs, time, outcome/reason and duration only (plus schema version), no names/paths/conditions; 1 MiB/3 backups and at most 500 tail lines. Hashing is not encryption.
- EC/LHM display-source selection and per-metric fallback; all fan/90°C/Guardian control continues to use raw EC `CpuTemperatureC`, not presentation fields.
- Default fan tiers 70°C/80%, 82°C/100%, 90°C/100%; opt-in RPM/s limiting with sticky sampling anchor, default-off duty-increasing `RpmFeedback`, optional curve emergency latch, lease gate and cross-process lease lock/compare-and-clear.
- Structured OSD, fixed safe glyphs, free Hex/font appearance and single-display placement. **OSD event templates are implemented**: `Core/OsdEventTemplates.cs` has 16 whitelisted event keys, v3 preferences and management UI, backward-compatible with v1/v2. Safety events (and Warning/Error messages) retain original title, body, icon and severity; duration cannot be shortened. Templates do not load external images/URIs or execute content.
- Overall CPU frequency remains available. **Windows efficiency-class grouped frequency is also implemented and wired to MainWindow/JSONL/history** through `CpuClassFrequency.cs` and `Hardware/CpuClassFrequencyReader.cs`: CPU-set EfficiencyClass/group/logical-index, no guessed Intel P/E labels, homogeneous topology `NotApplicable`, maximum 256 counters without truncation. Absolute `Processor Information\\Processor Frequency` MHz is not effective clock.
- Optional GPU temperature uses its independent GPU-only LHM reader. **Board/storage temperatures are implemented** through `AuxTemperatureSelector` and the independent `LibreHardwareMonitorAuxTemperatureSensor`, enabling Motherboard/Storage only. Either healthy group keeps the 5 s cadence; only both missing trigger 5–320 s backoff. Unknown stays null, never fabricated zero. Main-window/JSONL/history wiring exists. These metrics never enter CPU display-source selection or fan/EC/Guardian control. New grouped-frequency/auxiliary fields leave the **31-column CSV unchanged**.
- Audio/microphone/built-in brightness read and explicit write APIs/buttons exist, with probe, one write, read-back comparison and no timeout retry. Actual COM/WMI writes and device support are unaccepted; lock keys remain read-only. Audio control and brightness now reuse bounded `SingleFlightOperationWorker`: no queue/retry, timeout keeps the running slot, late results cannot cross requests and Dispose does not cancel the native call. Real brightness Read/Probe/Set reuse pure `BrightnessPanelSelection`, rejecting ambiguous targets.
- History retention/index/chart APIs, HistoryWindow wiring and the memory-budget patch have landed: default 10 MiB/3 archives, optional age based only on archive mtime, never active/schema deletion; trusted file min/max pruning with invalid-cache/change fallback scanning; independent temperature/RPM/%/W panels, °F display only, stored/exported °C unchanged. The default total scan budget is 500000 records; exhaustion explicitly warns of incomplete results and requests a narrower time range. Truncated files are not trusted, and index-skipped files consume no scan budget. This is targeted evidence, not real-machine acceptance.
- Three-window XAML and dynamically generated hotkey inputs have accessible names; static markup checks are not manual screen-reader or keyboard-navigation acceptance.

## Implemented control code — incomplete hardware acceptance

### Curve target RPM tracking

`FanCurvePoint.TargetRpm` is optional for the whole point set (all points supply it or none do). Independent tracking uses calibration feedforward, bounded added/reclaimed compensation and a temperature/ramp duty floor that takes priority. It is mutually exclusive with legacy `RpmFeedback`; emergency bypass commands 100%. Configuration/editor round-trip wiring exists. Startup calibration incompatibility removes only `TargetRpm`, preserving the temperature/Duty safety line with a warning; `ApplyCurveSettings` validates against the actual calibration before saving. The targeted TRX below is historical Core coverage; the earlier limited tracking run above is not full thermal/fault acceptance or latest-binary fan validation.

### Automatic calibration

`Core/FanCalibrationSampling`, `FanCalibrationEditor` and `MainWindow.RunFanCalibrationAsync` contain the implementation being finalized: 40→100% duty in steps of 10; raw EC temperature must start at **0<T<65°C** and remain **0<T<70°C** (70 aborts/restores). Each point settles for 3 s then takes the median of 5 unique-timestamp stable samples with RPM range ≤150; 30 s per point, 4 min overall. Cancellation, lease loss, unavailable Guardian and invalid samples fail closed. Successful EC-auto restoration is required before replacing the editor draft; Save is required to apply it. Demo does not enable sampling. See [fan-calibration.md](fan-calibration.md).

## Historical targeted evidence (preserved, not full-suite totals)

TRX files are under `research/evidence/current-validation/` in the repository (not shipped). Counts below are the actual persisted `Counters`, not inferred from test declarations. Runs can overlap; do not sum them into a suite total.

| TRX | executed | passed | failed |
| --- | ---: | ---: | ---: |
| `osd-templates.trx` | 159 | 159 | 0 |
| `cpu-class-frequency.trx` | 65 | 65 | 0 |
| `cpu-class-wiring.trx` | 88 | 88 | 0 |
| `aux-temperatures.trx` | 62 | 62 | 0 |
| `aux-freshness.trx` | 29 | 29 | 0 |
| `curve-rpm-targets.trx` | 155 | 155 | 0 |
| `auto-calibration.trx` | 47 | 47 | 0 |
| `curve-tracking-telemetry.trx` | 195 | 195 | 0 |
| `cpu-class-history-hardening.trx` | 15 | 15 | 0 |
| `curve-calibration-compatibility.trx` | 15 | 15 | 0 |
| `control-worker-boundaries.trx` | 45 | 45 | 0 |
| `automation-history.trx` | 84 | 84 | 0 |
| `brightness-selection-fakes.trx` | 57 | 57 | 0 |
| `accessibility-markup.trx` | 8 | 8 | 0 |
| `history-memory-budget.trx` | 87 | 87 | 0 |

The latest persisted automatic-calibration run is 47/47 passing. `remaining-debug-build.log` retains the initial CS9124/CS0104 failure and the corrected final successful Debug build with 0 warnings / 0 errors. `remaining-worker-regression.trx` Counters record 14 executed / 14 passed / 0 failed; runs are not summed. Documentation does not constitute execution evidence; no future Release result or pending settings-window smoke pass is claimed. These targeted records do not substitute for the current full Release evidence above or establish target-machine acceptance. Core logic tests do not execute WPF, real performance counters, LHM, Guardian service or hardware.

## Release interception and blocked read-only probe

Existing `release-gate-interception.json` records an offline `scripts/test-release-gates.ps1` fixture: fake dotnet exit **73**, publisher child **exit 1**, old fixture payload intact, no manifest/ZIP/hash generated; both existing/absent-payload cases passed. Harness exit 0 means the red-light interception fixture passed, **not real Release/Core tests**. RealDotnetExecuted/RealPublishExecuted/RepositoryPayloadAccessed are false. No repository payload was tested or replaced.

`readonly-system-probe.json` now records the single read-only retry: all 7 calls returned, host exit0 in about1.58s. CPUclass returned NotApplicable twice (one efficiency class,16LP); overall CPU frequency only established baseline3793MHz, current=null. Audio output/microphone values were unavailable and control probes false; brightness found no built-in panel instance and probe=false. The earlier blocked zero-call attempt remains in history, not the current outcome. No setters/EC/LHM/service/elevation occurred. Read-only execution with mostly unavailable values is not write or thermal-control acceptance.

`runtime-eligibility.json` records non-administrator, AMD Ryzen 7 8745H, PowerSwitchInterface class metadata present and Guardian Running, not functional control acceptance; the historical certificate harness failure remains in that file. The corrected synchronous read-only `code-signing-availability.json`/`.log` probe exited 0: CurrentUser/My and LocalMachine/My each had 0 currently time-valid explicit Code Signing EKU certificates and 0 matching HasPrivateKey certificates. Scope is only these two stores, not all certificate sources; trust/revocation/key usability were not tested, and runtime history is not overwritten. `readonly-lhm-probe.json` covers existing Debug binaries only: CPU temperature/power null, GPU twice 32°C, board/storage null; availability is not accuracy acceptance.

`hosted-demo-smoke.json` attempt 2 PASS follows one harness correction; attempt1 retains the harness failure, not a product CLI failure. Existing Debug DLL under dotnet: three status calls exit0/demo=True, activate exit0, eight distinct appended telemetry timestamps, graceful shutdown exit0/no kill. `Environment.ProcessPath=dotnet`, so this is not published-apphost identity/privilege or peer-security acceptance. The early apphost ERROR 740 is historical and superseded by the later elevated Release run above. Limited fan and normal lifecycle evidence now exists; sleep, Explorer and long-soak acceptance remains open.

The current binary's real Release gates have passed as stated above. This round updates docs only and creates no new package or release evidence.

## Historical evidence retained, not current acceptance

- Historical Debug build records include 0 warnings / 0 errors (`merged-build.log` for `--no-incremental`). These historical records do not replace the later `remaining-debug-build.log`.
- **726 passed / 0 failed / 0 skipped** was an earlier filtered targeted run. **257 passed / 0 failed / 0 skipped** was the filtered OSD/SystemIndicators/ToolkitCommand merge run in `merged-osd-system-cli.trx`. Neither is a current full-suite total.
- 2026-11-01 15:31 local static enumeration: 59 test files, 540 Fact, 57 Theory, 248 InlineData, no MemberData, about **788 declared cases**. Enumeration is not execution and is not a fresh count of the now-changing tree. Earlier 561/525/505/187/181/155/153 snapshots are historical only.
- Earlier WPF smoke covered taskbar settings, history filters and automation editor loading. Earlier 60 s read-only cross-check recorded Energy Meter power/CPU usage in 59/60 samples, non-elevated WMI access denied and driver-less LHM 0°C rejected; no driver/elevation was added then. None of this validates the newly added sensors.
- Historical 20-script AST/lifecycle checks proved parsing/order shapes, not runtime lifecycle safety.
- The historical release snapshot in `final-release-summary.json` has manifest `GeneratedUtc=2026-11-01T10:05:29.1537087Z`, 740 manifest entries/741 payload files including manifest, ZIP/Setup sidecars consistent and Setup NotSigned. This historical snapshot does not cover later source/docs changes; later payload/ZIP/installer/hash status follows the candidate's actual summary/inspection evidence.

## Still requiring acceptance

Remaining work includes installer failure rollback/active-lease branches; wider Guardian stop/crash/interleaving and read/write fault matrices; sleep/resume and elevated logon startup; at least 2 h soak, real 90°C/latches, multi-level fixed-RPM/tracking stability and automatic calibration on hardware; sensor accuracy, manual UI/a11y, single-display DPI/auto-hide/Explorer, OSD/hotkeys, full IPC/automation matrices and explicit audio/brightness setters. Earlier short fan/owner-crash/normal lifecycle passes do not cover these matrices or retest current mode-fix fan control. Sleep/resume handling and DPI declarations exist; their existence is not acceptance.

The main project still has no root LICENSE. License choice, copyright holder and signing certificate remain user decisions; third-party notices and removed-subproduct historical licensing do not authorize the main product; Setup remains NotSigned. No legal or signing claim is fabricated.

The lite subproduct and its packaging script have been removed; the main product does not depend on them. Out of scope: multiple fans/calibrations/displays, network upload/download/today traffic, keyboard backlight, vendor Fn/touchpad. The application's Windows hotkeys remain distinct and in scope.
