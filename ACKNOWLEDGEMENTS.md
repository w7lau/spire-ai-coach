# 参考项目与致谢 · References and acknowledgements

以下区分参考思路、可选研究方向和运行依赖。项目独立实现，未导入下列社区项目的求解器源码，也不要求用户安装这些参考 Mod。

These are research references, optional future directions, and runtime dependencies. The coach was implemented independently; the community solvers below were not imported and are not required mods.

| 项目 / Project | 参考内容 / Relationship |
| --- | --- |
| [CombatSolver](https://github.com/Torch1230/CombatSolver) | 搜索组织、原生操作后的状态评估、选牌／药水分支与第三方适配边界。Search organization, states after native actions, selection/potion branches and adapter boundaries. |
| [autoSpire](https://github.com/LightEnding/autoSpire) | 自动出牌项目的原生动作和选牌接口；未采用外部命令服务器。Native action and selection interfaces; no external command server was adopted. |
| [SpireVibePlaying](https://github.com/Wuxie233/SpireVibePlaying) | AI 战斗建议、牌堆信息和游戏内面板的可行性与加载方式。AI advice, pile inspection, UI and initialization reference. |
| [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2) | 社区 Mod 加载与项目组织的起步参考。Community initialization and project layout reference. |
| [BaseLib-StS2](https://github.com/Alchyr/BaseLib-StS2) | 调研通用扩展库，当前不是本项目依赖。Extension-library research; not a current dependency. |
| [EnemyCycle](https://github.com/sts2mods/EnemyCycle) | 后续意图研究；未复用其临时修改真实战斗日志的预测路径。Future-intent research; its live-log mutation path was not reused. |
| [StS2.RandomForeseer](https://github.com/hotwords123/StS2.RandomForeseer) | 抽牌与洗牌预测调研，当前未集成。Draw/shuffle prediction research, not integrated. |
| [RollTheSpire2](https://github.com/FalseOcean/RollTheSpire2) | 种子与局势预测调研，当前未集成。Seed/state prediction research, not integrated. |
| [官方 sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader) | Steam 创意工坊发布工具，不包含在教练运行包内。Official Workshop publishing utility, not bundled in the coach. |

研究记录见 [docs/research.md](docs/research.md) 与 [docs/validation.md](docs/validation.md)。曾核对 [autoSpire GameHookServer](https://github.com/LightEnding/autoSpire/blob/0f6b862c88af4e4bd4452a0f98de936126984c6b/scripts/core/GameHookServer.cs)，以及 CombatSolver 的 [药水选择续接](https://github.com/Torch1230/CombatSolver/blob/1a3d1a3747953b5145b2f38809d49f882c7271c1/src/Search/CombatBeamSolver.PotionChoiceContinuation.cs) 与 [第三方适配说明](https://github.com/Torch1230/CombatSolver/blob/1a3d1a3747953b5145b2f38809d49f882c7271c1/docs/THIRD_PARTY_ADAPTERS.md)。这些链接记录参考来源，不代表当前发布集成了其源码。

Native game APIs and GodotSharp are supplied by the user's Slay the Spire 2 installation. [Harmony](https://github.com/pardeike/Harmony), also referenced from that installation, provides runtime patching. They retain their own licenses and are not redistributed here. 原生游戏、GodotSharp 与 Harmony 使用本机游戏引用，不作为本项目原创代码重新授权。

感谢上述作者，也欢迎在使用本项目改进方案时保留来源说明。
