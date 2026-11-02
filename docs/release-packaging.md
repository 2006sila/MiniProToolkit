# Release Packaging and Lifecycle

> Current state: **no new verified binary snapshot**. The `mode-fix-*` records below are a **historical** verified snapshot: their Core 1088/1088 run, ZIP/Setup hashes and 740-entry manifest describe that snapshot's source/binary only and are superseded for the current tree. This round changes `scripts\package-common.ps1` (ProgramData purge whitelist, required payload list) and repository documentation only: no App/Guardian/installer source logic, rebuild, republish, real publish or installation ran. The current source therefore has no new full-suite Release result and no real publish/install evidence yet - those runs are pending and are not claimed here. These newer repository docs are not in the existing ZIP/Setup or installed copy. A future package containing them must be rebuilt and verified; the existing evidence summaries are not rewritten to pretend a new release occurred.

## Historical binary baseline (mode-fix snapshot) and evidence boundaries

The `mode-fix-*` records below are historical: they describe that snapshot's source/binary, not the current tree, which includes this round's `package-common.ps1` change and has no matching full-suite/publish/install result yet.

Evidence files below are repository references under `research/evidence/current-validation/`, not shipped links.

- `mode-fix-release-summary.json`: Debug exit0, 0 warnings/errors; real unfiltered Core Release **1088/1088**, 0 failed/skipped, no `-SkipTests`; publish and installer build exit0. `mode-fix-artifact-inspection.json` verifies 740 manifest files, 741 payload/ZIP entries, no illegal separators or duplicates, and matching SHA-256 sidecars.
- ZIP SHA-256: `4052D911B19BCA22C9530C377748AB8AB199183E8F1CD1A004F49F0D924FF182`; Setup: `1E42B1D5CFBEF1263478FFED219533BB9704FC4E9ACCF0F9DFBB3B38426F8ADA`. Setup is **NotSigned**. ZIP Authenticode UnknownError is not a signature claim.
- `mode-fix-install.json`: actual same-version upgrade passed, all 740 installed manifest files match, user configuration preserved. Installed App/Guardian Core SHA-256 is `0D635E403A3F1A632267D8546BF88208ADAD22DD54C204613764E95B16195E66`.
- `mode-fix-live-validation.json`: silence/performance/balance requests each exit0 with stable targets. Its original overall false is retained because the old harness compared LastAccess metadata. `mode-fix-cleanup-review.json` independently records `modeSwitchAcceptance=true`, `configurationContentAndPermissionsRestored=true`, no App/no lease/Guardian Running. Reading/hashing can change LastAccess; the old evidence is not overwritten.
- The earlier screenshot's intermittent expected0/got2 mode readback has **no confirmed root cause**. One setter followed by bounded 1 s read-only polling, 25 targeted fake tests and the three-mode live success demonstrate hardening, not a proven cure or firmware-delay diagnosis.
- `live-installer-upgrade.json`, `live-installer-repair.json`, `live-installer-uninstall.json`, `live-installer-freshinstall.json`: four normal flows actually passed on an earlier snapshot; failure rollback and active-lease branches remain untested. Only same-version upgrade is separately confirmed on mode-fix.
- `live-fan-control.json`: Fixed 2500 RPM and default curve each passed a 20 s run. `live-tracking-recovery.json`: tracking ran successfully for 20 s, while its overall false reflects the first no-log harness blocker. `live-guardian-crash.json`: subsequent actual owner-crash recovery passed. These used earlier Core `FF335A8BDBEB28442E340538BCF2ECB0D883181172F7B6EE9F4073DC6E709FAE`, **not** the mode-fix Core above. Mode hardening did not change fan policy, but that is not a fan retest on the latest binary.
- `elevated-release-demo-smoke.json` verifies real elevated Release apphost/demo CLI; historical ERROR 740 is superseded, not a current blocker. `elevated-readonly-probe.json` observed CPU 4201 MHz, LHM CPU 59.6°C/13.6 W, GPU 35°C and Storage 38°C; board/audio/brightness remain unknown. These are availability observations on their recorded snapshot, not sensor-accuracy or setter acceptance.

