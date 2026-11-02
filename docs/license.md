# 许可证记录：MIT（2026-11-02 决定）

## 决定

- 许可证：**MIT**（官方原文，未改写条款），文件位于仓库根 [LICENSE](../LICENSE)。
- 版权人字符串：`2006sila`；年份：2026。不沿用已删除 `lite/LICENSE` 的 `MiniProLite contributors`，也不覆盖 `lite` 子产品。
- 本文件原名 `docs/LICENSE-DECISION-REQUIRED.md`（决策待定状态），2026-11-02 定案后改为现在的记录。

## 为什么选 MIT

- 诉求是「源码公开、别人可以看/改/用」，MIT 覆盖这三点且义务最少：保留版权与许可声明即可。
- 与随包分发的第三方组件兼容：LibreHardwareMonitorLib 及其传递程序集为 MPL-2.0（文件级 copyleft，按**未修改的二进制**分发即可）、HidSharp 为 Apache-2.0、Mono.Posix.NETStandard 与 .NET 平台库为 MIT。主项目采用 MIT 不改变这些组件自身的条款，见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。
- 与 Apache-2.0 的实质差别只有两点：明示专利授权、商标条款。本项目没有专利诉求，故取 MIT 的简洁版。

## 本轮落地

| 项 | 状态 |
| --- | --- |
| 仓库根 `LICENSE`（MIT 原文，`Copyright (c) 2026 2006sila`） | 已加 |
| `README.md` 增加「许可证」章节，删掉「无根 LICENSE」表述 | 已改 |
| `THIRD-PARTY-NOTICES.md` 顶部区分主项目许可证与第三方声明 | 已改 |
| `SOURCE-ARCHIVE-NOTICE.md` 的授权表述改为 MIT | 已改 |
| `installer/MiniProToolkit.iss` 增加 `LicenseFile=..\LICENSE` | 已改（只影响后续构建） |
| `SOURCE-ARCHIVE-MANIFEST.json` 重算、源码 zip 重打 | 已做 |

## 尚未落地（下次重建二进制时）

1. `scripts/publish-release.ps1` 的 `$shippedDocs` 白名单与 `scripts/package-common.ps1` 的 `Assert-PackagePayload` 必需文件清单**还没有**加入 `LICENSE`。本轮不重建安装包，改动门禁脚本无法验证，留到下次构建一并做，并重跑发布门禁。
2. 已发布的 `MiniProToolkit-Setup-x64-v0.1.0.exe` 构建于本次改动之前，安装向导里**没有**许可页；`LicenseFile=` 只对之后重新构建的安装包生效。
3. 代码签名仍未做（Setup 为 NotSigned）。

## 边界

- MIT 只授权本项目自身代码；`THIRD-PARTY-NOTICES.md` 列出的依赖仍按各自许可证，历史 `lite/` 子产品的许可证与本项目无关。
- 已按 MIT 提供的快照，其授权不可撤回；后续版本若换许可证，只约束新版本，别人已获得的授权不受影响。
- 文档中的验收边界（模式快照、单风扇/单显示器、未验收项）不因许可证变更而改变。
