# 设计与合同

## 数据流

Godot 主线程 → `StateCapture` → 独立 C# 快照 → SHA-256 指纹 → `PromptBuilder` → 单次 HTTP 请求 → `AdviceContract` 投影和校验 → 主线程核对当前指纹 → 纯文本建议。

状态只在主线程读取，网络和 JSON 解析在后台执行。UI 每 750ms 采集一次，发送前、结果显示前另外刷新。游戏 StateTracker 的变更事件递增快照 revision，因此事件后即使可见数值恰好恢复原样，旧建议仍失效。回合、牌堆、资源、意图等变化会取消进行中的分析并标记旧建议。请求序号防止取消后的迟到回复覆盖更新结果。Mod 不调用出牌、药水使用、回合结束或状态机推进命令。

实例编号由本次战斗内对象身份生成，不发送 Steam ID。牌堆清单保留每张实例；计数按 model_id、名称、升级等级聚合，仅作为摘要，不用于指令身份。玩家名称使用角色名称。自定义效果文本中可能仍含由其他 Mod 插入的文本，发送前可查看完整快照。

## Prompt

`PromptBuilder.SystemPrompt` 是稳定规则：游戏基础机制、例外优先级、证据、不确定性、指导范围解释。`UserPrompt` 包含任务、guidance_scope、max_rounds、快照指纹和完整快照。`AdviceContract.Instructions` 是输出字段与动作枚举的唯一权威，并被系统 Prompt 引用。请求冻结设置中的范围；切换范围取消旧请求，回复必须回显相同范围。

选用显式的有序操作序列，因为每一步的资源、目标、条件与后续状态可能变化；不是给模型重复生成所有牌堆。建议不会作为下一次的游戏事实重新注入，下一次始终读取真实状态。

没有第二个模型 reviewer；第一版仅做本地结构、身份和首步校验。不宣称校验器能证明语义正确、后续资源充足或策略最优，不会用自然语言关键词解析模拟玩法。

## 输出字段

模型拥有下表候选输出。消费方为只读 UI；没有写回游戏或存档。

| 字段 | 类型及边界 | 消费与约束 |
| --- | --- | --- |
| snapshot_id | 必填字符串，最长128 | 必须等于输入指纹；显示前再核对当前指纹 |
| summary | 必填非空字符串，最长4000 | 纯文本战术摘要 |
| guidance_scope | current_turn / combat | 必须与本次请求相同 |
| horizon_note | 必填非空字符串，最长4000 | 说明胜利预期、未知、当前轮结束或10轮上限等规划终点 |
| future_turns | 必填数组，0–9；current_turn 时必须空 | 本轮之外的条件规划；最多含当前轮在内10轮 |
| future_turns[].turn_offset | 整数，从1连续递增 | 相对当前轮；不允许缺号或重复 |
| future_turns[].plan | 必填非空字符串，最长4000 | 后续轮次的策略，不是游戏已发生的事实 |
| future_turns[].assumptions | 必填非空字符串，最长2000 | 成立条件；内容不会被本地模拟器验证 |
| steps | 必填数组，1–64 | 保留原始顺序，溢出拒绝、不截断 |
| steps[].action | play_card / use_potion / end_turn / reassess | 每种均有消费出口 |
| steps[].card_id | play_card 时必填有效实例字符串 | 属于快照某个牌堆；首步必须在手牌且可打出 |
| steps[].potion_id | use_potion 时必填有效实例字符串 | 来自本人药水；首步检查可用状态 |
| steps[].target_id | 动作需要显式目标时填合法实例；否则 null | 首步符合游戏给出的可选目标；后续只校验存在性 |
| steps[].condition | 必填字符串，最长2000；空为无条件 | 未来抽到的牌／同一实例再次打出必须声明条件 |
| steps[].reason | 必填非空字符串，最长4000 | 玩家可见的简短理由 |
| uncertainties | 必填数组，0–32；每条非空且最长2000 | 不确定性提示，不以此证明规则正确 |

首步检查是当前资源和游戏 `CanPlay` / 目标接口的基本检查。自动触发药水不会建议手动使用；其他 Hook 自定义禁用条件与特殊 Mod 可能需要进一步适配。最后一步必须是 end_turn 或 reassess，这些终止操作之后没有步骤。药水实例不可重复消耗；卡牌实例允许有条件重用以支持回手、循环等机制，条件本身没有被模拟证明。

卡牌 `requires_target_selection` 来自 `!IsValidTarget(null)`，读取失败时为未知。仅当 Self、明确不需选目标、可选目标为空且 AI 指向本人时，等价归一为 null；不修正陌生 ID 或错误敌方目标。`star_cost` 使用游戏花费所用的非负语义（负数哨兵归零），`star_cost_x` 保留 X 星星属性。玩家的 stars 仍表示现有资源。

JSON 顶层和每个步骤先投影到消费字段。额外字段被丢弃；非当前动作拥有的 card_id / potion_id / target_id 规范为空；有效动作使用的必填字段继续严格验证。完整 Markdown JSON 代码围栏可去除，其他语义不唯一的格式不猜测。重复消费键、类型错误、未知 ID、快照不匹配、数组越界均拒绝。

## HTTP 与日志

使用非流式 Chat Completions。输入包含 model、messages、stream=false，不设置温度或输出 token 上限。超时和取消独立处理；限制回复体 2 MiB；不跟随重定向，防止 Authorization 被交给另一个地址；默认仅允许 HTTPS，本机允许 HTTP。

分类包括 configuration、phase、authentication、http、transport、timeout、provider_json、provider_schema、empty_response、truncated、finish_reason、invalid_json、schema、contract、identity、condition、illegal_first_action、stale_snapshot、scope、horizon。

`CallDiagnostics` 在解析前捕获成功 HTTP 的原始响应体，并在格式校验前捕获 AI 正文、finish_reason、请求 ID。校验失败也保留精确冻结输入、响应和具体首步错误。非成功 HTTP 不读取响应体；截断、取消、传输错误分别记录，不补造 AI 建议。原始响应在保留本次密钥脱敏后写入本地 diagnostics，最多保留20个本组件命名的文件。UI 最近记录用请求序号防止被旧请求覆盖；实时预览明确标注尚未发送。游戏日志仅记分类、调用ID及快照等摘要；完整诊断只能通过本地文件或用户复制分享。

## 预测扩展

当前只读游戏现有抽牌序列和内置固定敌人连接。未来接第三方预测器时需要注明版本、预测来源、适用快照、随机分支和失效条件，且不能临时修改真实 RNG、行动历史或 live 战斗对象。未经实现和实测的预测器不作为已经集成的能力宣传。
