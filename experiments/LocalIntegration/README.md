# 本地指导集成测试

此测试 Mod 只在带 NativeProbe 所有权标记的私有游戏副本中运行，不安装到玩家游戏目录。默认加载合成 `fixture.json`，经正常地图入口进入战斗，在开局、出牌后和下一回合三个点调用与面板相同的 LocalCapture / LocalWorkerPool API，验证搜索及宿主状态不变。另测取消和错误根指纹拒绝。后台仍加载真实 HextechRunes / RitsuLib；真实故障重放只在显式传入冻结记录时运行。

需要先按 NativeProbe 文档准备人工牌组及私有工作目录。宿主副本独立；后台通过同盘 NTFS 硬链接共享游戏资源，配置、Mod、存档和日志各自独立。版本变化会新建 worker 缓存。测试不直接读取或修改玩家存档。

```powershell
dotnet build experiments/LocalIntegration/LocalIntegration.csproj -c Release '-p:GameDir=R:\SteamLibrary\steamapps\common\Slay the Spire 2'
python experiments/LocalIntegration/run.py --workspace C:\path\owned-native-worker
```

默认每进程/每阶段最多评估 6 条路线；`--quick` 改为 2 条，其余恢复、取消、偏差检查不变。`integration-result.json` 保存各阶段结果，成功还需 `integration-success` 文件及退出码为 0；失败详情在 `integration-error.txt`，引擎日志另外保留。测试 DLL 不进入正式安装包。

定向用例：

- `--runtime-failure-test --replay <private-request.json> --game <installed-game> --mods <matching-frozen-mods>`：两种算法在两个所属实例中各进行最多2条/20秒的有限搜索。仅在精确所有权保护的测试Mod中，于真实原生动作结算后输出一次原生Error；检查父进程及时退役该拥有者、保留原始消息/栈及健康实例候选，随后只重建失败实例并复用健康进程/代次。每个采用的候选另在普通场景完整独立复放。单实例错误负例须返回结构化拒绝且不启动普通兼容搜索。此检查验证处理路径，不改变原生规则，也不冒充符文异步冲突已修复或完整八路性能基准。

- `--transport-reuse-test --replay <private-request.json> --game <installed-game> --mods <matching-frozen-mods>`：单个所属常驻原生实例依次运行采样/分支扩展/采样，每次2条、20秒预算，再独立普通场景复放最佳候选。只在所属测试观察器中让结果发布持锁6秒，核对不会误判原生失败或重建，并保持同一PID/代次及完整原生检查点。此例修改实验搜索范围以隔离通信容错，不作为原完整预算的性能比较；真实输入、配置、路线及日志不得公开。

- `--enemy-test --enemy-rounds 8`：运行原生实际遭遇目录，对比普通场景与两种数值算法的固定结束回合前缀。`--enemy-attack --enemy-rounds 32` 改为每种算法两次不带答案的短搜索，再将最佳候选在普通场景独立复放；未获胜仍须原生检查点一致。`--enemy-cases <native-encounter-id,...>` 限定遭遇，未知 ID 直接失败。`--enemy-seed <seed>` 通过原生新开局 API 生成合成根，不直接修改 RNG；`--enemy-seeds <seed,...>` 在同一所属宿主中逐一检查最多32个原生种子，减少变体检查的重复冷启动。`--enemy-character <native-character-id>` 同时保留原生初始遗物，亡灵契约师用例要求奥斯蒂已实际生成。测试观察器记录真实创建、行动、死亡请求和逃跑请求，并检查位置审查拒绝新增规则调用或全局写入；原始目录、请求、路线和日志仍只保存在忽略目录。不得将普通模式回退算作数值通过，`--enemy-fast-only` 仅作诊断。

- `--snapshot-metadata-test --replay <private-request.json> --seed-result <private-result.json> --game <installed-game> --mods <matching-frozen-mods>`：同一个所属常驻实例交替开启/关闭原生快照元数据复用，两次预热后八次测量相同15步已知路线；比较完整状态、RNG、操作/选牌历史、逐步生命与结算，再独立常规复核。检查反射读取和关键词数组复制实际减少，没有更换算法或测量自主搜索质量。原始记录留在忽略目录；结果需去除真实输入、身份、路线和本机路径再公开。