## Current scope and authorization

One controlled Fan1, one calibration and one display/configuration. Fan2 selection, multi-fan, multi-calibration and multi-display behavior are outside scope. The supported display's DPI, auto-hide and Explorer re-attachment remain target-machine checks. The lite subproduct and its packaging script have been removed; the main project does not depend on them. Network upload/download/today traffic, keyboard backlight and vendor Fn/touchpad are excluded; application Windows hotkeys are not vendor Fn.

Necessary Release gates were executed for the mode-fix binary snapshot (**historical**). For the current tree they are **not** re-executed in this round: the new full-suite Release run, the real publish/inspection chain and the installer build are still pending. The newer docs and this round's `package-common.ps1` change have not been packaged. An offline interception fixture alone is not a real Release pass; older release counts are retained only in the historical section.

## Test gate and existing interception evidence

`publish-release.ps1` executes the following before deleting/recreating the payload:

```powershell
dotnet test .\tests\MiniProToolkit.Core.Tests\MiniProToolkit.Core.Tests.csproj -c Release
```

A nonzero exit throws before payload deletion. Core green means Core regressions cleared, not WPF/Hardware/Guardian or lifecycle acceptance. `-SkipTests` explicitly bypasses only this test gate, warns and requires disclosure that the package is unverified; it does not mean tests passed. Installer/lifecycle scripts do not independently run Core tests.

**Offline red-light interception is already demonstrated**, within this exact scope: repository evidence `research/evidence/current-validation/release-gate-interception.json`, invocation `scripts/test-release-gates.ps1`, injects fake dotnet **exit 73** and observes publisher child **exit 1**. Existing-payload and absent-payload fixtures both pass: the old fixture payload/sentinel is intact (content/hash/length/timestamps unchanged), absent payload stays absent, and no manifest/ZIP/hash appears. Harness exit 0 / PASSED describes the interception fixture only.

`RealDotnetExecuted=false`, `RealPublishExecuted=false`, `RepositoryPayloadAccessed=false`: no real dotnet or release was run, and the repository payload was not accessed. Thus the fixture alone proves only red-light interception, not a real Release pass, new package or exercise of repository dist. The separate mode-fix evidence above establishes the current binary snapshot; the fixture is not its substitute.

## Other publish gates

| Gate | Timing | Scope |
| --- | --- | --- |
| PowerShell AST (`Assert-ScriptSyntaxGate`) | before payload deletion | `scripts/*.ps1` parse, not runtime correctness |
| Payload copies (`Assert-PayloadCopiesMatchRepository`) | after copy | shipped docs/scripts match repository inputs, not factual correctness |
| Capability symbols (`Assert-CapabilitySymbols`) | after publish | required type names in managed assemblies, no assembly load or hardware acceptance |
| Payload self-check | after manifest | child runs payload `validate-package.ps1`, manifest/list/length/SHA-256 |
| Manifest consistency | after manifest | product/version/runtime/self-contained, timestamp, paths and file set |
| ZIP structure | after archive | `/` separators, ASCII safe relative entries, required paths, exact manifest set |
| Freshness | before ZIP sidecar | release-relevant source/tests/scripts/shipped docs not newer than run start |

The ZIP implementation uses `ZipFile.CreateFromDirectory` plus separator normalization rather than Windows PowerShell 5.1 `Compress-Archive`. The earlier ZIP's 740/741 entries with backslash separators are a **historical, already superseded** artifact description; neither that record nor the mode-fix ZIP (which passed its own inspection on its own snapshot) describes the current tree. A package of the current tree must rerun the complete chain. The offline red-light fixture stops at the first test gate and does not prove later gates ran end-to-end.

## Package and installer (commands for later execution)

