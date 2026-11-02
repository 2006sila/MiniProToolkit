# 第三方组件声明（THIRD-PARTY NOTICES）

> 本文件只覆盖第三方组件。MiniProToolkit 主项目自身采用 MIT（版权人 2006sila），见仓库根 `LICENSE` 与 `docs/license.md`；下列组件仍按各自许可证提供，不因主项目采用 MIT 而改变。

MiniProToolkit 以**未修改的二进制形式**分发下列第三方 NuGet 包。本文件不复制第三方源码或许可证全文；完整许可文本可从注明的 nuget.org 包或许可证链接取得。

核对依据（只读）：

- `src/MiniProToolkit.Hardware/MiniProToolkit.Hardware.csproj`、`src/MiniProToolkit.App/MiniProToolkit.App.csproj`、`src/MiniProToolkit.Guardian/MiniProToolkit.Guardian.csproj`；
- `src/MiniProToolkit.App/obj/project.assets.json`（targets `net9.0-windows7.0`、`net9.0-windows7.0/win-x64`）；
- `~/.nuget/packages/<id>/<version>/<id>.nuspec` 的 `license` / `licenseUrl` / `projectUrl` / `repository` 字段；
- App 与 Guardian 的 Debug 输出目录中实际复制的程序集（`bin/Debug/net9.0-windows/win-x64`）。

> `dist/` 下的发布目录、ZIP 与安装包已在本轮重新生成，当前产物**包含**下列 LibreHardwareMonitor 相关程序集和本文件（副本位于 payload 的 `docs/THIRD-PARTY-NOTICES.md`，见 `release-manifest.json`）。可用 `scripts/validate-package.ps1` 只读核对清单与哈希。

## LibreHardwareMonitor 依赖链（可选、只读的 CPU 对照来源）

| 包 | 版本 | 关系 | 随 App/Guardian 输出的程序集 | 许可证 | 来源 |
| --- | --- | --- | --- | --- | --- |
| LibreHardwareMonitorLib | 0.9.7-pre717 | 直接引用（MiniProToolkit.Hardware） | `LibreHardwareMonitorLib.dll` | MPL-2.0 | nuget.org；仓库 https://github.com/LibreHardwareMonitor/LibreHardwareMonitor （nuspec 记录 commit `d6cb2604646287da7a21e414b6539055cefb6a81`） |
| DiskInfoToolkit | 2.1.2 | LibreHardwareMonitorLib 传递 | `DiskInfoToolkit.dll` | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| BlackSharp.Core | 1.1.4 | 传递（DiskInfoToolkit / RAMSPDToolkit-NDD 依赖，解析结果取 1.1.4） | `BlackSharp.Core.dll` | MPL-2.0 | https://github.com/Blacktempel/BlackSharp |
| HidSharp | 2.6.4 | LibreHardwareMonitorLib 传递 | `HidSharp.dll` | Apache-2.0（包内 `LICENSE.txt` 明文声明） | https://software.seekye.com/hidsharp |
| Mono.Posix.NETStandard | 1.0.0 | LibreHardwareMonitorLib 传递 | `Mono.Posix.NETStandard.dll`、`MonoPosixHelper.dll`、`libMonoPosixHelper.dll` | MIT（依据包自身 `licenseUrl` 的最终跳转目标，见下） | nuspec 未给出 SPDX 表达式，仅 `licenseUrl` = https://go.microsoft.com/fwlink/?linkid=869050 |
| RAMSPDToolkit-NDD | 1.6.0 | LibreHardwareMonitorLib 传递 | `RAMSPDToolkit-NDD.dll` | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |

## .NET 平台包（MIT）

以下包是 App 与 Guardian 输出目录的合计实际输出（个别包只在其中一个目录出现，例如 `System.Configuration.ConfigurationManager`、`System.Diagnostics.EventLog`、`System.Security.Cryptography.ProtectedData`、`Microsoft.Extensions.*` 只随 Guardian 输出）：`System.Management` 10.0.10（Hardware 直接引用，LibreHardwareMonitorLib 要求 ≥ 10.0.10）、`System.IO.Ports` 10.0.10（LHM 传递）、`System.CodeDom` 10.0.10（`System.Management` 传递）、`System.Threading.AccessControl` 10.0.10（LHM 传递）、`System.Diagnostics.PerformanceCounter` 9.0.10（Hardware 直接引用）、`System.Configuration.ConfigurationManager` 9.0.10、`System.Diagnostics.EventLog` 9.0.10、`System.Security.Cryptography.ProtectedData` 9.0.10（以上三个为传递）、`System.ServiceProcess.ServiceController` 9.0.10（App 直接引用）、Guardian 的 `Microsoft.Extensions.*` 9.0.10 与 `Microsoft.Extensions.Hosting.WindowsServices` 9.0.10，以及 .NET 9 自包含运行时（`Microsoft.NETCore.App` / `Microsoft.WindowsDesktop.App`）。

这些均为 MIT 许可的 .NET 平台库，来源：https://github.com/dotnet/runtime 、https://github.com/dotnet/windowsdesktop 、https://github.com/dotnet/aspnetcore 。

## 使用范围

- LibreHardwareMonitor 仅作为「LHM 对照」提供只读的 CPU Package 温度与功耗；EC 温度仍用于全部控制与保护阈值，功耗既不是整机墙插功率，也不参与风扇控制。
- 运行时只启用 LHM 的 CPU 组（`IsCpuEnabled`），GPU/NVAPI、主板、存储、网络、控制器、电池、PSU、功耗计分组全部关闭；但上述库的程序集仍会随 App 与 Guardian 输出（程序集级依赖，未做额外裁剪）。
- 本程序不随包分发 PawnIO 驱动，也不安装任何驱动；仅在诊断文案中只读查询 `PawnIo.IsInstalled`，用于解释「未发现 CPU 传感器」。
- MPL-2.0 组件的二进制未作修改，因此按原样分发；如替换为修改版，需按 MPL-2.0 提供相应源码。

## 待核对

- `Mono.Posix.NETStandard` 1.0.0 的许可证：已按包自身 `licenseUrl` 只读核对（2026-11-01，HTTP 200）。该链接最终跳转到 https://github.com/mono/mono/blob/main/LICENSE，原文列明 “the runtime and its class libraries are licensed under the terms of the MIT license, and some third party code is licensed under the 3-clause BSD license”。包内（`.nupkg`）未附带 LICENSE 文件，nuspec 也没有 SPDX 表达式，因此结论依据是该 `licenseUrl` 指向的 Mono 主仓库许可证文本，按 **MIT** 记录。如需更严格的法务口径，仍建议对外发布前人工确认一次该页面。
- 任何新增或升级的 NuGet 包都会改变实际载荷，需重新生成 `obj/project.assets.json` 后更新本文件。