- `--finisher-targets-test`：普通敌人、原生爪牙、外部Power禁止Fatal、混合敌人的最后一击和没有符合资格目标的五类原生检查。前四类实际出牌核对补刀奖励、计数及合法伤害分支；最后一类检查当前排序与账本，因为全是爪牙的原生战斗可能直接结束。此检查不运行整场搜索，也不作为任意Mod私有奖励兼容证明。

- `--selection-paging-test`：336 个原生有序选牌组合跨分页重放及两路并行预热/复用；带 `--replay` 时改为该冻结输入的一次搜索，保留原回合、时间和次数选项，检查没有因选牌分页切换执行方式。此病例是正确性与启动调度验证，不是八路等配置质量/速度对比。
- `--mechanics`：通用牌序调整、坚毅＋消耗手牌、普通武装升级、准备＋多选弃牌、头槌弃牌堆取牌，以及超过十回合的胜利复核。可用 `--mechanic-cases exhaust,multi` 只运行指定流程。选择用例还在合成宿主显式执行方案，重新采集有选择历史的当前状态并再次计算。长战斗用例使用 1000 HP 人工角色、前十一回合结束回合的搜索种子，专门验证回合范围，不作真实打法或性能基准。
- `--choices`：生成卡牌选择、方案执行、结束回合及已有选择历史重新计算。
- `--execution` / `--optimization` / `--features`：执行保护、路线续用、药水与进度。
- `--fallback`：保留旧命令名称；原先的普通武装失败夹具现在断言手牌选择正常参与、无排除，不再期待不支持武装。
- `--replay <private-request.json> --game <installed-game> --mods <matching-frozen-mods>`：冻结故障记录，只替换本项目 DLL/版本及旧轮数上限，保留请求的并发、药水和搜索预算。原始记录、完整结果及日志仅留本机，提交验证摘要前必须去除真实记录、身份和本机路径。
- `--route-feedback-test --seed-result <previous-private-result.json>`：从同一冻结根、相同完整历史的既有获胜结果提取动作，比较旧 Monte Carlo／新回合搜索的已有路线复用，并记录每回合生命变化。保留捕获的并发、次数、时间、回合和药水选项；这项明确带已知路线，不作为无提示最优解发现。可加 `--route-feedback-focused-only` 仅验证复用组。每组完整结果留本机，摘要注明准备状态、单次样本和种子；死亡、未完成及预算内最佳候选不能冒充无伤或全局最优。
- `--data-combat-benchmark --data-run --seed-result <private-result.json>`：常规执行与省去冒险/战斗场景的固定路线交错对照，逐步比较原生状态、历史和完整结算；两种模式的最终复核都使用常规场景。需要上面的冻结请求参数。单条路线限制只用于速度配对，不改变产品预算。加 `--death-route` 使用连续结束回合的种子，比较原生死亡、失败结算与失败后的实例复用。
- `--execution-search-benchmark --seed-result <private-result.json>`：预热两种执行路径后，保留冻结请求的时间与路线数量预算，测量常规／无场景完整搜索。固定路线只用于预热，正式搜索不注入种子；要求所有进程完成、无拒绝、只复核最终路线一次，禁止用常规回退的结果冒充无场景通过。

另建含旋风斩和四张放血的人工牌组，捕获放血后的 68 HP 局面，检查后台胜利后燃烧之血回血至 74 HP，而前台仍保持 68 HP。该断言针对本合成场景的原生遗物效果，不是通用奖励计算规则。

该测试验证前后台通信、重放、搜索、进程复用及只读边界，不证明全部 Mod 私有状态已恢复，不包含实际窗口内按钮点击或布局验收。
# Mod replay checkpoint regression

`SPIRE_LOCAL_MOD_REPLAY_TEST=1` uses the owned replay entry with a locally frozen four-action incident. `SPIRE_LOCAL_REPLAY`, `SPIRE_LOCAL_SEED_RESULT`, and `SPIRE_LOCAL_MOD_REPLAY_AFTER` identify ignored inputs. It compares the original native prediction against the exact live hash/history, validates both restore modes and ownership/missing-state rejection, then searches, independently verifies, and executes a complete native winner. Inputs, proc-state mutation, and all game commands stay inside the owned host; never include this observer or player inputs in the installed package.
