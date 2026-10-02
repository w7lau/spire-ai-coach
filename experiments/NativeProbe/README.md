# 原生本地战斗实验

这是独立的可行性原型，不是已接入游戏面板的本地求解器。正常安装的 Spire AI Coach 不引用此项目。试验使用用户本地合法安装的游戏及 Mod 的私有副本、私有用户目录与人工构造牌组，不读取玩家存档，不执行玩家当前战斗，不调用 AI。

## 2026-10-02 实测

构建 0 warning / 0 error，证据比较器 5 项测试通过。四次最终实验均完整运行至第二回合，游戏日志没有 `[ERROR]`。完整检查点见 [结果数据](results/2026-10-02.json)。

| 实验 | 子进程全程 | 出牌至下一回合 | 次回合 HP | 剩余敌人数 |
| --- | ---: | ---: | ---: | ---: |
| 血甲：放血、打击、防御、结束 | 55.04 s | 13.08 s | 77 | 3 |
| 从战前快照恢复，同路线 | 55.32 s | 13.16 s | 77 | 3 |
| 无血甲对照，同路线 | 53.00 s | 12.22 s | 77 | 3 |
| 恢复同快照：打击、打击、防御、结束 | 47.49 s | 9.47 s | 80 | 2 |

源场景和恢复场景的五个检查点逐字段、逐指纹完全相同。放血时血甲组获得 3 覆甲，对照组 0；两组都是 -3 HP、+2 能量。替代路线起点完全相同，打击目标为同一只 12 HP 小怪，第二击将它击杀。两条路线之后的牌序也不同，因此这里只比较实际结果，未证明哪条路线在整场战斗中最优。

日志仍有 Godot `Invalid Task ID`（资源预加载）及退出时 RID/resource 泄漏报告，已保留在结果中；这不是零错误运行，也未证明常驻进程可安全重复使用。最早两次探索分别暴露开局选择等待超时、新手教程导致回合循环异常；后者虽能打牌也不计入最终成功样本。仅在私有测试档关闭教程、直接建立固定场景后，才取得上述完整回合证据。

真实加载了 HextechRunes，登记了 469 个遗物模型，但仅实测此处的一个符文效果。游戏 exe、sts2.dll、HextechRunes 0.111.0 实现、RitsuLib 0.111.0 实现的私有副本与原安装哈希一致；实验前后所监测的原游戏/海克斯文件未变化。没有将探针安装到正常游戏目录。

结论：复用真实游戏和 Mod 来做局部复现已获得实证；目前速度不足以直接穷举，更没有“所有 Mod 兼容”或“实时全局最优”的证据。下一步应先验证常驻工作进程的低成本恢复与分支隔离，再开发搜索和实战接线。

## 验证什么

- 在真实 Godot/.NET 游戏进程中加载 HextechRunes 和 RitsuLib，走原生出牌队列和战斗钩子。
- 铁甲战士、固定种子、两张打击、两张防御、一张放血，进入 `SLIMES_WEAK`。
- 持有 `BloodArmorRune` 时，放血失去 3 HP 后，由原 Mod 实际施加 3 层覆甲；探针没有实现这条效果的计算规则。
- 将合成的战前局面通过游戏原生 `SerializableRun` 保存，在另一个进程中恢复；逐步比较投影和 `NetFullCombatState` 序列化指纹。
- 比较同一战前快照下的不同出牌路线，执行敌人行动直至第二回合可操作。

`NetFullCombatState` 包含原生 RNG、牌堆、生命、能量及已序列化遗物数据，但不是整个进程的完整状态。它不能证明任意 Mod 的静态变量、私有字段、异步任务、额外随机源、外部文件或自定义 UI 都可恢复。注册了某个遗物也不代表已验证它。

## 运行

需要 Windows、.NET SDK 9、Python 3.11+，以及本机游戏 v0.111.0、HextechRunes 0.9.7、RitsuLib 0.6.5。其它版本未验证。游戏二进制、PCK 和合成存档不随仓库分发。

先编译，`GameDir` 指向游戏原安装目录：

