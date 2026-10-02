# 本地指导集成测试

此测试 Mod 只在带 NativeProbe 所有权标记的私有游戏副本中运行，不安装到玩家游戏目录。它加载原先的合成 `fixture.json`，经正常地图入口进入战斗，在开局、出牌后和下一回合三个点调用与面板相同的 LocalCapture / LocalWorkerPool API，验证双进程搜索及前台状态不变。另测取消和错误根指纹拒绝。后台仍加载真实 HextechRunes / RitsuLib。

需要先按 NativeProbe 文档准备人工牌组及私有工作目录。测试需约十余 GB 额外空间（宿主副本与按构建隔离的两个后台副本），不读取玩家存档。版本变化会新建 worker 缓存，注意磁盘占用。

```powershell
dotnet build experiments/LocalIntegration/LocalIntegration.csproj -c Release '-p:GameDir=R:\SteamLibrary\steamapps\common\Slay the Spire 2'
python experiments/LocalIntegration/run.py --workspace C:\path\owned-native-worker
```

默认每进程/每阶段最多评估 6 条路线；`--quick` 改为 2 条，其余恢复、取消、偏差检查不变。`integration-result.json` 保存各阶段结果，成功还需 `integration-success` 文件及退出码为 0；失败详情在 `integration-error.txt`，引擎日志另外保留。测试 DLL 不进入正式安装包。

另建含旋风斩和四张放血的人工牌组，捕获放血后的 68 HP 局面，检查后台胜利后燃烧之血回血至 74 HP，而前台仍保持 68 HP。该断言针对本合成场景的原生遗物效果，不是通用奖励计算规则。

该测试验证前后台通信、重放、搜索、进程复用及只读边界，不证明全部 Mod 私有状态已恢复，不包含实际窗口内按钮点击或布局验收。
