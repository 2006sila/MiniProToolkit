# MiniProToolkit Product Roadmap

This is the consolidated product plan for the Lecoo MINI PRO-AHP control center. The project is intended to replace the vendor ControlCenter and ImagesShow workflows, not only to provide a fan test utility.

> Current binary baseline is the verified mode-fix snapshot; exact evidence/hashes and earlier live-test boundaries are in [release-packaging.md](release-packaging.md). This round updates docs/directory organization only, not source/binaries, dist or installation. These newer repository docs are not yet in the existing package; a future package containing them must be rebuilt and verified. The lite subproduct and its packaging script have been removed; the main product does not depend on them. Scope remains one Fan1, one calibration and one display/configuration.

## Stage 0: Research and contract

- Preserve original installers, extracted files, reverse-engineering evidence and measured hardware data.
- Confirm the ACPI-WMI contract and performance mode values.
- Confirm CPU temperature, packed fan RPM and FanDuty semantics.
- Keep hardware access on `root\\WMI:PowerSwitchInterface`; do not access EC MMIO or modify firmware.

Status: complete.

## Stage 1: Core monitoring and control

- WPF control center with CPU temperature, real fan RPM, CPU usage, memory usage and capability status.
- Performance mode read and switching: Balance, Performance and Silence; write operations must read back the mode and use the read-back value as final state.
- EC automatic fan control, fixed duty/fixed RPM control and full-speed control.
- Duty-to-RPM calibration, target RPM display and control-state display.
- Serializes hardware operations and avoids hardware writes during startup.
- Demonstration mode that never accesses hardware.
- Hardware capability detection for optional exposed features; unsupported controls stay hidden. **Historical planning only:** keyboard backlight and vendor Fn / touchpad control are explicitly excluded from the current product by user decision; the retained probe/flag code is read-only capability detection, not a backlog item. The app's own Windows global hotkeys are a separate feature and remain in scope.
- Windows system audio/microphone/built-in brightness reads and explicit write APIs/buttons exist (probe, one write, read-back; no timeout retry); actual COM/WMI/device support remains unaccepted. Lock keys stay read-only.

Status: the current UI has the core temperature/RPM/usage/mode controls, calibrated fixed-RPM control, bounded actual-RPM feedback correction, target-error display and fan fault recovery states. A read-only capability probe and diagnostics presentation are implemented. Keyboard backlight and vendor Fn / touchpad controls are **excluded from the current product** (the probe flags remain read-only capability detection, not a pending "reversible write/readback" work item).

Calibration status: the editable, versioned calibration covers 40–100% duty and is shared by fixed-RPM feedforward and feedback controllers. Automatic sampling is implemented, awaiting hardware acceptance: 40→100 step10, start 0<T<65°C, throughout 0<T<70°C (70 abort/restores), 3 s settle + median of 5 unique stable samples with range≤150 RPM, 30 s/point and 4 min total; cancel/lease/Guardian/bad sample fail closed, restoration before draft replacement, Save before application, demo disabled. Target-machine acceptance remains pending.

## Stage 2: Taskbar hardware status

- Embedded taskbar status window.
- Left-click and double-click open the control center.
- Right-click menu for open, mode switching, fan control, settings and exit.
- Selectable and reorderable display items: CPU temperature, fan RPM, mode, CPU usage, memory usage, CPU frequency and control state.
- Compact and vertical display layouts, plus hover details showing temperature, RPM, target duty/RPM, mode and WMI state.
- Explorer re-attachment and DPI-aware sizing.
- One supported taskbar on the current single-display device, including orientation and relocation handling. Multi-display/secondary-taskbar layouts are outside the current product scope.
- Taskbar enable/disable control.
- Validation on the supported display: taskbar orientations, Explorer restart, current-display DPI scaling, automatic-hide layout and relocation. **Historical product-plan mention:** secondary-monitor validation is outside the current single-display scope, not backlog or release blocking.

Status: display-item selection/order, compact/vertical layouts, hover details, Explorer re-attachment, DPI-aware placement and the double-click compatibility rule (a double click is the same gesture as a single click and only opens the window once, `TaskbarClickGesture`) are implemented for the one supported display/taskbar. The live taskbar, Explorer restart, automatic-hide layout and current-display DPI validation remain target-machine work. Per-display customization, secondary-taskbar layouts and multi-display behavior are historical plan references only: they are outside the current scope, not backlog or release blockers.

