# 原生回复边界识别检查

读取本机安装的游戏程序集和异步方法元数据，不启动游戏、修改存档或执行卡牌效果。检查通用机制分类与固定战后回复表达式，不按卡名实现效果；列表中的模型仅是验证样本。

```powershell
dotnet run --project experiments/RecoveryBounds -c Release "-p:GameDir=R:\SteamLibrary\steamapps\common\Slay the Spire 2" -- experiments/RecoveryBounds/results/2026-10-05.json
```

报告只保存模型分类及程序集 MVID，不能证明玩家实际战斗的完整回复上界、剪枝正确率或性能。包含外部普通回调、回复命令、反射和注册委托的代码分类；这些测试回调不执行。未知全局效果、未知生成、增加生命上限与重复回血保持未知。游戏 DLL、反编译源码、存档和真实日志不提交。

实际生命写入需另外在已有所属原生宿主检查，可用 LocalIntegration 的 `--recovery-audit --health-audit`，并提供准确冻结请求及其实际 Mod 目录。只做回复代码检查、少量生命写入及完整起点恢复，不搜索或出牌。返回状态为 audited，不能成为可执行方案；有权访问该输入不构成在正式游戏中做这些写入的授权。
