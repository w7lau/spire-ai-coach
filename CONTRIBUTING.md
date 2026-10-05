# 参与算法优化 · Contributing

欢迎中文或英文 Issue / PR。我们希望更快找到完整获胜路线，并尽可能取得或证明最小生命损失，而不牺牲真实游戏与 Mod 规则。

Chinese and English issues and PRs are welcome. We aim to find complete winning plans faster and approach or prove minimum HP loss while preserving actual game and mod behavior.

## 从哪里入手 / Entry points

| 方向 / Area | 文件 / Files |
| --- | --- |
| 路线采样 / Rollout sampling | `LocalSearchTree.cs`, `LocalRolloutPolicy.cs`, `LocalRouteRefiner.cs` |
| 分支扩展 / Branch expansion | `LocalTurnSearch.cs`, `LocalTurnWork.cs` |
| 公共前缀、任务分发 / Shared prefixes and work distribution | `LocalSearchWorkBroker.cs`, `LocalTurnWork.cs`, `LocalWorker.cs`, `LocalWorkerPool.cs` |
| 原生数值模拟与展示省略 / Native simulation and presentation overhead | `LocalWorkerLogic.cs`, `LocalWorkerDataMode.cs`, `LocalWorkerOverhead.cs`, `LocalModelDisplay.cs` |
| 损失、回血、停止条件 / HP accounting and proven stopping bounds | `LocalHealthAccounting.cs`, `LocalMinimumLossProof.cs`, `LocalRecoveryEstimator.cs` |
| 泛用选牌、抽弃消耗 / Generic selections, draw, discard and exhaust | `LocalChoices.cs`, `LocalSelectionSpace.cs`, `LocalDiscardLearning.cs` |

纯搜索和合同在 `src/SpireAiCoach.Core`，游戏适配在 `src/SpireAiCoach.Mod`。首次修改请阅读 [架构](docs/architecture.md)、[搜索策略](docs/local-search-policy.md) 和对应验证记录。

Core search/contracts live in `src/SpireAiCoach.Core`; game adapters live in `src/SpireAiCoach.Mod`. Read the architecture, search-policy and relevant validation notes before changes.

## 怎样证明改进 / Demonstrating improvements

- 使用相同冻结输入、游戏／Mod 版本、目标、并发、次数和时间预算比较。Compare identical frozen inputs, versions, objectives, concurrency and budgets.
- 分别报告准备、搜索、可选复核时间，以及完整候选、获胜候选、战后生命、药水消耗、停止原因与搜索覆盖情况。Report preparation/search/verification timing, complete and winning candidates, final HP, potions, stop reason and coverage.
- 搜索更快和打法更好分别验证；达到预算不等于证明最优。Validate speed and quality separately; a budget limit is not an optimality proof.
- 状态复用与剪枝需保留 RNG、牌序、选择、触发顺序及 Mod 私有状态；无法证明等价或损失下界时，作为排序提示而非删掉合法操作。Preserve RNG, pile order, selections, hook order and mod state. Unproven equivalence or bounds should guide ordering, rather than remove legal actions.
- 不用缩短预算、漏掉合法操作或忽略回血／药水／Mod 效果来制造提速。Do not manufacture speedups by reducing budgets or omitting actions or effects.

## 验证与提交 / Validation and submissions

核心变更运行 `dotnet run --project tests/SpireAiCoach.Tests -c Release`；游戏适配变更还需使用自己的合法安装编译。原生检查限拥有的隔离进程，不能在真实玩家进度上自动试牌、改存档或修改 RNG。

Run core checks for core changes and build against your own installation for game adapters. Native experiments must use owned isolated processes, never automatically play a real user's run or edit saves/RNG.

请在 PR 中说明问题、前后行为、验证及未覆盖范围。不要提交游戏 DLL/PCK、反编译游戏源码、存档、API Key 或真实配置。可提供脱敏后的小型结果与复现步骤；引用其他项目的思路或代码时，明确来源并遵守其许可证。

Describe the problem, behavior change, validation and remaining limits. Do not commit game DLLs/PCKs, decompiled source, saves, keys or real configurations. Use small sanitized results and reproduction steps. Credit external ideas/code and follow their licenses.
