namespace SpireAiCoach.Core;

public sealed record LocalHistoryStamp(int Count, string Hash);
public sealed record LocalContinuationPoint(int ActionIndex, string NativeHash, LocalHistoryStamp History, int HpLost, int? Hp = null);

public sealed class LocalContinuation(string combatId, string[] mods, LocalSearchResult original)
{
    public int CompletedActions { get; private set; }
    public bool Invalid { get; private set; }

    public LocalSearchResult? Advance(string currentCombat, string[] currentMods, string nativeHash, LocalHistoryStamp history,
        bool requireProgress = false)
    {
        if (Invalid) return null;
        var best = original.Best;
        if (currentCombat != combatId || !mods.SequenceEqual(currentMods) || best?.Continuation == null)
            return Reject();
        var matches = best.Continuation.Where(p => p.ActionIndex >= CompletedActions &&
            p.ActionIndex < best.Actions.Length && p.NativeHash == nativeHash && p.History == history).ToArray();
        if (matches.Length != 1) return Reject();
        if (requireProgress && matches[0].ActionIndex == CompletedActions) return Reject();
        var point = matches[0]; CompletedActions = point.ActionIndex;
        var remaining = best.Actions.Skip(CompletedActions).ToArray();
        return original with { Best = best with { Actions = remaining,
            StartingHp = point.Hp ?? best.StartingHp, HpLost = Math.Max(0, best.HpLost - point.HpLost),
            Rounds = remaining.Select(a => a.Round).Distinct().Count() },
            Message = CompletedActions == 0 ? original.Message : $"已核对并完成前 {CompletedActions} 步，继续使用原路线；未重新搜索。" };
    }

    private LocalSearchResult? Reject() { Invalid = true; return null; }
}

public sealed record LocalSearchTiming(long RestoreMs = 0, long ActionMs = 0, long DecisionMs = 0,
    long VerificationMs = 0, long StartupMs = 0, int Actions = 0, int Restores = 0, int Verifications = 0);