Requires Windows x64/.NET SDK for publishing and Inno Setup for installer compilation:

```powershell
.\scripts\publish-release.ps1 -Version 0.1.0
.\scripts\build-installer.ps1 -Version 0.1.0
```

Both scripts take path parameters so a payload produced outside `dist\` can still be packaged, and so a dry run can stay in a temporary directory:

- `publish-release.ps1 -OutputRoot <dir>` publishes the payload to `<dir>\MiniProToolkit-x64-v<version>` (`-OutputRoot` defaults to `dist`).
- `build-installer.ps1 -PayloadRoot <dir>` compiles the installer from that payload directory (default `dist\MiniProToolkit-x64-v<version>`). Pass the same directory `publish-release.ps1` wrote; it must contain `release-manifest.json` for the same `-Version`.
- `build-installer.ps1 -OutputRoot <dir>` receives the setup executable and its `.sha256` sidecar (default `dist\installer`).

Relative paths are resolved against the repository root, absolute paths are used as given; both are converted to full paths and handed to ISCC as `/DPayloadRoot=<full path>` and `/DInstallerOutput=<full path>`, so the Inno defaults in `installer/MiniProToolkit.iss` only apply when the defines are absent. Spaced paths are accepted. `build-installer.ps1` still accepts `-CompilerPath`.

Isolated acceptance example (repository plus a temporary directory only, no `dist` write):

```powershell
.\scripts\publish-release.ps1 -Version 0.1.0 -OutputRoot temp\release-dryrun-0.1.0
.\scripts\build-installer.ps1 -Version 0.1.0 `
  -PayloadRoot temp\release-dryrun-0.1.0\MiniProToolkit-x64-v0.1.0 `
  -OutputRoot temp\installer-dryrun-0.1.0
