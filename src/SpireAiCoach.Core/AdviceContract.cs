using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record AdviceStep(string Action, string? CardId, string? PotionId, string? TargetId,
    string Condition, string Reason);
public sealed record Advice(string SnapshotId, string Summary, IReadOnlyList<AdviceStep> Steps,
    IReadOnlyList<string> Uncertainties);

public static class AdviceContract
{
    public const int MaxSteps = 64;
    public const int MaxUncertainties = 32;
    public static readonly string[] Actions = ["play_card", "use_potion", "end_turn", "reassess"];
    public static readonly string[] RootFields = ["snapshot_id", "summary", "steps", "uncertainties"];
    public static readonly string[] StepFields = ["action", "card_id", "potion_id", "target_id", "condition", "reason"];

    public static string Instructions => $$"""
        Return one JSON object with exactly these consumed fields: {{string.Join(", ", RootFields)}}.
        snapshot_id: copy the input snapshot_id string. summary: nonempty short Chinese tactical summary.
        steps: ordered array of 1..{{MaxSteps}} objects, each containing {{string.Join(", ", StepFields)}}.
        action is one of {{string.Join(", ", Actions)}}.
        card_id is a snapshot card instance_id for play_card, otherwise null.
        potion_id is a snapshot potion instance_id for use_potion, otherwise null.
        target_id is an explicitly selected creature instance_id, or null for an action without a chosen target.
        condition is a Chinese string; empty means unconditional. reason is a nonempty concise Chinese explanation.
        uncertainties: array of 0..{{MaxUncertainties}} nonempty Chinese strings.
        Use actual instance IDs, preserving separate copies of identical cards and identical enemies.
        The first action must be legal in the current snapshot. Later actions may depend on earlier effects:
        state such conditions explicitly; repeated use of an instance needs an explicit return/reuse condition.
        End the sequence with end_turn only when the supplied information supports ending the turn,
        otherwise with reassess at the first unresolved draw, selection, random outcome, or hidden mechanic.
        """;