## Stage 3: Safety and recovery

- Safety fan curve with minimum duty, maximum duty, emergency temperature, hysteresis and slope limiting.
- Fixed RPM mode backed by calibrated duty values.
- Interpolated temperature-to-target-RPM/duty curves.
- Separate ramp-up and ramp-down thresholds to model thermal hysteresis.
- Separate RPM increase/decrease rate limits and calibrated actual-RPM feedback; curve RPM feedback exists as an optional default-off setting and can only increase duty.
- Configuration persistence with version validation and default fallback.
- Guardian Windows service with lease, heartbeat, PID/start-time validation and EC automatic recovery.
- Restore on write failure, stale sensor data, Guardian stop, process crash and forced termination.
- Explicit service install, start, stop, uninstall and manual recovery scripts.

Status: safety curve and Guardian recovery are implemented; earlier real crash-recovery validation passed. Fixed-RPM control now has independent control/presentation cadence, explicit read/control separation, invalid-input and lease checks, latched fault context and restore-only retry after failures. Curve disable failures stop output and retain recovery state. These latest paths are simulated-test covered; updated real-machine fault acceptance remains pending.

## Stage 4: Real thermal validation

- Idle smoke test.
- Controlled low-load run.
- Higher-load emergency stop.
- Verify temperature-to-duty response, target-RPM response, actual-RPM response, lease lifecycle and EC automatic recovery.
- Verify emergency levels and response policy: conservative full-speed escalation, sensor failure recovery, WMI failure recovery and stalled-fan detection.
- Repeat failure and recovery tests after curve changes.
- Record temperature, actual RPM, target RPM, duty, mode and error events to CSV/JSONL.
- Provide historical charts and exportable test data.
- Longer soak testing across realistic workloads and ambient conditions.

Status: historical smoke/low-load/bounded-load evidence is retained. Later `live-fan-control.json` passed Fixed 2500 and default curve each for 20 s; `live-tracking-recovery.json` recorded 20 s tracking success (overall false due to the initial no-log harness blocker), followed by actual owner-crash recovery passed in `live-guardian-crash.json`. These use earlier Core `FF335A8B…`, not current mode-fix Core `0D635E40…`; unchanged fan policy is not a latest-binary retest. At least 2 h soak, multi-level stability and full fault/thermal acceptance remain.

## Stage 5: Daily-use experience

### 5.1 Tray and lifecycle

- Tray icon, open/restore, minimize-to-tray and real exit.
- Tray commands for performance modes and EC automatic recovery.
- Branded icon, useful status tooltip and state-aware menu items.
- Optional start-with-Windows setting with explicit elevation/registration behavior.