```powershell
dotnet build experiments/NativeProbe/NativeProbe.csproj -c Release '-p:GameDir=R:\SteamLibrary\steamapps\common\Slay the Spire 2'

$probeArgs = @(
  '--game', 'R:\SteamLibrary\steamapps\common\Slay the Spire 2',
  '--hextech', 'R:\SteamLibrary\steamapps\workshop\content\2868840\3747501308',
  '--ritsu', 'R:\SteamLibrary\steamapps\workshop\content\2868840\3747602295',
  '--workspace', 'C:\path\outside-repository\native-worker'
)
python experiments/NativeProbe/run_probe.py @probeArgs --name source --mode route --blood-armor --actions BLOODLETTING STRIKE_IRONCLAD DEFEND_IRONCLAD END_TURN
python experiments/NativeProbe/run_probe.py @probeArgs --name replay --mode route --restore-fixture --blood-armor --actions BLOODLETTING STRIKE_IRONCLAD DEFEND_IRONCLAD END_TURN
python experiments/NativeProbe/run_probe.py @probeArgs --name alternative --mode route --restore-fixture --blood-armor --actions STRIKE_IRONCLAD STRIKE_IRONCLAD DEFEND_IRONCLAD END_TURN
python experiments/NativeProbe/run_probe.py @probeArgs --name control --mode route --actions BLOODLETTING STRIKE_IRONCLAD DEFEND_IRONCLAD END_TURN
```

顺序重要：创建新合成场景会覆盖工作目录中的 `fixture.json`；恢复和替代路线必须在对照场景之前运行。每次 `--name` 必须不同。同一个工作目录只允许串行运行。复制约 3 GB 本地资源，复制不会使用硬链接；不要把工作目录放入仓库或原游戏目录。

独立档关闭新手教程，合成场景直接进入战斗，跳过开局事件/符文选择 UI。这个差异是实验边界，不可当作实战开局已复现。牌和目标按模型 ID 与固定牌组的先后规则选择；这不是通用动作协议，无法区分任意升级、附魔或动态实例，应在实战接入前替换。

证据在 `<workspace>/evidence/<name>/`，含请求、结果、游戏日志、加载程序集清单和计时。`wall_ms` 从启动子进程计至退出，不含复制及哈希时间；`action_ms` 包括出牌动画/原生等待、敌方回合及检查点采集，不是纯规则吞吐量。超时仅结束本次启动的子进程。

```powershell
python experiments/NativeProbe/verify_probe.py --source C:\path\native-worker\evidence\source --replay C:\path\native-worker\evidence\replay --alternative C:\path\native-worker\evidence\alternative --control C:\path\native-worker\evidence\control --output C:\path\comparison.json
python -m unittest discover -s experiments/NativeProbe -p 'test_*.py'
```

比较器拒绝失败/超时、缺失指纹、不同快照、不同构建和重放偏差。游戏 `[ERROR]` 使该次验证失败；Godot `ERROR:` 单独保留，不能把结果文件成功等同于运行日志零错误。

## 距离可用求解器还有什么

1. 从真实战斗开始捕获战前快照及全部玩家选择，在工作进程重放至当前决策点；校验不一致时不给“精确指导”。当前没有实战捕获接线。
2. 处理选牌、目标、药水、生成牌、死亡/胜利、Mod 自定义选择及额外状态；当前只支持此有限测试路线。
3. 在常驻进程内验证清理与恢复，防止上一分支的 Mod 静态状态污染下一分支，解决无窗口资源/退出日志问题。
4. 测量并减少画面和等待开销，再实现动作枚举、去重、剪枝与时间预算。启发式搜索只能称“预算内最好”；只有有限、完整且精确搜索完成才可称该目标下最优。
5. 明确评分：存活、生命损失、击杀、伤害、药水消耗和后续牌序可能相互冲突，不能只最大化本回合伤害。当前没有自动搜索或评分策略。

当前观察到的原生执行速度不适合逐个启动进程穷举。复用原规则有助于兼容，但“任意 Mod + 即时结果 + 保证全局最优”仍不是已实现承诺。

## 参考

- [CombatSolver](https://github.com/Torch1230/CombatSolver) 的原生 worker、战前恢复与 Mod adapter 设计；检查版本 `382a496d894ebc409fd09ff805bcb4844fc93e55`。其高速搜索还有镜像规则层及适配要求。
- [sts2-cli](https://github.com/wuhao21/sts2-cli) 的命令行路径。其替代 Godot 与测试模式不能直接作为本次真实 Mod 兼容性的证明。

本实验没有分发上述项目代码或游戏反编译源码；使用公开/本地程序集 API 编写独立探针。