    // Construct a new allowlisted tree before validation. Extra fields never reach the UI.
    public static Advice Parse(string text, CombatSnapshot state)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
        {
            var line = text.IndexOf('\n');
            if (line >= 0) text = text[(line + 1)..^3].Trim();
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw Invalid("invalid_json", "AI 回复不是完整 JSON，请重新分析。"); }
        using (doc)
        {
            var root = Project(doc.RootElement, RootFields);
            var snapshotId = String(root, "snapshot_id", 128);
            if (snapshotId != state.Fingerprint()) throw Invalid("stale_snapshot", "AI 回复与本次战斗快照不一致。");
            var summary = String(root, "summary", 4000);
            var stepsArray = Array(root, "steps", 1, MaxSteps);
            var steps = new List<AdviceStep>();
            var cards = state.AllCards.ToDictionary(c => c.InstanceId);
            var potions = state.Player.Potions.ToDictionary(p => p.InstanceId);
            var targets = state.Enemies.Concat(state.Allies).Concat(state.Player.Pets)
                .Select(c => c.InstanceId).Append(state.Player.InstanceId).ToHashSet();
            var used = new HashSet<string>();
            foreach (var element in stepsArray.EnumerateArray())
            {
                var item = Project(element, StepFields);
                var action = String(item, "action", 32);
                if (!Actions.Contains(action)) throw Invalid("contract", "AI 返回了未知操作。");
                var condition = String(item, "condition", 2000, true);
                var cardId = action == "play_card" ? NullableString(item, "card_id") : null;
                var potionId = action == "use_potion" ? NullableString(item, "potion_id") : null;
                var targetId = action is "play_card" or "use_potion" ? NullableString(item, "target_id") : null;
                var reason = String(item, "reason", 4000);
                if (targetId != null && !targets.Contains(targetId)) throw Invalid("identity", "建议引用了不存在的目标。");
                if (action == "play_card")
                {
                    if (cardId == null || !cards.TryGetValue(cardId, out var card))
                        throw Invalid("identity", "建议引用了不存在的卡牌。");
                    if (steps.Count == 0 && (!state.Hand.Cards.Any(c => c.InstanceId == cardId) || card.PlayableNow != true))
                        throw Invalid("illegal_first_action", "建议的第一张牌目前不能打出。");
                    if (steps.Count == 0 && !TargetFits(card.TargetType, card.LegalTargetsNow, targetId))
                        throw Invalid("illegal_first_action", "建议的首个目标目前不能选择。");
                    if ((!state.Hand.Cards.Any(c => c.InstanceId == cardId) || !used.Add(cardId)) && string.IsNullOrWhiteSpace(condition))
                        throw Invalid("condition", "尚未在手中的卡牌或重复出牌需要说明成立条件。");
                }
                if (action == "use_potion")
                {
                    if (potionId == null || !potions.TryGetValue(potionId, out var potion) || !used.Add(potionId))
                        throw Invalid("identity", "建议引用了不存在或重复使用的药水。");
                    if (steps.Count == 0 && !potion.UsableNow)
                        throw Invalid("illegal_first_action", "建议的药水目前无法使用。");
                    if (steps.Count == 0 && !TargetFits(potion.TargetType, potion.LegalTargetsNow, targetId))
                        throw Invalid("illegal_first_action", "建议的药水目标目前不能选择。");
                }
                if (steps.LastOrDefault()?.Action is "end_turn" or "reassess")
                    throw Invalid("contract", "结束或重新评估之后不能继续给出操作。");
                steps.Add(new(action, cardId, potionId, targetId, condition, reason));
            }
            if (steps[^1].Action is not ("end_turn" or "reassess"))
                throw Invalid("contract", "建议缺少结束回合或重新评估的收尾。");
            var uncertainties = Array(root, "uncertainties", 0, MaxUncertainties).EnumerateArray()
                .Select(x => ReadString(x, 2000, false)).ToArray();
            return new(snapshotId, summary, steps, uncertainties);
        }
    }

    private static bool TargetFits(string type, IReadOnlyList<string> legal, string? id) =>
        type is "AnyEnemy" or "AnyAlly" or "AnyPlayer" ? id != null && legal.Contains(id) : id == null || legal.Contains(id);

    private static Dictionary<string, JsonElement> Project(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("schema", "AI 回复中的对象类型有误。");
        var projected = new Dictionary<string, JsonElement>();
        foreach (var property in value.EnumerateObject())
            if (fields.Contains(property.Name) && !projected.TryAdd(property.Name, property.Value))
                throw Invalid("schema", "AI 回复含重复字段。");
        return projected;
    }
    private static JsonElement Required(Dictionary<string, JsonElement> obj, string name) =>
        obj.TryGetValue(name, out var value) ? value : throw Invalid("schema", $"AI 回复缺少字段：{name}。");
    private static string String(Dictionary<string, JsonElement> obj, string name, int max, bool empty = false) =>
        ReadString(Required(obj, name), max, empty);
    private static string ReadString(JsonElement value, int max, bool empty)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("schema", "AI 回复中的文本类型有误。");
        var text = value.GetString()!;
        if (text.Length > max || (!empty && string.IsNullOrWhiteSpace(text))) throw Invalid("schema", "AI 回复中的文本为空或过长。");
        return text;
    }
    private static string? NullableString(Dictionary<string, JsonElement> obj, string name)
    {
        var value = Required(obj, name);
        return value.ValueKind == JsonValueKind.Null ? null : ReadString(value, 128, false);
    }
    private static JsonElement Array(Dictionary<string, JsonElement> obj, string name, int min, int max)
    {
        var value = Required(obj, name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max)
            throw Invalid("schema", "AI 回复中的列表类型或长度有误。");
        return value;
    }
    private static CoachException Invalid(string category, string message) => new(category, message);
}
