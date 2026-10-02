# 设计与合同

## 数据流

Godot 主线程 → `StateCapture` → 独立 C# 快照 → SHA-256 指纹 → `PromptBuilder` → 单次流式 HTTP 请求 → 接收中正文预览 → 完整正文交 `AdviceContract` 投影和校验 → 主线程核对当前指纹 → 纯文本建议。

状态只在主线程读取，网络和 JSON 解析在后台执行。UI 每 750ms 采集一次，发送前、结果显示前另外刷新。游戏 StateTracker 的变更事件递增快照 revision，因此事件后即使可见数值恰好恢复原样，旧建议仍失效。回合、牌堆、资源、意图等变化会取消进行中的分析并标记旧建议。请求序号防止取消后的迟到回复覆盖更新结果。Mod 不调用出牌、药水使用、回合结束或状态机推进命令。

实例编号由本次战斗内对象身份生成，不发送 Steam ID。牌堆清单保留每张实例；计数按 model_id、名称、升级等级聚合，仅作为摘要，不用于指令身份。玩家名称使用角色名称。自定义效果文本中可能仍含由其他 Mod 插入的文本，发送前可查看完整快照。

## Prompt

`PromptBuilder.SystemPrompt` 是稳定规则：游戏基础机制、例外优先级、证据、不确定性、本回合指导职责。turn-coach-v5 使用三条消息：system、ContextPrompt user、UserPrompt user。ContextPrompt 仅携带 context.player.character / relics；UserPrompt 包含任务、完整快照指纹及去掉这两字段的当前快照。合并两部分即可无损恢复完整观察，遗物不重复发送，原始 CombatSnapshot 与首步校验不变。`AdviceContract.Instructions` 是输出字段与动作枚举的唯一权威，并被系统 Prompt 引用。0.2.1 移除多轮 UI、设置和输出合同，旧配置中的范围字段按未知字段忽略。诊断的 guidance_scope 恒为 current_turn，仅用于记录，不由模型生成。

当前回合推断包括本轮出牌及紧接的敌方行动；要求使用状态效果和遗物的描述、层数、使用状态及触发时机，解释会改变本轮决策的机制。确定性数值结论应核算资源、牌堆移动、格挡和已知触发；缺少后续轮次信息不妨碍规划已有充分证据的本轮动作。该 Prompt 是模型行为要求，没有伪装成本地模拟器校验。

turn-coach-v4 将默认战斗规则集中在 system：回合开始能量重置到上限（不与余量累加）、普通抽5张、抽弃牌循环、消耗／能力牌、保留／虚无、格挡到所属方下回合开始、逐次伤害和前后触发。实际描述和有效规则例外优先。结束回合前比较剩余能量的可行用途：能量本身默认不保值；无已知代价的正收益动作不因收益小而跳过，保留或不出牌则说明实际代价／收益。不会加入“有能量就必须出牌”的硬校验。

针对本机 v0.111.0 已核实的内置机制，固定说明仅对 source=sts2、相应 model_id 生效：覆甲在所属回合结束的早期给予格挡，回合开始再减层；胆小在符合条件且造成穿透伤害的卡牌攻击之后获得格挡。`StateCapture` 读取公开 `SkittishPower.HasGainedBlockThisTurn` 到 used_up；其他状态仍可为 null，不以描述猜测已触发状态。没有修改游戏状态或推进随机数。

SystemPrompt 初始化一次并保留 SHA-256 指纹；不包含动态快照ID、时间、人物或模型。HTTP 两次不同回合测试直接比较前两条消息逐字相同，第三条包含各自快照。ContextPrompt 每次从同一份当前冻结快照重建，没有本地跨请求缓存；遗物含有动态描述、amount、variables、used_up 和 stack_count，任一变化均更新前缀，空数组表示当前无遗物。保留原采集顺序及重复实例，避免压缩改变语义。该指纹可用于诊断规则版本一致性，不是服务端缓存凭据。没有新 reviewer，输出字段、首步校验、纯文本显示和旧建议不再注入的边界保持一致。

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

卡牌 `requires_target_selection` 来自 `!IsValidTarget(null)`，读取失败时为未知。仅当 Self、明确不需选目标、可选目标为空且 AI 指向本人时，等价归一为 null；不修正陌生 ID 或错误敌方目标。`star_cost` 使用游戏花费所用的非负语义（负数哨兵归零），`star_cost_x` 保留 X 星星属性。玩家的 stars 仍表示现有资源。