```

`build-installer.ps1` verifies `release-manifest.json` (version, file list, SHA-256) and then the payload's own `scripts\validate-package.ps1` before ISCC runs. A payload **without** that validator is now a hard failure before ISCC (previously only a warning), matching the payload contract `Assert-PackagePayload` enforces - an unvalidated tree is never compiled into an installer. ISCC writes into a `.staging-<guid>` directory inside the requested output root; the setup executable and its sidecar are moved to the final names only after the compiler exited 0 and produced a non-empty PE file (MZ header checked), and the staging directory is removed on failure. An existing `MiniProToolkit-Setup-x64-v<version>.exe` or `.sha256` in the output root is **never** overwritten or deleted: the script stops before compiling and asks for a different `-OutputRoot`, so a failed run leaves the previous package untouched and a published artifact is only replaced deliberately by the operator.

### Publisher staging and previous-artifact semantics

Current `publish-release.ps1` source text (not a run; no publish was executed this round):

- The payload and its ZIP are built inside `<OutputRoot>\.publish-staging-<transactionId>\` (`payload\`, `package.zip`, `package.zip.building`); an existing staging directory is refused instead of reused, and nothing under the output root is touched before every gate accepts the staged result.
- The commit only renames same-volume paths: the previous payload, ZIP and `<zip>.sha256` become `<name>.prev-<transactionId>` (**never deleted**), and an already existing `.prev-<transactionId>` aborts the commit before any change.
- The old sidecar leaves before the new ZIP arrives; after the new ZIP is in place its hash is recomputed and must match the verified staged hash, then the new sidecar is written to `<zip>.sha256.writing-<transactionId>` and renamed, so no stale hash ever describes another ZIP.
- A failed commit rolls back only artifacts it can prove belong to this transaction; unknown occupants are left in place and the remaining `.prev-*` paths are reported for manual restore.
- Staging cleanup removes only a directory whose leaf is exactly `.publish-staging-<transactionId>` directly under the output base; anything else is refused rather than deleted recursively.

So a previous payload/ZIP/sidecar is preserved as `.prev-*` instead of "refused when the artifact already exists"; `build-installer.ps1` keeps the stricter never-overwrite rule for the setup executable. No new ZIP, Setup, manifest or hash exists for the current tree.

Expected default output paths, not a claim of new output:

```text
dist\MiniProToolkit-x64-v0.1.0\
dist\MiniProToolkit-x64-v0.1.0.zip
dist\MiniProToolkit-x64-v0.1.0.zip.sha256
dist\installer\MiniProToolkit-Setup-x64-v0.1.0.exe
dist\installer\MiniProToolkit-Setup-x64-v0.1.0.exe.sha256
```

This documentation round only records the parameters: no publish run and no ISCC compilation were executed, no installer was generated, and the existing `dist\` artifacts were not modified.

`installer/MiniProToolkit.iss` consumes the versioned self-contained win-x64 payload. Actual normal install/upgrade/repair/uninstall flows passed on the earlier (historical) snapshot, and the mode-fix installer passed same-version upgrade on its own snapshot as detailed above; this does not cover failure branches or the current tree. A repair is requested with `/REPAIR=1`; uninstall does not silently authorize EC recovery.

Payload top-level directories are `App/`, `Guardian/`, `scripts/`, `docs/`, plus `release-manifest.json`. App and Guardian executables are included with lifecycle/validation/recovery scripts.

Shipped document whitelist (publisher `$shippedDocs` plus `architecture.md`): `docs/README.md`, `architecture.md`, `phase-3-validation.md`, `phase-4-validation.md`, `phase-5-validation.md`, `fan-calibration.md`, `fan-control-hardening.md`, `history-and-diagnostics.md`, `performance-review.md`, `profile-automation-and-installer.md`, `product-roadmap.md`, `release-packaging.md`, `THIRD-PARTY-NOTICES.md`. `performance-review.md` ships because `architecture.md` links it; until this round it was copied into the payload but missing from `Assert-PackagePayload`'s required list, so a payload without it could still pass the lifecycle scripts' payload gate. The required payload surface also includes `scripts\validate-package.ps1` (the publisher ships and runs it, and `build-installer.ps1` hard-fails without it); both entries are checked by exact relative path. The README is copied from the repository root; `docs/` link prefixes are removed. Architecture's `../THIRD-PARTY-NOTICES.md` is rewritten to same-directory `THIRD-PARTY-NOTICES.md`, with assertions and packaged Markdown link checks.

`gap-list.md`, `console-product-plan.md` and `LICENSE-DECISION-REQUIRED.md` remain repository-internal, not shipped. Do not introduce packaged-document links to these unshipped md files. Source/evidence paths in backticks are references, not payload links. Third-party notices cover actual dependency outputs, not the main project's license.

Later package verification:

```powershell
.\dist\MiniProToolkit-x64-v0.1.0\scripts\validate-package.ps1 `
  -PackageRoot .\dist\MiniProToolkit-x64-v0.1.0
```

Internal consistency never substitutes for hardware/lifecycle acceptance.

## Lifecycle order and lease safety

1. Read-only preflight (`Assert-RestoreAuthorization`) rejects an active lease without explicit `-RestoreEcAutomatic`, before writes/deletes.
2. Gracefully stop the App (`Stop-MiniProToolkitGracefully`), wait up to its budget and throw if it survives; never force-kill it.
3. Reconfirm App stopped and lease state before any recovery (`Assert-MiniProToolkitStopped`, `Invoke-RestoreIfRequired`).
4. Only then request `FanDuty=101` through the established WMI path. Snapshot the lease before the write, lock `<lease>.lock` with bounded waiting, re-read and compare PID/start/heartbeat/mode before clearing. Changed, unreadable, vanished records or a busy lock fail rather than silently delete. The lock file is retained.

`package-common.ps1` checks recovery child exit, reconfirms App stopped/lease gone and rejects removal of the lock file. Service stop precedes replacing locked Guardian binaries but follows lease settlement; failures attempt payload/install-state rollback.

