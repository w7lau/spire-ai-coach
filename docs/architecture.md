# 设计与合同

## 数据流

Godot 主线程 → `StateCapture` → 独立 C# 快照 → SHA-256 指纹 → `PromptBuilder` → 单次 HTTP 请求 → `AdviceContract` 投影和校验 → 主线程核对当前指纹 → 纯文本建议。

状态只在主线程读取，网络和 JSON 解析在后台执行。UI 每 750ms 采集一次，发送前、结果显示前另外刷新。游戏 StateTracker 的变更事件递增快照 revision，因此事件后即使可见数值恰好恢复原样，旧建议仍失效。回合、牌堆、资源、意图等变化会取消进行中的分析并标记旧建议。请求序号防止取消后的迟到回复覆盖更新结果。Mod 不调用出牌、药水使用、回合结束或状态机推进命令。

实例编号由本次战斗内对象身份生成，不发送 Steam ID。牌堆清单保留每张实例；计数按 model_id、名称、升级等级聚合，仅作为摘要，不用于指令身份。玩家名称使用角色名称。自定义效果文本中可能仍含由其他 Mod 插入的文本，发送前可查看完整快照。

## Prompt

`PromptBuilder.SystemPrompt` 是稳定规则：游戏基础机制、例外优先级、证据、不确定性、当前回合指导职责。`UserPrompt` 包含任务、快照指纹和完整快照。`AdviceContract.Instructions` 是输出字段与动作枚举的唯一权威，并被系统 Prompt 引用。

选用显式的有序操作序列，因为每一步的资源、目标、条件与后续状态可能变化；不是给模型重复生成所有牌堆。建议不会作为下一次的游戏事实重新注入，下一次始终读取真实状态。

没有第二个模型 reviewer；第一版仅做本地结构、身份和首步校验。不宣称校验器能证明语义正确、后续资源充足或策略最优，不会用自然语言关键词解析模拟玩法。

## 输出字段

模型拥有下表候选输出。消费方为只读 UI；没有写回游戏或存档。

| 字段 | 类型及边界 | 消费与约束 |
| --- | --- | --- |
| snapshot_id | 必填字符串，最长128 | 必须等于输入指纹；显示前再核对当前指纹 |
| summary | 必填非空字符串，最长4000 | 纯文本战术摘要 |
| steps | 必填数组，1–64 | 保留原始顺序，溢出拒绝、不截断 |
| steps[].action | play_card / use_potion / end_turn / reassess | 每种均有消费出口 |
| steps[].card_id | play_card 时必填有效实例字符串 | 属于快照某个牌堆；首步必须在手牌且可打出 |
| steps[].potion_id | use_potion 时必填有效实例字符串 | 来自本人药水；首步检查可用状态 |
| steps[].target_id | 动作需要显式目标时填合法实例；否则 null | 首步符合游戏给出的可选目标；后续只校验存在性 |
| steps[].condition | 必填字符串，最长2000；空为无条件 | 未来抽到的牌／同一实例再次打出必须声明条件 |
| steps[].reason | 必填非空字符串，最长4000 | 玩家可见的简短理由 |
| uncertainties | 必填数组，0–32；每条非空且最长2000 | 不确定性提示，不以此证明规则正确 |

首步检查是当前资源和游戏 `CanPlay` / 目标接口的基本检查。自动触发药水不会建议手动使用；其他 Hook 自定义禁用条件与特殊 Mod 可能需要进一步适配。最后一步必须是 end_turn 或 reassess，这些终止操作之后没有步骤。药水实例不可重复消耗；卡牌实例允许有条件重用以支持回手、循环等机制，条件本身没有被模拟证明。

JSON 顶层和每个步骤先投影到消费字段。额外字段被丢弃；非当前动作拥有的 card_id / potion_id / target_id 规范为空；有效动作使用的必填字段继续严格验证。完整 Markdown JSON 代码围栏可去除，其他语义不唯一的格式不猜测。重复消费键、类型错误、未知 ID、快照不匹配、数组越界均拒绝。

## HTTP 与日志

使用非流式 Chat Completions。输入包含 model、messages、stream=false，不设置温度或输出 token 上限。超时和取消独立处理；限制回复体 2 MiB；不跟随重定向，防止 Authorization 被交给另一个地址；默认仅允许 HTTPS，本机允许 HTTP。

分类包括 configuration、phase、authentication、http、transport、timeout、provider_json、provider_schema、empty_response、truncated、finish_reason、invalid_json、schema、contract、identity、condition、illegal_first_action、stale_snapshot。

返回的 `CallResult` 在内存保留模型、请求 ID、finish_reason、usage、耗时和已验证建议。游戏日志只记成功快照指纹／耗时／Prompt 版本或失败分类；不记录密钥或原始响应。后续若增加真实样本评测，应显式导出用户同意提供的冻结快照及脱敏响应。

## 预测扩展

当前只读游戏现有抽牌序列和内置固定敌人连接。未来接第三方预测器时需要注明版本、预测来源、适用快照、随机分支和失效条件，且不能临时修改真实 RNG、行动历史或 live 战斗对象。未经实现和实测的预测器不作为已经集成的能力宣传。
