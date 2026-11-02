# 主项目许可证：需要决策，本轮不创建 LICENSE（任务文档）

> 状态：**待用户/主代理决策，未落地**。本文件只记录只读核对到的事实、以及决策后要做的落地步骤。
> 本轮**没有**在仓库根创建 `LICENSE`，也没有在任何地方声明主项目许可证——因为决定许可证归属的两个事实（许可证种类、版权人/年份）在仓库里都拿不到，编造一个等于伪造法律授权。
> 本文件是仓库内部任务文档，**不随发布包分发**（不在 `scripts\publish-release.ps1` 的 `$shippedDocs` 白名单里）。

## 1. 历史只读核对到的事实（2026-11-01，目录清理前）

以下保留清理前的核对事实与原决策背景。`lite/`（包括许可证及 staging 复制品）现已删除；已删除的 lite 许可证不代表主项目授权，下述历史路径不是现存文件链接。许可证种类、版权人/年份仍待决策，本次不作法律选择。

| 检查项 | 结果 |
| --- | --- |
| 仓库根 `LICENSE` / `LICENSE.md` / `LICENSE.txt` / `COPYING` | **全都不存在**（全仓库递归搜索 `*license*`，排除 `research/`、`bin/`、`obj/`） |
| 清理前仓库里唯一的许可证文件（现已删除） | `lite/LICENSE`（外加 `lite/build/staging/**` 的复制品），内容是 **MIT**，版权行为 `Copyright (c) 2026 MiniProLite contributors` |
| 已删除的 `lite/LICENSE` 能否代表主项目 | **不能**。`docs/gap-list.md` §1.4 明确把 `lite/` 划为独立子产品（“不跟踪其功能、验收与发布产物”），§9 也写明“lite 相关决策一律不进入本清单”；`lite/LICENSE` 的版权人字符串是 `MiniProLite contributors`，不是 MiniProToolkit 的版权人 |
| `THIRD-PARTY-NOTICES.md` | 只覆盖**第三方依赖**（LibreHardwareMonitorLib 及传递程序集 MPL-2.0 / MIT / Apache-2.0 与 .NET 平台库），未声明主项目自身许可证，也不构成主项目授权 |
| `README.md` | 没有任何许可证声明或指向许可证文件的链接 |
| `src/**/*.csproj`、`Directory.Build.props` | 没有 `PackageLicenseExpression`、`PackageLicenseFile`、`Copyright`、`Authors`、`PackageProjectUrl` |
| `installer/MiniProToolkit.iss` | **没有** `LicenseFile=` 指令（对照：`lite/installer/MiniProLite.iss:28` 有 `LicenseFile=..\LICENSE`）→ 主产品安装器目前不展示任何许可页 |
| 已有决策记录 | `docs/gap-list.md` D-8 记为缺口、“需主代理决策许可证选择（见 §9-Q1）”；§9-Q1 的记录是“主项目是否采用与 lite 不同的许可证？…建议：先选一个宽松许可证（MIT 或 Apache-2.0），在 README 与发布说明中引用”——**建议不是决策** |

结论：许可证种类与版权人都是**未定事实**，不是“忘了写文件”。因此本轮不创建 `LICENSE`。

## 2. 决策后要做的落地步骤（按顺序）

1. 由用户确认两项输入：
   - 许可证种类（候选：MIT、Apache-2.0；若选 Apache-2.0 需要连同 `NOTICE` 与专利授权条款一并接受）；
   - 版权人字符串与年份（例如 `Copyright (c) 2026 <holder>`）。**不要**默认抄 `lite/LICENSE` 的 `MiniProLite contributors`。
2. 在仓库根新增 `LICENSE`（选 Apache-2.0 时另加 `NOTICE`），正文使用所选许可证的官方原文，不改写条款。
3. 同步引用点：
   - `README.md` 加一节“许可证”，链接 `LICENSE`；
   - `THIRD-PARTY-NOTICES.md` 顶部加一句“本文件只覆盖第三方组件；项目自身许可证见 `LICENSE`”，避免两者混淆；
   - `installer/MiniProToolkit.iss` 增加 `LicenseFile=..\LICENSE`，并把它加入 `[Files]` 不需要（Inno 只用 `LicenseFile` 做向导页，不复制到安装目录），但**必须**确认打包时 `LICENSE` 位于 `installer\` 的上一级（与 `lite` 的做法一致）；
   - 若要随包分发，把 `LICENSE` 加入 `scripts\publish-release.ps1` 的 payload 白名单（复制到 payload 根或 `docs\`）**并**同步 `scripts\package-common.ps1` 的 `Assert-PackagePayload` 必需文件清单，否则包内自检仍会通过而没有许可证文件。
4. 落地后把 `docs/gap-list.md` 的 D-8 改为“已收口（许可证 X，版权人 Y，落地于 `LICENSE`）”，并删掉 §9-Q1（已决策项不再挂在“需要用户决策”里）。
5. 重新发布前跑一次发布门禁（`scripts\publish-release.ps1`），它会用「包内文件必须与仓库逐字节一致」和「AST/能力符号/清单/归档/新鲜度」门禁复查新增文件。

## 3. 明确不做（本轮边界）

- 不自造许可证文本或改写条款；
- 不把 `lite/LICENSE` 复制成根 `LICENSE`（版权人与适用范围都不对）；
- 不在 `csproj` 里加 `PackageLicenseExpression` 猜测许可证；
- 不修改 `installer/MiniProToolkit.iss`（在许可证确定前加 `LicenseFile=` 会指向不存在的文件，直接打断安装器编译）；
- 不因为缺少 `LICENSE` 而阻塞其它发布门禁工作。

## 4. 交叉引用

- `docs/gap-list.md` D-8（缺口行）与 §9-Q1（待决策问题）；
- `THIRD-PARTY-NOTICES.md`（第三方声明，非项目自身许可证）；
- `docs/release-packaging.md`（发布门禁与 payload 文档白名单）。