Commands below are operator guidance, **not executed here**, and require the appropriate explicit authorization/elevation for real use:

```powershell
.\scripts\install-package.ps1
.\scripts\upgrade-package.ps1
.\scripts\repair-package.ps1
.\scripts\uninstall-package.ps1
```

User configuration under `%LOCALAPPDATA%\MiniProToolkit` is preserved by normal upgrade/uninstall. Optional startup registration is explicit. Active leases block operations unless recovery is explicitly authorized with `-RestoreEcAutomatic`.

`-PurgeUserData` removes user data but ProgramData cleanup (`Remove-GuardianProgramData`) is whitelist-only and never recursive. The exact-name whitelist follows the writers' rotation: `guardian.log` plus `guardian.log.1`-`.3` (`BoundedTextLog`, 2 MiB + 3 archives) and `diagnostic-events.jsonl` plus `.1`-`.4` (`DiagnosticEventStore`, 2 MiB + 4 archives). Every entry is matched by exact file name (no wildcard/prefix/glob), its resolved parent must still be the program-data root, and a reparse point (junction/symlink) entry - or a linked root directory - aborts the purge instead of being deleted through. The lease, the lease lock file and unknown files/subdirectories are never deleted: an active lease or any unknown entry blocks the whole purge, and only a directory left empty is removed afterwards. An empty leftover lock is expected. The offline fixture `tests\test-package-common-purge.ps1` copies the script into a temporary sandbox and asserts all of this without touching the real `%ProgramData%`. Development `uninstall-guardian.ps1` also stops App first and refuses a remaining lease.

## Static checks

`check-ps-syntax.ps1` parses/inspects AST and ordering text without executing packaged scripts. Default lifecycle checks, `-All`, `-SkipLifecycleChecks` and `-Path` select scope; exit 0 means checked shapes parse/hold, exit 2 reports violations. Historical 20-script checks and isolated temporary lease/purge helper checks do not prove runtime safety on the installed App/service/real ProgramData/hardware. Documentation is not evidence that a static script ran.

This round the AST gate reports 20 scripts passing (`-All`, exit 0), and `tests\test-package-common-purge.ps1` reports PASSED on its offline fixture: copied script, sandbox `%ProgramData%`, whitelist-only deletion, unknown entry / out-of-range rotation / subdirectory / active-lease refusals, junction-entry and linked-root refusals, and the required-payload gate with and without `docs\performance-review.md` and `scripts\validate-package.ps1`. Both are static/fixture evidence only. A file symbolic link needs elevation (or Developer Mode) on this machine, so that single variant is reported as a skip and the directory-junction cases carry the reparse refusal.

## Current implementation and validation boundary

Implemented, Core-targeted covered, not target-machine accepted:

