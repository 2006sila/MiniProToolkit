# Performance review - status surfaces, telemetry storage and log reading

This document records one performance round against the user reports "the taskbar/desktop status
hiccups every once in a while" and "the app sits around 100 MB of memory". It lists what was
actually confirmed, what changed, what was measured in isolation, what was **not** measured, and
the constraints that were deliberately kept. It contains no wall-clock release date because the
numbers are tied to the code state and the harness below, not to a calendar day.

## 1. Confirmed problems (code-level, not yet target-machine profiled)

| # | Observation | Root cause found in source |
| --- | --- | --- |
| 1 | The 1 s status pass rebuilt the whole taskbar/desktop WPF tree | `TaskbarStatusWindow` / `FloatingStatusWindow` cleared and recreated every `TextBlock`/`Border`/`Grid` per sample, because nothing distinguished "same topology, new value" from "different arrangement" |
| 2 | Trend legend and Guardian availability work repeated per tick | the legend's `Inlines` were rebuilt per sample instead of cached; the Guardian state (a `ServiceController` **service-status** query, not a process lookup) was evaluated more than once per pass |
| 3 | Telemetry disk I/O ran on the UI thread | the record was serialized and appended to `telemetry.jsonl` **and** `telemetry.csv` synchronously inside the 1 s pass (ordered by the pass gate, so no interleaving - but a slow disk blocked the dispatcher for the duration of both appends) |
| 4 | `TelemetryRecorder.Append` revalidated the CSV header on every row | baseline `EnsureCsvSchema()` opened the CSV and read the header line on every append (the very first append on a brand-new/empty file returns before the read, so 1 000 appends cost ≈ 999 reads; with an existing file it is 1 000), and `EnsureCsvHeader()` then stat-ed the file again (`File.Exists` + `FileInfo.Length`, no read); nothing remembered "this exact file content was already validated" |
| 5 | `AppLog` grew without bound | plain `File.AppendAllText` on `%LOCALAPPDATA%\MiniProToolkit\miniprotoolkit.log` |
| 6 | The history window read plain-text logs whole | `TelemetryHistory.ReadLog` streamed the entire file into a `List<HistoryEvent>` (a multi-hundred-MB legacy log became a multi-hundred-MB list) |
| 7 | Taskbar discovery walked every top-level window on every tick | the 1 Hz pass called `EnumWindows` each tick just to learn that nothing had changed, and only a shell/display/settings event made it re-enumerate for real (there was no cheap "nothing changed" path) |
| 8 | The main-window trend chart was repainted while hidden | chart shapes/legend were rebuilt on every sample even when the window was hidden or minimized, so the work was paid where nobody could see it and the first visible frame still had to catch up |

## 2. What changed

**Status surfaces (rendering).** `MiniProToolkit.Core/TaskbarRenderSignature.cs` introduces a
value-independent topology key: mode, rows/columns, effective direction, compact flag, font size,
safe width/height budget, every slot (item, source index, row, column, group, wording style), the
stable label/unit/card-label text the planner measured, the `+N` marker and the exact order of its
hidden items, and the concrete grid column projection. `TaskbarRenderTexts.Updates(plan)` is the
pure "what to write now". Both windows now build the tree only when `Matches(signature)` fails and
otherwise only assign text/tooltip/accessible name that actually differ. Cached element references
are dropped on rebuild and on close, so the cache is bounded by the slot count (≤ 14).
`TaskbarStatusWindow`/`FloatingStatusWindow` keep the newest `HardwareSnapshot` even while hidden,
skip the invisible tree, invalidate the cache so the first visible frame repaints the kept sample,
skip only the provably identical native placement (`IsSameNativePlacement`) and re-place after a
re-attach/DPI change/restore.

