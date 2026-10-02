# 原生本地战斗实验

这是独立的可行性原型，不是已接入游戏面板的本地求解器。正常安装的 Spire AI Coach 不引用此项目。试验使用用户本地合法安装的游戏及 Mod 的私有副本、私有用户目录与人工构造牌组，不读取玩家存档，不执行玩家当前战斗，不调用 AI。

## 同进程恢复与并行扩展

`resident` 模式在一个进程内按 A→B→A→B→A 连续执行五条路线。A 为放血、打击、防御；B 为两次打击、防御；每条均执行结束回合。分支之间调用游戏原生清理/返回主菜单，等待节点释放，再从同一份战前快照重建。每次根状态及同一路线的五个检查点都必须相同，否则记录部分证据并停止该批次。

`--time-scale 4` 仅将独立工作进程的 Godot 时间速度设为 4，保留原生动作队列、遗物钩子和敌方回合；并未把所有等待直接删除。加速状态必须通过 `verify_resident.py` 与正常速度逐字段和逐指纹对照。真实时间、帧数或非原生随机数会影响某些 Mod，当前通过的固定场景不能代表这些 Mod。

更有效的选项是 `--instant --keep-assets`：前者使用游戏原生 `FastModeType.Instant` 跳过其支持的表现等待，后者清理上一条路线的运行状态与场景，保留进程和资源缓存，避免反复加载主菜单。角色、卡牌、战斗 UI 节点仍重建；这不是完整节点池或内存快照回滚。`--skip-transitions` 使用原生房间入口的关闭淡入选项，本场景未测到明显额外收益。

节点不能只更换一个战斗引用：原生 `NRun` 的 UI 初始化绑定玩家，`NCombatRoom` 持有旧房间、角色节点及事件订阅，并没有通用的重新绑定接口。另试过真实 Mod 加载完成后启用内部 `TestMode`，在建战时因 `Backend should not be subscribing to CombatStateChanged!` 失败。检查发现此模式还停用状态通知及该通知路径的卡牌数值重算，不能将其视为只关闭画面。失败路径已撤回，没有删除订阅者来绕过报错；接下来应验证完整重新绑定或仅隔离表现层。

`run_pool.py` 用 Python 线程调度两个独立的游戏进程。每个进程有自己的游戏副本、用户目录、动作队列、单例及静态变量；单个 Godot 进程内仍顺序执行游戏操作。`--parallelism 1` 是同样两个批次的串行对照，`--parallelism 2` 同时执行两个批次。两者均从 `--fixture-from` 指定的同一合成快照复制，比较器同时检查快照哈希及完整路线结果。

```powershell
python experiments/NativeProbe/run_probe.py @probeArgs --name resident-normal --mode resident --blood-armor
python experiments/NativeProbe/run_probe.py @probeArgs --name resident-fast --mode resident --blood-armor --restore-fixture --time-scale 4
python experiments/NativeProbe/run_probe.py @probeArgs --name resident-instant-cache --mode resident --blood-armor --restore-fixture --instant --keep-assets

# 用此前的 game / hextech / ritsu 参数，另选独立的 pool 工作目录。
python experiments/NativeProbe/run_pool.py --game 'R:\SteamLibrary\steamapps\common\Slay the Spire 2' --hextech 'R:\SteamLibrary\steamapps\workshop\content\2868840\3747501308' --ritsu 'R:\SteamLibrary\steamapps\workshop\content\2868840\3747602295' --workspace 'C:\path\native-pool' --fixture-from 'C:\path\outside-repository\native-worker\fixture.json' --name parallel-fast --parallelism 2 --time-scale 4
# 将上条命令的 name 改为 serial-fast、parallelism 改为 1，取得串行对照。

python experiments/NativeProbe/verify_resident.py --normal C:\path\native-worker\evidence\resident-normal\summary.json --fast C:\path\native-worker\evidence\resident-fast\summary.json --pool C:\path\native-pool\parallel-fast.json --serial-pool C:\path\native-pool\serial-fast.json --output C:\path\resident-comparison.json
```

同一 worker 目录有 Windows 文件锁，重复启动会拒绝；不同 worker 可同时运行。每个原生进程最多运行 240 秒，超时只终止对应子进程。进度和部分分支分别保存在 `progress.json`、`branches.json`，日志中的游戏错误与 Godot 引擎错误分别保留。

这是有界批次验证，尚无实时 IPC 服务、动态分支工作队列、自动动作枚举、搜索评分或 UI 接线。两个 worker 执行重复的 A/B 路线，是为了验证并发一致性和测量吞吐量，并不等于已搜索十条不同策略。

## 常驻与加速实测（2026-10-02）

证据见 [常驻结果数据](results/2026-10-02-resident.json)。每批为固定 A→B→A→B→A；比较器直接检查各次根状态、同一路线的五个完整检查点、原生同步/RNG 指纹及冻结快照哈希，不依赖探针自报的相等标志。编译 0 警告 / 0 错误，比较器与工作目录锁共 16 项测试通过。

| 模式 | 热分支整轮中位耗时 | 热分支三张牌至下一回合 |
| --- | ---: | ---: |
| 常驻，正常速度 | 17.59 s | 9.45–13.10 s |
| 常驻，时间速度 4 倍 | 6.37 s | 2.72–3.62 s |
| 原生 Instant | 2.99 s | 0.182–0.185 s |
| 原生 Instant，保留资源 | 约 1.5 s | 约 0.18–0.19 s |

