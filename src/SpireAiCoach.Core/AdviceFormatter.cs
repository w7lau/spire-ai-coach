using System.Text;

namespace SpireAiCoach.Core;

public static class AdviceFormatter
{
    public static string Format(Advice advice, CombatSnapshot snapshot)
    {
        var cards = snapshot.AllCards.ToDictionary(x => x.InstanceId);
        var potions = snapshot.Player.Potions.ToDictionary(x => x.InstanceId);
        var names = snapshot.Enemies.Concat(snapshot.Allies).Concat(snapshot.Player.Pets)
            .ToDictionary(x => x.InstanceId, x => x.Name);
        names[snapshot.Player.InstanceId] = "自己";
        var output = new StringBuilder("本回合指导")
            .AppendLine().AppendLine(advice.Summary).AppendLine().AppendLine($"当前第 {snapshot.Round} 轮：");
        for (int i = 0; i < advice.Steps.Count; i++)
        {
            var step = advice.Steps[i];
            string action = step.Action switch
            {
                "play_card" => $"打出 {cards[step.CardId!].Name} [{step.CardId}]",
                "use_potion" => $"使用 {potions[step.PotionId!].Name} [{step.PotionId}]",
                "end_turn" => "结束回合",
                "reassess" => "观察结果后重新分析",
                _ => throw new InvalidOperationException("Unvalidated advice")
            };
            output.Append(i + 1).Append(". ").Append(action);
            if (step.TargetId != null) output.Append(" → ").Append(names[step.TargetId]).Append(" [").Append(step.TargetId).Append(']');
            output.AppendLine();
            if (step.Condition.Length != 0) output.Append("条件：").AppendLine(step.Condition);
            output.AppendLine(step.Reason).AppendLine();
        }
        if (advice.Uncertainties.Count > 0)
        {
            output.AppendLine("需要留意");
            foreach (var note in advice.Uncertainties) output.Append("• ").AppendLine(note);
        }
        return output.ToString();
    }
}