**Main window.** The 8 trend shapes are created once and afterwards only get frozen `PointCollection`s
and guide coordinates; the legend is cached by the one display-source sentence it depends on; a
sample that arrives while the window is hidden or minimized sets a dirty flag instead of painting,
and `OnStateChanged` repaints it immediately when the window becomes visible again. The duplicate
Guardian **service** query per pass is gone (one verdict feeds the fixed-RPM branch, the curve and
the status/tray refresh). The telemetry append moved off the UI thread into an `await Task.Run(...)`
that runs **inside the same pass gate** and is awaited before the pass returns, so records keep
their order, none is lost, the exit sequence still sees the last record - and a slow disk no longer
blocks the dispatcher (the 1 s sampling/safety gate is unchanged). Taskbar discovery no longer calls
`EnumWindows` every tick: it enumerates on the first pass, immediately when a mounted overlay's
native taskbar handle died, and on a 30 s monotonic safety net, while shell/display/settings events
still enumerate/refresh at once.

**Storage.** `TelemetryRecorder` caches a `(length, lastWrite, creationTime)` stamp of the CSV it
last validated, so consecutive appends read the header **once**; any external write/truncate/
delete/rename changes the stamp and forces a fresh validation, a rotation drops the stamp, and a
partial write drops it too. The cache cannot go stale silently: `PrepareCsvAppend()` re-checks the
CSV immediately before the row is written (restoring a missing/truncated header, revalidating a
changed stamp) and `AuthenticateCsvStamp()` only accepts the post-write stamp when the file grew by
exactly the appended UTF-8 byte count - a delete/truncate/concurrent append race therefore leaves
the cache empty, never poisoned. `AppLog` now goes through `BoundedTextLog` (2 MiB live file, 3
rotated archives, 32 KiB per entry, UTF-8-boundary-safe truncation). `TelemetryHistory.ReadLog`
reads a bounded tail (2 MiB / 2 000 entries by default), drops the partial first line (the byte just
before the tail start is inspected so a tail that begins exactly on a line boundary keeps that
line), keeps the original file untouched and reports truncation through an optional warning
callback that `HistoryWindow` shows.

**Test coverage of this round.** The Core test project now has **1 415** tests (1 355 before the
first batch, 1 364 after the taskbar switch, 1 407 after the three performance batches, 1 410 with
the taskbar re-attach fix, 1 415 with the storage hardening). All of them pass in Debug and
Release; the new performance cases are `MainWindowPerformanceMarkupTests`,
`StatusRenderPerformanceTests` and `StoragePerformanceTests`.

## 3. What is deliberately unchanged

- The 1 s sampling/control cadence and the safety gate, all temperature/curve/Guardian gates and
  every EC write path.
- The CSV schema: exactly the existing **31 columns**, same header string, same archive-on-mismatch
  rule (`telemetry.csv.schema-*.bak`).
- Telemetry dual write (`telemetry.jsonl` + `telemetry.csv`) and the retention defaults
  (`TelemetryRetentionSettings`: 10 MiB per file, 3 archives, no age-based deletion).
- Every floating-window feature (drag, snap, click-through, hotkeys, single editor, position
  persistence) and every taskbar layout mode (A/C/D/E).
- The `+N` overflow marker, tooltips and accessible names; the hidden-sample-then-render order.

## 4. Isolated benchmark (reproducible)

No GUI, no hardware access, no `%LOCALAPPDATA%`/`%ProgramData%` writes: the harness only creates
its own directory under `temp/perf-validation-20261102/results`, uses synthetic
`HardwareTelemetryRecord`/`HardwareSnapshot` values and a `net9.0` console.

- Current build: `temp/perf-validation-20261102/bench-current` (project reference to
  `src/MiniProToolkit.Core`).
- Baseline build: `temp/perf-validation-20261102/bench-baseline` (project reference to
  `temp/perf-validation-20261102/baseline-core`, an untouched copy of the current `Core/*.cs` with
  `Telemetry.cs` and `TelemetryHistory.cs` restored from
  `temp/perf-storage-backup-20261102-170304/Core/`). The main project is never modified.
- Shared scenario code: `temp/perf-validation-20261102/shared/Program.cs`.
- Reports: `temp/perf-validation-20261102/results/after-review-current.txt`,
  `after-review-baseline.txt` and a second independent run of each
  (`after-review-*-confirm.txt`). **These are the numbers to quote.** The earlier
  `bench-current*.txt` / `bench-baseline*.txt` files are kept as history only: they were taken
  before `PrepareCsvAppend()`/`AuthenticateCsvStamp()` added the extra pre-append `stat`, so their
  timing deltas (−24 % for the 1 000-append case) are **superseded** and must not be mixed with the
  tables below.