JSON 顶层和每个步骤先投影到消费字段。额外字段被丢弃；非当前动作拥有的 card_id / potion_id / target_id 规范为空；有效动作使用的必填字段继续严格验证。完整 Markdown JSON 代码围栏可去除，其他语义不唯一的格式不猜测。重复消费键、类型错误、未知 ID、快照不匹配、数组越界均拒绝。

## HTTP 与日志

使用流式 Chat Completions。输入包含 model、messages、stream=true，Accept 为 text/event-stream，不设置温度或输出 token 上限。超时覆盖响应头和响应体读取，取消独立处理；限制完整线上的回复体 2 MiB（包括 SSE 元数据），底层按读取字节计数，没有换行的超长事件也不能绕过；不跟随重定向；默认仅允许 HTTPS，本机允许 HTTP。

默认 IncludeStreamUsage=true，发送标准 stream_options.include_usage；设置关闭时整个 stream_options 键省略，遇到不兼容拒绝不会自动重试。读取可选 usage.prompt_tokens、completion_tokens 和 prompt_tokens_details.cached_tokens；缺失、类型错误或负数视为未知，明确的0保留为0。原始 usage 与规范计数都写入诊断，并在成功完成时显示。格式失败的响应若有 usage 也保留。固定前缀便于兼容服务商缓存，但客户端无法承诺命中、折扣或第三方真实账单。

`StreamingResponse` 按 [WHATWG SSE 事件格式](https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation) 解码 UTF-8，支持 BOM、LF/CRLF/CR、注释心跳与多行 data。只拼接 choices 中 index=0 的 delta.content，允许单项回复省略 index；角色和 usage 事件不当作正文，忽略其他选择的正文。finish_reason=stop 且 [DONE] 或完整事件后的正常 EOF 才算流完整；缺少结束原因、未闭合事件、异常断线或 length 均不发布建议。明确 stop 后仍追加正文视为接口结构错误。仅有正常流结束还需通过已有建议合同。

正文回调提供累计文本，首段立即通知，随后最多每100ms刷新一次，并在流结束时补齐。UI 只保留最新待显示片段，避免按 token 累积主线程队列；用请求代次和快照复查阻止取消或过期请求覆盖新内容。接收中正文与最终校验建议有明确状态区分；失败或取消后清除主面板预览，部分正文留在诊断。服务商若忽略 stream 并返回 JSON，则在同一次请求内用完整响应路径校验，标明一次性返回，不隐式重试。

分类包括 configuration、phase、authentication、http、transport、timeout、provider_json、provider_schema、provider_error、provider_encoding、stream_incomplete、empty_response、truncated、finish_reason、invalid_json、schema、contract、identity、condition、illegal_first_action、stale_snapshot。

`CallDiagnostics` 在解析前捕获成功 HTTP 的原始响应体，并在格式校验前捕获 AI 正文、finish_reason、请求 ID。校验失败也保留精确冻结输入、响应和具体首步错误。非成功 HTTP 不读取响应体；截断、取消、传输错误分别记录，不补造 AI 建议。原始响应在保留本次密钥脱敏后写入本地 diagnostics，最多保留20个本组件命名的文件。UI 最近记录用请求序号防止被旧请求覆盖；实时预览明确标注尚未发送。游戏日志仅记分类、调用ID及快照等摘要；完整诊断只能通过本地文件或用户复制分享。

诊断 response_format 区分 sse/json；stream_completed 只表示流式传输有完整结束，不代表建议合法。SSE 中断时也保留原始已读事件和已拼接正文。预览掩去本次密钥及末尾可能仍在接收的密钥前缀；拼接正文需要脱敏时，省略原始 SSE 事件体以避免从片段重建密钥，保留脱敏正文。

DiagnosticDisplay 只接收已脱敏记录，按已知层级解析日志 → request_body → messages[].content，原样展示模型消息内容和 assistant_content。不会靠替换反斜杠破坏合法 JSON / 描述。UI 不再把整份嵌套诊断追加到文本末尾；复制按钮及本地诊断保留完整记录，实时输入预览包括两个 user 段。修改展示不改变实际发送的文本或诊断存储协议。

## 预测扩展

当前只读游戏现有抽牌序列和内置固定敌人连接。未来接第三方预测器时需要注明版本、预测来源、适用快照、随机分支和失效条件，且不能临时修改真实 RNG、行动历史或 live 战斗对象。未经实现和实测的预测器不作为已经集成的能力宣传。
