# 尖塔 AI 教练 · Spire AI Coach

[English](README.en.md) · [创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3814010843) · [下载](https://github.com/w7lau/spire-ai-coach/releases/latest) · [反馈](https://github.com/w7lau/spire-ai-coach/issues)

《杀戮尖塔 2》的战斗助手，提供 **AI 本回合指导**、**本地整场战斗规划**和**自动执行方案**。用于娱乐、研究与比较打法。

## 使用

1. 订阅创意工坊，启动游戏并启用 Mod；也可将下载包中的 `SpireAiCoach` 文件夹放入游戏的 `mods` 目录。两种安装方式选一种。
2. 按 **F8** 打开或收起面板。
3. 本地计算选择“路线采样”或“分支扩展”。获得方案后可手动照做，或点击“执行方案”；**Esc 停止执行**。
4. 使用 AI 时，在“AI 指导与设置”填写 Chat Completions 兼容服务的 URL、模型和 API Key，保存后点击“AI 分析”。

## 本地战斗规划

两种算法都从当前状态计算整场战斗，无需 API。

| 算法 | 搜索方式 |
| --- | --- |
| **路线采样 / Rollout Sampling** | 试走完整路线，根据已有结果调整探索方向 |
| **分支扩展 / Branch Expansion** | 按回合组织分支，向后续回合扩展 |

- 优先降低战后净生命损失，计入战中和战后回血；同等损失优先保留药水。
- 可设置并发、尝试次数、搜索时间、回合上限、提前返回条件，以及用药、补刀和目标牌选项。
- 可查看进度、路线与耗时，并继续搜索。
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
dotnet run --project tests/SpireAiCoach.Tests -c Release
./scripts/Build.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
```

[架构](docs/architecture.md) · [搜索策略](docs/local-search-policy.md) · [性能](docs/local-simulation-performance.md) · [验证记录](docs/validation.md)

## 参考与许可

开发参考了 CombatSolver、autoSpire、SpireVibePlaying、ModTemplate-StS2 等项目，详见 [致谢与来源](ACKNOWLEDGEMENTS.md)。

原创代码采用 [MIT 许可证](LICENSE)。本项目是独立社区 Mod。