- **OSD templates**: `Core/OsdEventTemplates.cs`, 16-event whitelist, v3 preferences/management UI and v1/v2 compatibility. Safety title/body/icon/severity cannot be removed/overridden, duration cannot be shortened; no external resource execution.
- **CPU Windows efficiency-class frequency**: `CpuClassFrequency.cs` + `Hardware/CpuClassFrequencyReader.cs`, CPU-set/group/logical-index mapping, homogeneous `NotApplicable`, 256-counter bound without truncation, absolute `Processor Frequency` MHz rather than effective clock; no guessed Intel P/E identity. MainWindow/JSONL/history connected.
- **Auxiliary board/storage temperature**: `AuxTemperatureSelector` + independent `LibreHardwareMonitorAuxTemperatureSensor`, Motherboard/Storage only, either group valid keeps 5 s sampling; both missing back off 5–320 s. Unknown stays null; display/JSONL/history connected, 31-column CSV unchanged. CPU/GPU readers still use their own groups; those disabled flags do not mean the product lacks auxiliary temperature support.
- Existing GPU-only LHM temperature, overall CPU frequency, display-source selection, hotkeys, CLI/IPC/single instance/request-id, process conditions/status board, ordered shutdown, curve status/OSD, graded tiers, opt-in RPM/s/sticky anchor, default-off `RpmFeedback`/curve latch, lease gate and compare-and-clear remain implemented but not newly real-machine accepted.
- Explicit system audio/microphone/built-in brightness write APIs/buttons exist: probe, write once, compare readback, no timeout retry. Actual COM/WMI writes and device support remain unaccepted; CapsLock/NumLock read-only. Audio control/brightness reuse bounded `SingleFlightOperationWorker` (timeout retains slot, no retry, late result isolation, Dispose does not cancel native work); brightness Read/Probe/Set reuse pure fail-closed selection policy.
- De-identified automation execution JSONL/UI history/anonymous CSV exist: stable hashed rule/profile IDs, time/outcome/reason/duration only (plus schema version), 1 MiB/3 backups/500 tail lines; no names/paths/conditions, hash is not encryption. The per-tick runtime board remains memory-only.
- Retention/index/chart APIs, HistoryWindow and the memory-budget patch have landed: default 10 MiB/3 archives, optional archive-mtime age cleanup excludes active/schema files, trusted min/max file pruning falls back to scan on invalid/change, independent unit panels and display-only °F preserve °C storage/export. Default total scan budget: 500000 records; exhaustion warns of incomplete results and requests a narrower range. Truncated files are not trusted; index skips consume no scan budget. The persisted 87/87 targeted run is not full-suite or hardware acceptance.
- Accessible names added in three-window XAML and dynamic hotkey inputs; static markup coverage is not manual assistive-technology acceptance.

**Implemented, with limited tracking smoke but no automatic-calibration hardware acceptance**: whole-point-set optional `FanCurvePoint.TargetRpm`, independent bounded add/reclaim tracking with temperature duty floor priority, mutual exclusion with legacy `RpmFeedback`, emergency 100%, configuration/editor round trip. Automatic calibration (`FanCalibrationSampling` / editor / `RunFanCalibrationAsync`) runs 40→100 step 10, starts 0<T<65°C, requires 0<T<70°C throughout (70 abort/restores), 3 s settle then median of 5 unique stable samples with range ≤150 RPM, 30 s/point and 4 min overall. Cancel/lease/Guardian/bad sample fail closed; restoration must succeed before draft replacement, Save before application, demo disabled. Neither task has comprehensive thermal/fault acceptance; the earlier tracking smoke is limited to its recorded binary and duration. Startup calibration incompatibility removes only `TargetRpm`, preserving temperature/Duty safety points and warning; `ApplyCurveSettings` validates against actual calibration before saving.

### Historical targeted TRX counters (preserved, not current full-suite totals)

All files under repository `research/evidence/current-validation/`; not shipped. Runs may overlap and must not be summed.

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

The preserved automatic-calibration targeted run is 47/47 passing. `remaining-debug-build.log` retains the initial CS9124/CS0104 failure and corrected final successful Debug build with 0 warnings / 0 errors. `remaining-worker-regression.trx` Counters record 14 executed / 14 passed / 0 failed, not added to other runs. These are historical targeted records, not additive totals; the current full Release result and binary coverage are specified in the baseline section.

### Historical read-only probes (superseded availability snapshots)

`readonly-system-probe.json` now records the unique retry: 7calls/all returned, host exit0/about1.58s. CPUclass twice NotApplicable (one efficiency class,16LP); overall CPU first sample baseline3793MHz/current=null. Audio had no available output/microphone values and controls probe=false; brightness had no built-in panel instance and probe=false. The earlier dependency-preflight zero-call blocker remains in history only. No setter/EC/LHM/service/elevation occurred. This proves a limited read-only run with mostly unavailable values, not write/thermal-control acceptance.

