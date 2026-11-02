# MiniProToolkit source archive v0.1.0

This archive is the v0.1.0 source snapshot published next to the x64 portable payload
and the Inno Setup installer. Layout:

- src/      main application, Guardian service, hardware layer and Core policy library
- tests/    offline xUnit tests (no real hardware or service access required)
- scripts/  build, packaging, lifecycle and validation scripts
- installer/Inno Setup script used to build the setup executable
- docs/     architecture, fan control, telemetry, roadmap and packaging documentation

Build and test (requires the .NET 9 SDK on Windows x64):

    dotnet restore .\MiniProToolkit.sln
    dotnet build .\MiniProToolkit.sln -c Release -warnaserror
    dotnet test .\MiniProToolkit.sln -c Release

Package (paths are caller supplied; nothing in this repository hardcodes a machine path):

    .\scripts\publish-release.ps1 -Version 0.1.0 -OutputRoot <output-root>
    .\scripts\build-installer.ps1 -Version 0.1.0 -PayloadRoot <output-root>\MiniProToolkit-x64-v0.1.0 -OutputRoot <installer-output-root>

SOURCE-ARCHIVE-MANIFEST.json lists every file in this archive with its SHA256.

License status: the main project is licensed under the **MIT License** — see `LICENSE` at the
repository root (`Copyright (c) 2026 2006sila`). THIRD-PARTY-NOTICES.md covers third-party
dependencies only (MPL-2.0 / MIT / Apache-2.0) and does not change the main project's terms.