Status: tray and taskbar menus now expose guarded mode/fan commands, fixed-RPM presets, curve entry and EC automatic recovery. Fan items are disabled on unavailable Guardian, faults, active recovery or unsafe status; full speed remains a protective option at high temperature while fixed RPM/curve are gated below 90°C. Display font/size/colors are configurable. Startup registration is **implemented** (`src/MiniProToolkit.App/StartupRegistration.cs` plus the installer's optional `startup` task), so what remains is target-machine acceptance of the elevated logon startup path, not the feature itself.

### 5.2 OSD and shortcuts

- Temporary on-screen display for performance mode changes.
- OSD for fan curve enabled/disabled, fixed RPM, manual duty and EC automatic recovery.
- OSD for temperature, actual RPM and target RPM when control state changes.
- Non-blocking, DPI-aware overlay with timeout and placement on the one supported display. **Historical product-plan mention:** multi-monitor placement is outside the current scope, not backlog or release blocking.
- Avoid duplicate OSDs when several commands arrive quickly.
- Global shortcuts for Silence, Balance, Performance, EC automatic and full speed.
- Vendor Fn-key replacement: **excluded from the current product** (user decision). The vendor Fn event path is not investigated further; Windows-level global hotkeys are not a substitute claim for vendor Fn, and shortcut support is not excluded together with it.

Status: OSD events are coalesced and positioned on the supported display's work area with current-display DPI handling; global Ctrl+Alt hotkeys and startup-hidden mode are implemented. Current-display OSD placement, Explorer restart and DPI behavior remain target-machine acceptance items; mixed-DPI, display hotplug, multi-monitor placement and per-display settings are historical/product-plan references only, outside the current scope and not release blockers. Vendor Fn event support is **excluded from the current product** (no research, no implementation, no acceptance). Shortcut rebinding is implemented and Core/direct-test covered but **not** target-machine accepted: defaults stay `Ctrl+Alt+1/2/3/A/F`, the rebindable set is whitelisted in `MiniProToolkit.Core/HotkeySettings.cs` (Ctrl/Alt/Shift/Win + `A-Z`/`0-9`/`F1-F12`, no modifier-free/Shift-only/reserved/duplicate combinations) and persisted to `hotkeys.json`, and a registration that is already owned elsewhere only degrades that one action.

### 5.3 Fan curve editor and profiles

- Graphical point editor for temperature and target RPM/duty points.
- Edit separate ramp-up and ramp-down thresholds.
- Edit minimum duty, maximum duty, emergency thresholds, hysteresis and up/down slope limits.
- Show calibrated duty-to-RPM information and warn when the requested RPM cannot be reached.
- Validate unsafe curves before saving, including low-duty and non-monotonic curves.
- Named profiles such as Quiet Office, Balanced, Performance and Custom.
- Import/export/reset configuration.
- Manual profile switching and opt-in AC/battery, CPU-load, time-window, process-start and process-exit automation are implemented.
- Apply profile without losing Guardian ownership and recover safely on failure.

Status: persistence, the curve editor and profile manager are implemented. Profile automation supports AC/battery, CPU load, time-window, process-start and process-exit rules, priorities, stable tie-breaking, debounce for sustained conditions, pause/resume and explicit opt-in (default disabled). Process rules can additionally match the **full executable path**, a **command-line substring** and a **command-line regex** (`ProfileAutomationProcessCondition` with a background `ProfileAutomationProcessSampler`): path must be a drive-letter or UNC full path, the regex is case-insensitive with a timeout, substring and regex are mutually exclusive, invalid patterns are rejected on save, and command lines stay in memory only (never written to logs, diagnostics or telemetry). Automatic profile application uses the same serialized safety and rollback path as manual Apply; manual control, recovery faults, stale readings and emergency full speed suppress automation. Whole-point-set optional TargetRpm, independent bounded add/reclaim tracking with temperature/ramp duty-floor priority, mutual exclusion with legacy RpmFeedback, emergency 100% and configuration/editor round trip are implemented, with limited earlier tracking smoke but not comprehensive thermal/fault acceptance. Startup calibration incompatibility strips only TargetRpm, retaining temperature/Duty safety points with a warning; ApplyCurveSettings validates against actual calibration before saving.

### 5.4 Shell and display hardening

- The current taskbar, including vertical orientation and relocation.
- Explorer restart recovery.
- Current-display DPI and scale changes.
- Window placement on the single supported display.
- **Historical product-plan mention:** secondary-taskbar and multi-monitor behavior are outside the current scope, not backlog or release blocking.
- App shutdown, sleep/resume and service restart behavior.

Status: implemented in source, not yet accepted on a target machine. Sleep/resume has a documented policy (`PowerTransitionPolicy` only records a suspend and never writes EC/fan, `ResumeRefreshCoalescer` merges the resume into one refresh) wired to `SystemEvents.PowerModeChanged`, and per-monitor DPI awareness is declared in `src/MiniProToolkit.App/app.manifest` (`dpiAware=true/pm`, `dpiAwareness=PerMonitorV2, PerMonitor`); taskbar mounting/placement is event-driven (`TaskbarCreated`/`WM_DISPLAYCHANGE`/`WM_DPICHANGED`/`WM_SETTINGCHANGE`). No real suspend/resume cycle or current-display Explorer/DPI/automatic-hide run exists yet. Mixed-DPI, hot-plug, secondary-taskbar and multi-display behavior are outside the current single-display scope and are not acceptance items.

### 5.5 Operational quality and telemetry

- Settings and diagnostics view.
- Clear administrator/elevation state.
- Readable application and Guardian logs.
- Recovery action and status display when WMI or service access fails.
- WMI error and recovery event history.
- Live charts for CPU temperature, actual RPM, target RPM and duty.
- CSV/JSONL recording and export.
- Accessibility, keyboard navigation and localization cleanup.

Status: history window and event center provide time/category/keyword filtering, trend zoom/pan and exports. Bounded rotating JSONL diagnostic journals retain app control results and Guardian recovery actions; diagnostics include the latest readable recovery records and a redacted summary. Incompatible CSV headers are archived intact before new-schema rows are appended. Event export still needs user inspection before sharing.

## Stage 6: Packaging and release

- Publish self-contained x64 application.
- Package the main app and Guardian service together.
- Installer with install, upgrade, repair and uninstall flows.
- Service installation and rollback handling.
- Start menu/desktop shortcuts and optional startup registration.
- Preserve existing configuration during upgrades.
- Stop service safely before replacing locked Guardian binaries.
- Verify clean uninstall, service removal, lease cleanup and EC automatic recovery.
- Versioning, release notes, checksums and end-user recovery guide.

Status: mode-fix full Release/publish/installer gates and actual same-version upgrade passed. Earlier `live-installer-{upgrade,repair,uninstall,freshinstall}.json` verify four normal flows on their snapshot; failure rollback and active-lease branches remain untested. This round does not repackage; the newer docs are not in existing dist or installed files.

## Current position

The project is between Stage 5 and Stage 6. Recent code adds guarded shell controls, configurable single-taskbar styling, current-display OSD handling, a read-only GPU temperature (separate optional LHM GPU group, JSON-only, never in the EC control path), a runtime-only automation status board, opt-in profile automation including process path / command-line / regex conditions, persistent diagnostics, an ordered exit sequence, a curve-status line with JSON-only telemetry fields, and a verified mode-fix ZIP/installer pair. Source and binaries remain at that snapshot; only newer repository docs await future packaging. Remaining acceptance includes lifecycle failure branches, supported-display behavior, long soak/fault validation, sleep/resume and wider fan-control matrices; mode-fix three-mode write/readback passed without establishing the earlier intermittent mismatch's root cause; system audio/microphone/built-in brightness explicit write code exists but real COM/WMI writes remain unaccepted; lock keys stay read-only. Multi-display, secondary-taskbar, per-display and cross-screen behavior are historical plan references, not release gates.

Scope decisions and open work (do not describe these as done):

- **Excluded by user decision, not backlog**: network upload/download and today-traffic, keyboard backlight, vendor Fn / touchpad control. The app's own Windows global hotkeys stay in scope.
- **Implemented, Core unit-test covered, not target-machine accepted** (earlier documentation described these as still being developed; they are neither "fully done" nor "plan only"): single-fan RPM jitter suppression (sticky sampling anchor plus the opt-in RPM/s rate limit, `RpmRateLimit` still `null` by default), the calibrated default-off curve RPM feedback (`RpmFeedback`, which can only increase duty) and curve 90 °C latch (`LatchCurveEmergency`), the cross-process short-held lease lock with compare-and-clear, the event-driven taskbar appbar refresh (the displayed values are still refreshed by the 1 s sampling pass; only mounting/placement moved to events), the configurable OSD appearance (free hex colours and arbitrary font family), and the runtime automation status board. Target-machine acceptance for all of them is outstanding.
- Newly implemented, targeted-Core covered, not target-machine accepted: OSD 16-event whitelist/templates/v3 management (safety title/body/icon/severity preserved, duration cannot shorten); Windows EfficiencyClass CPU frequency with MainWindow/JSONL/history wiring (no Intel P/E guessing, homogeneous NotApplicable, 256 counters, absolute Processor Frequency MHz not effective clock); independent Motherboard/Storage LHM temperatures (either valid: 5 s; both missing: 5–320 s backoff; unknown not zero). CSV stays 31 columns, no fan/EC/Guardian control use.
- Current mode-fix evidence records Debug 0 warnings/errors and real unfiltered Core Release 1088/1088, no SkipTests, matching ZIP/Setup/installed manifest. Three-mode live acceptance and configuration restoration are independently true in `mode-fix-cleanup-review.json`; the original harness false from LastAccess comparison is retained. Earlier elevated apphost/demo CLI passed (historical ERROR 740 is superseded), with read-only CPU/LHM/GPU/storage availability observations, not sensor accuracy or full UI/IPC acceptance. This round runs no build/test/script/App/hardware/service/install and produces no package. Main root LICENSE/copyright holder/signing decision remain pending and Setup is NotSigned.
