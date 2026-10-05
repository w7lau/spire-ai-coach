using SpireAiCoach.Core;

internal static class ReplayFailureTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        void Check(bool value) { if (!value) throw new Exception("Replay failure assertion failed"); }
        var request = new LocalSearchRequest("first", "snapshot", [1, 2], "native", 42, ["mod:1"], false,
            History: new(3, "history"), Workers: 8);
        var error = new CoachException("local_replay_mismatch", "root differs");
        error.Data["expected_native_hash"] = "native";
        error.Data["actual_native_hash"] = "restored";
        var failure = LocalSimulationFailure.Capture(error, 0, "search");
        var probe = new LocalSearchResult("regular-root", request.SnapshotId, "failed", error.Message,
            0, 0, 1, null, Failure: failure);
        var now = DateTimeOffset.UtcNow;
        test("native replay mismatch retains both actual and expected states", () =>
            Check(failure.Category == "local_replay_mismatch" && failure.ExpectedNativeHash == "native" &&
                failure.ActualNativeHash == "restored" && LocalSearchRecovery.NeedsReplayValidation([failure])));
        test("only two independently failed root modes establish the short replay failure memo", () =>
        {
            var memo = new LocalReplayFailureMemo();
            Check(!memo.Remember(request, "sdk", [], probe, now));
            Check(!memo.Remember(request, "sdk", [failure], probe with { Evaluated = 1 }, now));
            Check(!memo.Remember(request, "sdk", [failure], probe with { RootBranches = 1 }, now));
            Check(!memo.Remember(request, "sdk", [failure], probe with { Failure = failure with { Category = "local_timeout" } }, now));
            Check(!memo.Remember(request with { DataOnlyCombat = false }, "sdk", [failure], probe, now));
            Check(memo.Get(request, "sdk", now) == null);
            Check(memo.Remember(request, "sdk", [failure], probe, now));
            Check(memo.Get(request with { Id = "second", BudgetSeconds = 30, Workers = 4 }, "sdk", now)?.Length == 2);
        });
        test("replay failure reuse expires and requires identical native replay history mods and installation", () =>
        {
            var memo = new LocalReplayFailureMemo(); memo.Remember(request, "sdk", [failure], probe, now);
            foreach (var changed in new[] { request with { Replay = [1, 3] }, request with { NativeHash = "other" },
                request with { History = new(4, "history") }, request with { History = new(3, "other") },
                request with { ModelHash = 43 }, request with { LoadedMods = ["mod:2"] },
                request with { DataOnlyRun = false }, request with { FastNativeWaits = false } })
                Check(memo.Get(changed, "sdk", now) == null);
            Check(memo.Get(request, "other-installation", now) == null);
            Check(memo.Get(request, "sdk", now.AddMinutes(2)) == null);
        });
        test("root-only command does not create an executable result or a usable compatibility candidate", () =>
        {
            var restored = probe with { Status = "restored", Failure = null };
            Check(!LocalSearchPolicy.HasExecutionPoints(restored));
            Check(!LocalSearchRecovery.NeedsCompatibilityPass(request, [restored]));
            Check(!LocalSearchRecovery.AbortPass(request, restored));
        });
        asyncTest("an invalid first native root does not admit seven unused manual lanes", async () =>
        {
            var started = new List<int>();
            var result = await LocalConcurrency.Run(8, false, true, async (index, _) => {
                started.Add(index); await Task.Delay(10); return probe;
            }, _ => new(100, 1, 0, 8), () => false, r => LocalSearchRecovery.AbortPass(request, r),
                CancellationToken.None, 1, waitForRoot: true);
            Check(started.SequenceEqual([0]) && result.Length == 1);
        });
        asyncTest("a valid first root admits the complete manual count together without changing its budgets", async () =>
        {
            bool rootReady = false;
            var started = new List<int>();
            var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = LocalConcurrency.Run(8, false, false, index => {
                Check(index == 0 || rootReady); started.Add(index); return finish.Task;
            }, _ => new(0, 0, rootReady ? 1 : 0, 8), () => false, CancellationToken.None, 1, waitForRoot: true);
            await Task.Delay(10); Check(started.SequenceEqual([0]));
            rootReady = true;
            for (int i = 0; i < 100 && started.Count < 8; i++) await Task.Delay(2);
            Check(started.SequenceEqual(Enumerable.Range(0, 8)));
            finish.SetResult(1); Check((await running).Length == 8);
        });
    }
}
