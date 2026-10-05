# 本地指导集成测试

此测试 Mod 只在带 NativeProbe 所有权标记的私有游戏副本中运行，不安装到玩家游戏目录。默认加载合成 `fixture.json`，经正常地图入口进入战斗，在开局、出牌后和下一回合三个点调用与面板相同的 LocalCapture / LocalWorkerPool API，验证搜索及宿主状态不变。另测取消和错误根指纹拒绝。后台仍加载真实 HextechRunes / RitsuLib；真实故障重放只在显式传入冻结记录时运行。

需要先按 NativeProbe 文档准备人工牌组及私有工作目录。宿主副本独立；后台通过同盘 NTFS 硬链接共享游戏资源，配置、Mod、存档和日志各自独立。版本变化会新建 worker 缓存。测试不直接读取或修改玩家存档。

```powershell
dotnet build experiments/LocalIntegration/LocalIntegration.csproj -c Release '-p:GameDir=R:\SteamLibrary\steamapps\common\Slay the Spire 2'
python experiments/LocalIntegration/run.py --workspace C:\path\owned-native-worker
```

默认每进程/每阶段最多评估 6 条路线；`--quick` 改为 2 条，其余恢复、取消、偏差检查不变。`integration-result.json` 保存各阶段结果，成功还需 `integration-success` 文件及退出码为 0；失败详情在 `integration-error.txt`，引擎日志另外保留。测试 DLL 不进入正式安装包。

定向用例：

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