Both harnesses are built and run in **Release**. Each scenario runs one untimed warmup pass plus 3
timed repeats with a forced GC before each repeat; the reported number is the median of the 3.
`Stopwatch` measures the loop, `GC.GetAllocatedBytesForCurrentThread()` the allocation of the same
thread. The CSV header counter is the `internal` member `TelemetryRecorder.CsvHeaderValidationCount`,
read reflectively (no public diagnostic API was added).

### 4.1 `TelemetryRecorder.Append`

Pre-existing valid CSV header (the realistic case: history already exists), median of 3; second
independent run in parentheses:

| Scenario | Baseline | Current | Delta |
| --- | --- | --- | --- |
| 500 appends, elapsed ms | 82.7 (82.9) | 66.9 (66.6) | −19 % (−20 %) |
| 500 appends, allocated KiB | 11 616 (11 757) | 6 763 (6 865) | −42 % (−42 %) |
| 1000 appends, elapsed ms | 164.1 (165.4) | 138.2 (135.4) | −16 % (−18 %) |
| 1000 appends, allocated KiB | 22 255 (22 448) | 13 513 (13 716) | −39 % (−39 %) |
| CSV header reads over 1000 appends | **1 000** (one per append; the file already exists) | **1 total** | new |

Fresh directory (file creation on the first append), median of 3; second run in parentheses:

| Scenario | Baseline | Current | Delta |
| --- | --- | --- | --- |
| 500 appends, elapsed ms | 82.8 (82.0) | 68.6 (68.5) | −17 % (−17 %) |
| 1000 appends, elapsed ms | 160.9 (162.7) | 135.8 (133.8) | −16 % (−18 %) |
| 1000 appends, allocated KiB | 21 668 (21 950) | 13 506 (12 161) | −38 % (−45 %) |
| CSV header reads over 1000 appends | **999** (the first append only writes the header, then each later append reads it) | **0** (the header is written, never read back) | new |

Data integrity checked in every run, for both builds: `telemetry.jsonl` lines == CSV data lines ==
N (500/1000), CSV header line == `TelemetryRecorder.CsvHeader`, and **every** data row splits into
exactly 31 columns. The truncate/delete/external-write/rotation invalidation paths and the
pre-append / post-write authentication race cases are covered by `StoragePerformanceTests`
(22 cases; `StatusRenderPerformanceTests` has 23, `MainWindowPerformanceMarkupTests` 6).

Interpretation, stated conservatively: this is an IO-bound one-append-per-second path, so the
≈0.026 ms per append saved (164 µs → 138 µs) is not a user-visible win by itself, and the extra
pre-append `stat` deliberately gives back part of the earlier speedup: the timing delta is now
−16 %/−18 % where the first, pre-hardening measurement showed −24 %. What stays is the removal of
the per-tick second `StreamReader`/`FileInfo` *header read* inside the lock shared with the dual
write, the ≈40 % lower allocation, and the guarantee that a poisoned stamp cannot survive. No
percentage of "real CPU" is claimed.

### 4.2 `TelemetryHistory.ReadLog` on a 2.9 MiB legacy plain-text log (52 000 lines)

| Metric | Baseline | Current | Delta |
| --- | --- | --- | --- |
| median elapsed ms | 97.3 (91.1) | 56.1 (54.9) | −42 % (−40 %) |
| allocated KiB | 49 376 | 35 166 | −29 % |
| entries returned | 52 000 (whole file) | 2 000 (bounded tail) | bounded |
| truncation notice | none (silently complete) | 1 warning through the callback, shown by `HistoryWindow` | new |

### 4.3 `TaskbarRenderSignature` from the real formatter + layout

1 000 plans per configuration, each built from a synthetic `HardwareSnapshot` whose values move
every second (the item list, labels, units and card labels stay fixed), formatted by
`TaskbarMetricFormatter.Format` and laid out by `TaskbarItemLayout.Build`:

