# 调研依据 · 2026-10-02

这些项目用于确认可行性与接口模式。本项目独立实现，没有导入其求解器或整套源码。

| 项目 | 与本项目的关系 |
| --- | --- |
| [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2) | 社区 Mod 起步模板；可以移除不需要的 BaseLib 依赖 |
| [BaseLib-StS2](https://github.com/Alchyr/BaseLib-StS2) | 通用内容扩展库，本项目第一版无需依赖 |
| [SpireVibePlaying](https://github.com/Wuxie233/SpireVibePlaying) | 最接近的参考方向：牌堆、AI 建议、游戏内面板；据此确认原生加载方式，接口再以本机程序集核实 |
| [EnemyCycle](https://github.com/sts2mods/EnemyCycle) | 敌人循环和后续意图。公开预测实现会临时写入真实 StateLog 再恢复；本项目没有复用这条路径，仅读取固定连接 |
| [StS2.RandomForeseer](https://github.com/hotwords123/StS2.RandomForeseer) | 包含抽牌／洗牌预测方向，后续可研究快照适配；当前没有集成 |
| [RollTheSpire2](https://github.com/FalseOcean/RollTheSpire2) | 种子搜索和局势预测，超出第一版当前回合指导范围 |

本地接口核对：游戏 `v0.111.0`，release commit `41cef1ea`，Steam build `24724944`。验证了 ModInitializer、ModManifest、CombatManager、LocalContext、CardPile、CardEnergyCost、CardModel 的描述／合法性／预览接口，以及敌人意图与状态机连接。

`CardPile.Cards[0]` 是顶部。`CardModel.GetDescriptionForPile(target)` 本身不是完整的针对目标重算；因此目标数值通过 `UpdateDynamicVarPreview` 对独立 `DynamicVarSet` 副本计算。药水和卡牌的 Self 目标语义不同，不直接共用游戏执行路径。

本机游戏实现只用于合法安装环境中的接口核实，未把游戏程序集或反编译源码提交到仓库。
