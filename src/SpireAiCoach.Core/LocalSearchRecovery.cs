namespace SpireAiCoach.Core;

public static class LocalSearchRecovery
{
    // Returning a budget-interrupted task ends its ownership. Its partial state
    // is neither a completed trial nor a source of completed-result guidance.
    public static bool CompleteTrial(bool probe, bool cut, bool covered, bool fullRollout, bool terminal, bool budgetInterrupted) =>
        !budgetInterrupted && !probe && !cut && !covered && (fullRollout || terminal);

    private static bool Failed(LocalSearchResult r) => r.Status is "failed" or "unsupported" or "partial";
    private static bool Usable(LocalSearchResult r) => r.Status is "searched" or "done" or "partial" && r.Best != null;

    // A failure before a decision can invalidate the entire bootstrap. A later
    // route exception retires its own lane; healthy isolated processes keep running.
    public static bool AbortPass(LocalSearchRequest request, LocalSearchResult result) =>
        request.DataOnlyCombat && Failed(result) && result.Evaluated == 0 && result.RootBranches == 0 && !Usable(result);

    // Never discard completed native candidates just because another lane failed.
    // Keep failed-owner proof rejection and final candidate checks in their owners.
    public static bool NeedsCompatibilityPass(LocalSearchRequest request, IEnumerable<LocalSearchResult> results)
    {
        var lanes = results.ToArray();
        return request.DataOnlyCombat && lanes.Any(Failed) && !lanes.Any(Usable) &&
            !lanes.All(r => r.Failure?.Category == "local_mod_replay" && r.Evaluated == 0 && r.RootBranches == 0);
    }

    public static bool NeedsReplayValidation(IEnumerable<LocalSimulationFailure> failures) =>
        failures.Any(f => f.Category == "local_replay_mismatch");
}
