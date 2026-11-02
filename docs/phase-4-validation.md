# Phase 4 Validation: Safety Curve

The first real-machine smoke test has passed. The test used the installed Guardian service, enabled the safety curve through the WPF UI, sampled for 10 seconds, clicked `恢复 EC 自动`, and verified that the lease was removed.

Evidence:

- `research/evidence/curve-smoke-last.json`
- Guardian service remained `Running`.
- The lease was created and then removed.
- Sample temperatures were about 43-61 C.
- Sample fan speed was about 1935-2234 RPM.
- No MiniProToolkit process remained after cleanup.

## Current limits

This is an idle smoke test, not a sustained thermal validation. It does not prove that the curve is stable under CPU load or that all temperature transitions are safe for long periods.

## Controlled 60-second low-load validation

A controlled test has now completed with one CPU load worker, a 60-second limit and an 80 C stop threshold.

Evidence: `research/evidence/curve-load-last.json`.

- Duration: full 60 seconds; temperature stop threshold was not reached.
- Fan speed rose from about 1,916 RPM to about 3,150 RPM as temperature rose.
- After initial warm-up, CPU temperature stayed mostly around 72-76 C and ended at 74 C.
- The curve lease was created and removed after clicking `恢复 EC 自动`.
- Guardian remained `Running`; no main app or lease remained after the test.

This verifies a bounded low-load run, not long-duration reliability across seasons, BIOS changes or unusual ambient temperatures. A second workload tier or extended soak test should be done only after reviewing these measurements.

Stop immediately and use `scripts/restore-ec-automatic.ps1` if CPU temperature approaches 80 C in the conservative test profile, RPM does not increase, WMI reads fail repeatedly, or the UI becomes unresponsive.

A crash-recovery test is documented in `docs/phase-3-validation.md`. The Guardian service must remain installed and running for every curve test.
