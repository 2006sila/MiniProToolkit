# Phase 3 Validation: Basic Control and Guardian

This phase validates the installed Guardian service and the basic fan controls. Run every command from an elevated PowerShell window. Do not enable the safety curve during this procedure.

## Read-only preflight

Replace `<repository>` with your checkout directory.

```powershell
cd "<repository>"
.\scripts\guardian-status.ps1
Get-Service MiniProToolkitGuardian
```

Expected result: `Running`, `Automatic`, no active lease.

## Start the application

```powershell
Start-Process `
  ".\src\MiniProToolkit.App\bin\Release\net9.0-windows\win-x64\MiniProToolkit.exe" `
  -Verb RunAs
```

Confirm that the main window shows CPU temperature, fan RPM, power mode and `Guardian: Running`.

## Basic control test

1. Record the current fan RPM and power mode.
2. Click `最大转速`.
3. Confirm the RPM increases.
4. Confirm `%ProgramData%\MiniProToolkit\guardian-lease.json` exists.
5. Click `恢复 EC 自动`.
6. Confirm the lease file is removed and the fan returns to firmware behavior.

The manual restore script is available for recovery:

```powershell
.\scripts\restore-ec-automatic.ps1
```

## Guardian crash recovery test

Only perform this after the basic control test is understood:

1. Start the application as administrator.
2. Click `最大转速` and confirm the lease exists.
3. End only the `MiniProToolkit` process from Task Manager.
4. Do not stop `MiniProToolkitGuardian`.
5. Wait at least 12 seconds.
6. Run `.\scripts\guardian-status.ps1`.
7. Confirm the lease is removed after successful recovery.
8. Read the fan RPM and confirm the fan is no longer held by the application.

This test performs a real WMI write of `FanDuty=101`. Keep the manual recovery script ready and stop the test if temperature or fan behavior is abnormal.

## Scope

This phase does not validate custom curve stability under sustained load. Curve validation is a separate phase and should be performed only after the crash recovery test passes.