| Configuration | equal consecutive pairs | Create 1000 plans (ms) | Matches 999 pairs (ms) | Create alloc (KiB) | slots |
| --- | --- | --- | --- | --- | --- |
| StackedPairs / horizontal | 999 / 999 | 5.3 | 2.8 | 2 140 | 9 |
| StackedPairs / vertical | 999 / 999 | 1.6 | 0.8 | 1 179 | 13 |
| Inline / horizontal | 999 / 999 | 1.7 | 0.7 | 796 | 13 |
| Inline / vertical | 999 / 999 | 1.2 | 0.7 | 796 | 13 |
| AdaptiveColumns / horizontal | 999 / 999 | 2.6 | 0.7 | 1 601 | 9 |
| AdaptiveColumns / vertical | 999 / 999 | 1.5 | 0.7 | 1 179 | 13 |
| LabelOverValue / horizontal | 999 / 999 | 1.8 | 0.7 | 796 | 13 |
| LabelOverValue / vertical | 999 / 999 | 1.3 | 0.7 | 796 | 13 |

So a value-only change across 1 000 samples is 100 % "reuse, do not rebuild" in every mode and
direction, and one signature costs about 0.8–2.1 KiB (2 KB) - the cache is small and bounded.
Negative controls on the same plans (must all invalidate): budget changed → False, font changed →
False, configured order reversed → False, compact flag → False, vertical direction → False; only
the value-only sample → True. These are the same assertions the unit tests make; here they run on
1 000 real plans instead of a handful.

## 5. Limits and what was NOT measured

- **No live WPF paint timing.** Reusing the tree is proven by the signature equality above plus the
  source guards in `StatusRenderPerformanceTests`; the actual frame cost on a real desktop and the
  perceived hitch are still a target-machine smoke check. No window was constructed for this round.
- **No working-set measurement.** The "about 100 MB" figure was not profiled in this round, because
  it needs a running GUI process. What is known: the per-tick allocation churn and the per-tick
  element recreation are removed, and the log-reading path is bounded instead of "whole file into a
  list". Whether resident memory drops, and by how much, is unverified.
- **The bounded tail read still decodes the whole tail.** It allocates a 2 MiB byte buffer, decodes
  every complete line inside it and then trims to the newest 2 000 entries, so its *transient*
  allocation still grows with the tail text (≈ 35 MiB for the 2.9 MiB log above) even though the
  returned list and the retained memory are capped. Reading backwards until the entry budget is
  satisfied would be a further improvement; it is not part of this round.
- **Synthetic input.** Records/snapshots are generated in code; real telemetry rows have similar
  field sizes but the numbers above are not a model of every user's history directory.
- Durations are IO-bound observations on one machine (16 logical CPUs, .NET 9.0.22, Windows
  10.0.26300). A second independent run of the same harness moved these medians by up to 11 %
  (system noise), so the ratios are observations, **not** a stability gate. The `bench-*` reports
  taken before the storage hardening show better append timings (−24 %); they are kept as history
  only and must not be quoted.

## 6. Known gaps not addressed in this round

- LHM sampling and its three instances; the automation process queries.
- The history window's 500 000-record load peak and the incremental index (a large historical file
  is still bounded by a scan budget, not indexed).
- Old `telemetry.csv.schema-*.bak` archives are never deleted automatically.
- The Guardian log is not rotated.
- The first rotation of a pre-existing oversized `miniprotoolkit.log` keeps the whole old file as
  `.1`, so that archive can exceed the 2 MiB live-file cap once.
- There is no fixed total size for the data directory. Each telemetry file type may reach
  10 MiB + 3 × 10 MiB, `AppLog` reaches 2 MiB + 3 archives, schema backups and the Guardian log add
  unmanaged bytes - "the data directory is 8 MiB" would be wrong.

## 7. Reproduction

```text
dotnet build <scratch>/bench-current/bench-current.csproj -c Release
dotnet build <scratch>/bench-baseline/bench-baseline.csproj -c Release
dotnet <scratch>/bench-current/bin/Release/net9.0/PerfHarnessCurrent.dll after-current <temp-output-dir>
dotnet <scratch>/bench-baseline/bin/Release/net9.0/PerfHarnessBaseline.dll after-baseline <temp-output-dir>
```

Each run writes `bench-<label>.txt` into the given output directory and creates its own scratch
directories there (roughly 15 MiB per run; delete them after reading the report). Nothing outside
that directory is touched.