`runtime-eligibility.json`: non-admin, AMD Ryzen 7 8745H, interface class metadata present, Guardian Running; no functional hardware/lease acceptance. The certificate harness failure remains historical in that file. Corrected synchronous read-only `code-signing-availability.json`/`.log` exited 0: CurrentUser/My and LocalMachine/My each contained 0 currently time-valid explicit Code Signing EKU certificates and 0 matching HasPrivateKey certificates. This covers only those two stores, not every certificate source; trust/revocation/key usability were not tested, runtime history was not overwritten and no signing was performed. `readonly-lhm-probe.json`: existing Debug snapshot only, CPU temperature/power null, GPU twice 32°C, board/storage null; not accuracy acceptance.

`hosted-demo-smoke.json` attempt 2 PASS after one harness correction: existing Debug DLL/dotnet host, 3 status exit0/demo=True, activate exit0, 8 distinct appended telemetry timestamps, normal shutdown exit0/no kill. Attempt1 preserves a harness failure, not a product CLI failure. This historical dotnet-host run did not validate apphost privilege/security; later elevated Release apphost and limited fan/lifecycle evidence are described in the baseline section. Sleep/Explorer/long-soak acceptance remains open.

## Historical records (preserved, not latest)

- Earlier Debug builds recorded 0 warnings / 0 errors, including `merged-build.log`; these historical records do not replace the later `remaining-debug-build.log`.
- Historical **726 passed / 0 failed / 0 skipped** is targeted execution; **257 passed / 0 failed / 0 skipped** is the filtered OSD/SystemIndicators/ToolkitCommand run (`merged-osd-system-cli.trx`). Neither is current full-suite acceptance.
- Historical static enumeration at 2026-11-01 15:31 local: 59 files, 540 Fact, 57 Theory, 248 InlineData, no MemberData, about **788 declared cases**, not execution or pass count and not a new enumeration of the changing tree. Older 561/525/505/187/181/155/153 counts remain historical only. App/Hardware/Guardian have no independent test projects.
- Earlier WPF settings/history/automation-editor smoke and telemetry cross-check retain only their original scope, not new sensor or lifecycle acceptance.
- Previous verified snapshot: `final-release-summary.json`, manifest `GeneratedUtc=2026-11-01T10:05:29.1537087Z`, 740 manifest entries/741 payload files including manifest. ZIP had 741 entries/no illegal backslashes; ZIP/Setup SHA-256 sidecars matched. Setup Authenticode was NotSigned; ZIP UnknownError is not a signature claim. Inspection harness failures were corrected before final inspection exit0. Old 954 does not cover later code. Any later candidate's source coverage, manifest/ZIP/installer/hash validity and generation time are determined by its actual summary/inspection evidence.

## Remaining release blockers and acceptance

1. Automatic calibration on hardware; at least 2 h soak, multi-level fixed-RPM/tracking stability, real 90°C/latches, read/write/Guardian fault matrices and recovery interleavings. Short earlier smoke and one actual owner-crash pass do not cover these matrices or retest mode-fix fan control.
2. Sleep/resume, elevated logon startup, manual UI/screen-reader/keyboard navigation, single-display DPI/auto-hide/Explorer, OSD/hotkey conflicts, automation and full IPC/cross-user/session/request-id acceptance. The demo CLI pass is not complete live IPC acceptance.
3. Installer failure rollback, active-lease blocking/recovery and failure branches; actual system audio/brightness setters and sensor accuracy. Normal lifecycle success is not failure-path acceptance.
4. **No main root LICENSE**: license choice, copyright holder and signing decision remain pending; Setup is NotSigned. Historical lite licensing and third-party notices do not authorize the main project. Do not invent legal/signing facts or change legal files during directory cleanup.
5. No repackaging now. Before a future release containing these newer docs, rebuild and verify docs/copies/links, gates, manifest, ZIP/Setup and sidecars against that new snapshot. The current tree's full-suite Release run, real publish/inspection and installer build are pending and are not claimed by this round.