整轮包括清理、恢复、场景搭建与出牌。第一条路线、游戏启动、复制和文件校验不在热分支指标内。Instant 的出牌指标仍含动作队列、帧调度及状态采集，不能称为纯规则执行耗时。当前主要开销已转为恢复和场景搭建。

时间速度 4 倍的两个批次串行总耗时 137.42 s，并行 75.99 s，含启动、复制及校验，单次测得 1.808 倍吞吐提升。并行组首次创建工作目录，串行组复用了目录，且只有一次对照，不能据此承诺其他机器或 Instant 模式的加速比。资源预加载、无窗口纹理及退出资源泄漏错误仍单独记录；五条路线通过不证明无限期运行没有状态残留或内存增长。

最新构建的 Instant+资源复用双进程批次总耗时 48.58 s（含启动、文件准备及退出），10 次路线的完整检查点和原生指纹全部匹配正常模式。没有对应的 Instant 串行批次，不另报并行加速比。启动开销尚未通过常驻 IPC 摊销到多次请求。

## 2026-10-02 实测

构建 0 warning / 0 error，证据比较器 5 项测试通过。四次最终实验均完整运行至第二回合，游戏日志没有 `[ERROR]`。完整检查点见 [结果数据](results/2026-10-02.json)。

| 实验 | 子进程全程 | 出牌至下一回合 | 次回合 HP | 剩余敌人数 |
| --- | ---: | ---: | ---: | ---: |
| 血甲：放血、打击、防御、结束 | 55.04 s | 13.08 s | 77 | 3 |
| 从战前快照恢复，同路线 | 55.32 s | 13.16 s | 77 | 3 |
| 无血甲对照，同路线 | 53.00 s | 12.22 s | 77 | 3 |
| 恢复同快照：打击、打击、防御、结束 | 47.49 s | 9.47 s | 80 | 2 |

源场景和恢复场景的五个检查点逐字段、逐指纹完全相同。放血时血甲组获得 3 覆甲，对照组 0；两组都是 -3 HP、+2 能量。替代路线起点完全相同，打击目标为同一只 12 HP 小怪，第二击将它击杀。两条路线之后的牌序也不同，因此这里只比较实际结果，未证明哪条路线在整场战斗中最优。

日志仍有 Godot `Invalid Task ID`（资源预加载）及退出时 RID/resource 泄漏报告，已保留在结果中；这不是零错误运行；首阶段未验证常驻复用，后续有限批次结果见前节。最早两次探索分别暴露开局选择等待超时、新手教程导致回合循环异常；后者虽能打牌也不计入最终成功样本。仅在私有测试档关闭教程、直接建立固定场景后，才取得上述完整回合证据。

真实加载了 HextechRunes，登记了 469 个遗物模型，但仅实测此处的一个符文效果。游戏 exe、sts2.dll、HextechRunes 0.111.0 实现、RitsuLib 0.111.0 实现的私有副本与原安装哈希一致；实验前后所监测的原游戏/海克斯文件未变化。没有将探针安装到正常游戏目录。

首阶段结论：复用真实游戏和 Mod 来做局部复现已获得实证；上述逐进程启动方式不足以直接穷举，没有“所有 Mod 兼容”或“实时全局最优”的证据。同进程恢复和并行扩展见前节。

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

下一阶段按 [默认搜索停止规则](../../docs/local-search-policy.md) 实现满意解：完整回合无伤、无待争取或未知奖励收益且无额外玩家目标时可提前停止；玩家可选择继续优化。此规则尚未接入当前固定路线原型。

1. 从真实战斗开始捕获战前快照及全部玩家选择，在工作进程重放至当前决策点；校验不一致时不给“精确指导”。当前没有实战捕获接线。
2. 处理选牌、目标、药水、生成牌、死亡/胜利、Mod 自定义选择及额外状态；当前只支持此有限测试路线。
3. 扩大同进程恢复的验证范围，覆盖带私有计数器和静态缓存的 Mod，检查长期内存使用，解决无窗口资源/退出日志问题。当前 A/B 重放只验证固定场景。
4. 建立实时工作队列，再实现动作枚举、去重、剪枝与时间预算。启发式搜索只能称“预算内最好”；只有有限、完整且精确搜索完成才可称该目标下最优。
5. 明确评分：存活、生命损失、击杀、伤害、药水消耗和后续牌序可能相互冲突，不能只最大化本回合伤害。当前没有自动搜索或评分策略。

当前观察到的原生执行速度不适合逐个启动进程穷举。复用原规则有助于兼容，但“任意 Mod + 即时结果 + 保证全局最优”仍不是已实现承诺。

## 参考

- [CombatSolver](https://github.com/Torch1230/CombatSolver) 的原生 worker、战前恢复与 Mod adapter 设计；检查版本 `382a496d894ebc409fd09ff805bcb4844fc93e55`。其高速搜索还有镜像规则层及适配要求。
- [sts2-cli](https://github.com/wuhao21/sts2-cli) 的命令行路径。其替代 Godot 与测试模式不能直接作为本次真实 Mod 兼容性的证明。

本实验没有分发上述项目代码或游戏反编译源码；使用公开/本地程序集 API 编写独立探针。
