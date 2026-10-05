# 尖塔 AI 教练 · Spire AI Coach

[English](README.en.md) · [创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3814010843) · [下载](https://github.com/w7lau/spire-ai-coach/releases/latest) · [反馈](https://github.com/w7lau/spire-ai-coach/issues)

《杀戮尖塔 2》的战斗助手，提供 **AI 本回合指导**、**本地整场战斗规划**和**自动执行方案**。用于娱乐、研究与比较打法。

## 使用

1. 订阅创意工坊，启动游戏并启用 Mod；也可将下载包中的 `SpireAiCoach` 文件夹放入游戏的 `mods` 目录。两种安装方式选一种。
2. 按 **F8** 打开或收起面板。
3. 在“本地整场计算”选择“路线采样算法”或“分支扩展算法”。搜索中已找到胜利路线时，可点击“停止并使用胜利路线”，按当前复核设置取得方案；“取消”会放弃本次计算。获得方案后可手动照做，或点击“执行方案”；**Esc 停止执行**。
4. 使用 AI 时，在“AI 指导与设置”填写 Chat Completions 兼容服务的 URL、模型和 API Key，保存后点击“AI 分析”。

## 本地战斗规划

两种算法都从当前状态计算整场战斗，无需 API。

| 算法 | 搜索方式 |
| --- | --- |
| **路线采样 / Rollout Sampling** | 反复试算整场路线，调整出牌顺序、目标和选牌，比较完整结局 |
| **分支扩展 / Branch Expansion** | 展开不同操作分支，按回合安排后续探索，再比较整场结局 |

- 优先提高战后实际生命，计入战中和战后回血及高于起点的收益；同血量优先保留药水。
- 可设置并发、尝试次数、搜索时间、回合上限、提前返回条件，以及用药、补刀和目标牌选项。
- 可查看进度、路线与耗时，并继续搜索。
- 提前返回支持战后满血，或严格证明已取得最高战后血量和同血量下的最低用药；净损血为 0 本身不代表回血已最优。
- 自动执行支持单人战斗；实际状态偏离方案时停止。

为尽力兼容不同 Mod，本地搜索尽量复用游戏底层规则和已加载的 Mod 钩子来模拟出牌与结算，减少逐卡适配。

## AI 指导

根据手牌、牌堆、能量、生命、Buff、遗物、药水和敌人意图给出本回合建议，支持流式回复。

点击“AI 分析”会向你配置的服务商发送战斗信息，可能产生 API 费用。API Key 可选择加密保存在本机。

## 兼容范围

已验证 **Windows x64 / 游戏 v0.111.0**，界面主要为中文。部分 Mod 可能需要额外适配。搜索受时间和次数限制，结果不保证全局最优。

## 欢迎优化算法

欢迎通过 Issue / PR 改进算法，争取以最快的方式找到损伤最低、更接近或可证明最优的完整获胜路线。参与方法见 [贡献指南](CONTRIBUTING.md)。

## 开发

需要 .NET 9 SDK；编译 Mod 需要本机安装的游戏。

```powershell
./scripts/Update.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
```

这一个入口完成核心检查、打包和安装；相同源码与 SDK 复用已通过的检查，相同包核对哈希后复用。首次成功后记住本机游戏路径，之后直接运行 `./scripts/Update.ps1`。游戏运行时保留已准备的包，退出后可用 `-InstallOnly` 直接安装；每个阶段都会记录耗时。需要原生验收时先用 `-BuildOnly` 准备，详见[更新流程](docs/local-update.md)。

[架构](docs/architecture.md) · [搜索策略](docs/local-search-policy.md) · [性能](docs/local-simulation-performance.md) · [验证记录](docs/validation.md)

## 参考与许可

开发参考了 CombatSolver、autoSpire、SpireVibePlaying、ModTemplate-StS2 等项目，详见 [致谢与来源](ACKNOWLEDGEMENTS.md)。

原创代码采用 [MIT 许可证](LICENSE)。本项目是独立社区 Mod。
