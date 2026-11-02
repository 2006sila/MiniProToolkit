# MiniProToolkit source archive notice

Package version: **0.1.0**. Archive root: `MiniProToolkit-source-v0.1.0/`. Archive revision: **2026-11-02** (MIT `LICENSE` added; see the change note at the end).

This is a curated source-code archive for sharing/review on GitHub. The main project is licensed under the **MIT License** — see `LICENSE` at the archive root (`Copyright (c) 2026 2006sila`); the decision record and scope are in [docs/license.md](docs/license.md). The MIT grant covers this project's own code only. [Third-party notices](THIRD-PARTY-NOTICES.md) cover dependencies under their own licenses (MPL-2.0 / MIT / Apache-2.0) and do not extend or restrict the main project's terms. An MIT license is not an assurance that any hardware behavior described in the documentation is correct on a particular machine.

The manifest records relative path, byte length and SHA-256 for every other packaged file. `SOURCE-ARCHIVE-MANIFEST.json` intentionally does not hash itself (no recursive self-hash). The external archive evidence records the manifest hash and final ZIP hash. Source entries additionally record original hashes/lengths; any allowed Markdown transformation occurs only in this staged copy and records original/archive hashes in the manifest.

Only approved root files, C#/XAML/project/manifest sources, Core tests, the installer recipe, 15 approved PowerShell scripts and 11 approved documents are included. Research, `.pi`, `.git`, internal planning documents, logs, evidence, dist/artifacts/lite, build outputs/caches, TestResults, credentials and binary payloads are excluded. Plain-text references to internal planning/evidence are retained as historical context, not included documents or new validation evidence. Internal planning Markdown links, if encountered, are changed only in staging to unlinked explanatory text. Synthetic dummy paths in tests are intentionally retained.

## External build prerequisites and limitations

Use Windows x64 with the .NET 9 SDK (the application uses Windows WPF/Windows Forms). `dotnet restore ./MiniProToolkit.sln` requires access to NuGet sources (normally nuget.org) unless all exact dependencies are already in a local cache. Dependency packages, SDKs, Windows tooling and Inno Setup are external and are not vendored here. Building Setup additionally requires Inno Setup; no Setup compilation, installation, elevation or hardware validation is performed for this source archive. The installer recipe now sets `LicenseFile=..\LICENSE`, so a future build expects `LICENSE` next to the repository root.

Clean validation was performed on a separate temporary source copy, without research or prior bin/obj: restore the solution; build Debug with `--no-restore`; run the complete Core test project in Release without a filter. Generated files are removed before archiving. Consult the external `final-source-archive.json` evidence for actual outcomes; this notice is not an assurance that any hardware, UI, installer or security behavior is accepted. Secret checking uses finite patterns only and cannot guarantee the absence of sensitive information.

The previously verified **mode-fix binary snapshot is unchanged by this source archive**. New source-package documentation is not retroactively part of existing binary ZIPs, Setup or installed copies. This archive neither changes device mode nor supersedes snapshot-specific release/validation caveats in the included documentation.

## Change note (2026-11-02)

- Added `LICENSE` (MIT, `Copyright (c) 2026 2006sila`) and `docs/license.md`; removed the superseded `docs/LICENSE-DECISION-REQUIRED.md`.
- `README.md`, `THIRD-PARTY-NOTICES.md` and this notice no longer say the project is unlicensed.
- `installer/MiniProToolkit.iss` adds `LicenseFile=..\LICENSE`. The public `MiniProToolkit-Setup-x64-v0.1.0.exe` was built **before** this change, so its wizard has no license page and its bytes are unaffected.
- `SOURCE-ARCHIVE-MANIFEST.json` and the ZIP were regenerated; the ZIP SHA-256 differs from the first upload of this release.
