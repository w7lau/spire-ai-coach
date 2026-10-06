using SpireAiCoach.Core;

internal static class MinimumLossTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static LocalSearchRequest Request() => new("proof", "battle", [], "root", 1, ["sts2:fixture"], false);
    private static LocalAction Move(int id, string hash = "root", int round = 1) =>
        new(id, "card-" + id, null, "card", "", hash, round, CombatCardIndex: (uint)id);
    private static LocalHealthEnvelope Bound(LocalSearchRequest r, int hp, long? heal = 0, int potions = 0) =>
        new(r.SnapshotId + ":" + r.NativeHash, 50, hp, potions, heal);
    private static LocalCandidate Win(LocalAction[] actions, int hp = 38) =>
        new(actions, hp, 12, 0, 0, 50, true, false, false, StartingHp: 50);
    internal static LocalLossProofTrial FirstTurn(LocalSearchRequest r, int branch, bool finish = false)
    {
        var legal = new[] { Move(1, r.NativeHash), Move(2, r.NativeHash) };
        var step = new LocalLossProofStep(legal[branch], legal, [], Bound(r, 38));
        return new(50, [step], 38, Won: finish);
    }
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("compact proof replies retain exact claim order target certificates and final diagnostics", async () =>
        {
            (string[] Claims, string[] Statuses, long Bytes) Run(bool compact)
            {
                var r = Request() with { CompactMinimumLossReplies = compact };
                using var broker = new LocalMinimumLossBroker(r, 1);
                using var client = new LocalMinimumLossClient(r with { MinimumLossPipe = broker.PipeName });
                var legal = Enumerable.Range(1, 20).Select(i => Move(i)).ToArray();
                var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
                LocalLossProofTrial Trial(LocalAction a, bool won = false) =>
                    new(50, [new(a, legal, [], Bound(r, 38), BeforeHint: hint)], 38, Won: won);
                var claims = new List<string>(); var statuses = new List<string>();
                void Status() => statuses.Add(System.Text.Json.JsonSerializer.Serialize(client.Status with { Focus = null }));
                client.Observe(Trial(legal[0], true)); Status();
                Check((client.Status.Focus == null) == compact, "Only the duplicated wire focus list may be omitted");
                for (int i = 1; i < legal.Length; i++)
                {
                    var focus = client.TakeFocus() ?? throw new Exception("Missing native proof claim");
                    claims.Add(LocalTurnSearch.HistoryKey(focus.Prefix));
                    Check(focus.SearchRound == 1 && focus.Hint == hint, "The exact claimed task must stay complete");
                    client.Observe(Trial(focus.Prefix[0])); Status();
                }
                Check(client.TakeFocus() == null, "Complete domains must stop claiming work");
                broker.ConfirmOwner(0);
                Check(broker.Status is { Confirmed: true, Certificate.MinimumNetHpLoss: 12, Focus.Length: 0 },
                    "The parent must retain full authoritative proof diagnostics");
                return (claims.ToArray(), statuses.ToArray(), client.ReceivedBytes);
            }
            var full = Run(false); var compact = Run(true);
            Check(full.Claims.SequenceEqual(compact.Claims) && full.Statuses.SequenceEqual(compact.Statuses),
                "Compacting replies cannot change task order bounds or target updates");
            Check(compact.Bytes < full.Bytes / 2, "Unused task lists must account for a measurable payload reduction");
            await Task.CompletedTask;
        });

        test("short loss proofs stop at irreversible HP bounds and preserve healing and cheaper potion alternatives", () =>
        {
            var r = Request(); var target = new LocalLossProofTarget(9, 1, 41);
            Check(LocalMinimumLossProof.CannotImproveTarget(Bound(r, 41, 0, 1), target), "An equal HP and potion bound remained open");
            Check(!LocalMinimumLossProof.CannotImproveTarget(Bound(r, 41, 0, 0), target), "A cheaper potion route was discarded");
            Check(!LocalMinimumLossProof.CannotImproveTarget(Bound(r, 40, 2, 1), target), "Future healing was omitted");
            Check(!LocalMinimumLossProof.CannotImproveTarget(Bound(r, 40, null, 1), target), "Unknown healing became a proof");
            Check(LocalMinimumLossProof.CannotImproveTarget(Bound(r, 40, 0, 0), target), "A worse HP branch failed to close");
        });
        asyncTest("both algorithms claim distinct short proof prefixes independently of rollout queues", async () =>
        {
            var r = Request(); using var broker = new LocalMinimumLossBroker(r, 2);
            var command = r with { MinimumLossPipe = broker.PipeName };
            using var left = new LocalMinimumLossClient(command);
            using var right = new LocalMinimumLossClient(command with { Partition = 1 });
            var legal = new[] { Move(1), Move(2), Move(3), Move(4) };
            var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
            LocalLossProofTrial Trial(LocalAction a, bool won = false) =>
                new(50, [new(a, legal, [], Bound(r, 38), BeforeHint: hint)], 38, Won: won);
            left.Observe(Trial(legal[0], true));
            var claims = await Task.WhenAll(Task.Run(left.TakeFocus), Task.Run(right.TakeFocus));
            Check(claims.All(c => c != null) && claims[0]!.Prefix[0] != claims[1]!.Prefix[0], "Concurrent workers repeated the same proof prefix");
            left.Observe(Trial(claims[0]!.Prefix[0]));
            var next = left.TakeFocus();
            Check(next != null && next.Prefix[0] != claims[1]!.Prefix[0], "An active proof lease was assigned twice");
            left.Observe(Trial(next!.Prefix[0])); right.Observe(Trial(claims[1]!.Prefix[0]));
            Check(broker.Status.Certificate is { MinimumNetHpLoss: 12, MaximumFinalHp: 38 }, "Partial settled prefixes failed to establish the root bound");
            Check(left.TakeFocus() == null && right.TakeFocus() == null, "Closed bounds continued to schedule proof work");
        });
        test("loss proof targets open early turns without needing later battle terminals", () =>
        {
            var r = Request(); var proof = new LocalMinimumLossProof(r);
            var root = new[] { Move(1), Move(2) };
            var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
            LocalLossProofTrial Trial(int branch, bool finish, long? recovery = 0)
            {
                var next = Move(3, "round-2:" + branch, 2) with { EndTurn = true };
                return new(50, [new(root[branch], root, [], Bound(r, 50, recovery), BeforeHint: hint,
                    AfterHint: hint, AfterRound: 2), new(next, [next], [], Bound(r, 38, recovery),
                    BeforeHint: hint, AfterHint: hint with { Hp = 38 }, AfterRound: 3)], 38, Won: finish);
            }
            proof.Observe(Trial(0, true));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 0, "The unvisited first turn remains open");
            var missing = proof.Status.Focus?.Single(f => f.Prefix.Length == 1);
            Check(missing?.Prefix[0].CombatCardIndex == 2 && missing.SearchRound == 1,
                "The missing early branch should be scheduled rather than another full winner");
            proof.Observe(Trial(1, false));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 12 && proof.Status.Focus?.Length == 0,
                "Second-turn settled loss should close the bound while later turns remain open");
            Check(proof.Status.Target?.NetHpLoss == 12 && LocalSearchPolicy.CanStopAtMinimum(Win([root[0]]), r,
                proof.Status.Certificate), "The already acquired victory must return when the local floor catches up");
            proof = new(r); proof.Observe(Trial(0, true, null));
            Check(proof.Status.Focus?.Length == 0, "Unknown recovery must not monopolize scheduling for an unprovable floor");
        });
        test("loss proof scheduling retains native targets ordered selections and open pagination", () =>
        {
            var r = Request(); var proof = new LocalMinimumLossProof(r);
            var action = Move(1) with { TargetId = 9 };
            var choices = new[] { new LocalCardChoice("ordered-offer", 0, "opaque", "", [0, 1], "hand"),
                new LocalCardChoice("ordered-offer", 0, "opaque", "", [1, 0], "hand") };
            var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
            proof.Observe(new(50, [new(action with { Choices = [choices[0]] }, [action], [new(0, choices)],
                Bound(r, 38), BeforeHint: hint)], 38, Won: true));
            var focus = proof.Status.Focus!.Single();
            Check(focus.Prefix[0].TargetId == 9 && focus.Prefix[0].BeforeHash == r.NativeHash &&
                focus.Prefix[0].Choices![0].Indices!.SequenceEqual([1, 0]), "The alternate choice must retain exact ordered native identity");
            proof.Observe(new(50, [new(action with { Choices = [choices[1]] }, [action], [new(0, choices)],
                Bound(r, 38), BeforeHint: hint)], 38));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 12, "All settled ordered choices establish the local floor");
            var open = new LocalMinimumLossProof(r);
            var page = choices.Select(c => c with { CompleteOffer = false }).ToArray();
            foreach (int selected in new[] { 0, 1 }) open.Observe(new(50,
                [new(action with { Choices = [page[selected]] }, [action], [new(0, page)], Bound(r, 38), BeforeHint: hint)],
                38, Won: selected == 0));
            Check(open.Status.Certificate?.MinimumNetHpLoss == 0, "A page never certifies unobserved choice combinations");
        });
        test("loss proof priority uses short probes while preserving other search lanes", () =>
        {
            var search = new LocalTurnSearch(3);
            var hint = new LocalTurnHint(50, 50, 100, 100);
            for (int i = 1; i <= 8; i++) search.Offer([Move(i)], 1, hint);
            search.PrioritizeLossProof([new([Move(8)], 1, hint)]);
            Check(search.TryTake(out var proof) && proof.Prefix[0].CombatCardIndex == 8 && proof.LossProof && !proof.FullRollout,
                "A one-action proof prefix must not become a full battle rollout");
            Check(search.TryTake(out var normal) && !normal.LossProof, "Normal exploration retains its lane");
            var identities = new HashSet<uint?> { proof.Prefix[0].CombatCardIndex, normal.Prefix[0].CombatCardIndex };
            while (search.TryTake(out var task)) identities.Add(task.Prefix[0].CombatCardIndex);
            Check(identities.Count == 8, "Proof ordering cannot delete legal search work");
        });
        asyncTest("shared loss proof tasks stay short across native-owner scheduling protocol", async () =>
        {
            var r = Request(); using var broker = new LocalTurnWork(r, 2);
            var wire = r with { TurnWorkPipe = broker.PipeName };
            using var left = new LocalTurnWorkClient(wire);
            using var right = new LocalTurnWorkClient(wire with { Partition = 1 });
            var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
            left.Offer([], 1, hint);
            Check(left.TryTake(out var root), "Root task missing");
            for (int i = 1; i <= 8; i++) left.Offer([Move(i)], 1, hint);
            left.PrioritizeLossProof([new([Move(8)], 1, hint)]);
            Check(right.TryTake(out var normal) && !normal.LossProof, "Normal lane disappeared");
            left.Finish(root);
            Check(left.TryTake(out var proof) && proof.LossProof && !proof.FullRollout &&
                proof.Prefix[0].CombatCardIndex == 8, "The broker upgraded a short one-card proof task into a full battle");
            left.Finish(proof); right.Finish(normal);
            await Task.CompletedTask;
        });
        test("minimum loss covers all first-turn branches without finishing later battles", () =>
        {
            var r = Request(); var proof = new LocalMinimumLossProof(r);
            proof.Observe(FirstTurn(r, 0));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 0, "An untried sibling may avoid all damage");
            proof.Observe(FirstTurn(r, 1));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 12 && proof.Status.Trials == 2, "Settled turn bounds must aggregate");
            Check(LocalSearchPolicy.CanStopAtMinimum(Win([Move(1)]), r, proof.Status.Certificate), "A completed 12-loss victory reaches the floor");
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win([Move(1)]) with { Won = false }, r, proof.Status.Certificate), "A turn probe is not a victory");
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win([Move(1)], 37), r, proof.Status.Certificate), "13 loss is not solved");
        });
        test("minimum loss subtracts future and victory recovery and retains unknown futures", () =>
        {
            var r = Request();
            foreach (var (gain, floor) in new (long?, int)[] { (0, 12), (6, 6), (20, 0), (null, 0) })
            {
                var proof = new LocalMinimumLossProof(r);
                foreach (int branch in new[] { 0, 1 })
                {
                    var t = FirstTurn(r, branch);
                    proof.Observe(t with { Steps = [t.Steps[0] with { After = Bound(r, 38, gain) }] });
                }
                Check(proof.Status.Certificate?.MinimumNetHpLoss == floor, "Recovery cannot be omitted or inferred from intent");
            }
        });
        test("minimum loss leaves limits incomplete offers and untried ordered selections unresolved", () =>
        {
            var r = Request(); var action = Move(1); var other = Move(2);
            var choices = new[] { new LocalCardChoice("offer", 0, "x", "", [0, 1], "hand"),
                new LocalCardChoice("offer", 0, "x", "", [1, 0], "hand") };
            var proof = new LocalMinimumLossProof(r);
            foreach (int i in new[] { 0, 1 })
                proof.Observe(new(50, [new(action with { Choices = [choices[i] with { CompleteOffer = false }] }, [action],
                    [new(0, [choices[i] with { CompleteOffer = false }])], Bound(r, 38))], 38));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 0, "Observed pages are not a complete domain");
            proof = new(r);
            proof.Observe(new(50, [new(action with { Choices = [choices[0]] }, [action], [new(0, choices)], Bound(r, 38))], 38));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 0, "The other ordered selection may avoid damage");
            proof.Observe(new(50, [new(action with { Choices = [choices[1]] }, [action], [new(0, choices)], Bound(r, 38))], 38));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 12, "Both ordered choices settle the domain");
            proof = new(r);
            proof.Observe(new(50, [new(action, [action], [], Bound(r, 38), CompleteLegal: false)], 38));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 0, "A no-potion offer cannot certify a potion-enabled domain");
            proof.Observe(new(50, [new(action, [action, other], [], Bound(r, 38))], 38));
            Check(proof.Status.Certificate?.MinimumNetHpLoss == 0, "Completing the offer does not evaluate its missing sibling");
        });
        test("minimum loss preserves potion reserve and explicit disabled or benchmark goals", () =>
        {
            var r = Request() with { IncludePotions = true }; var proof = new LocalMinimumLossProof(r);
            var potion = Move(1) with { PotionSlot = 0 }; var card = Move(2); var legal = new[] { potion, card };
            proof.Observe(new(50, [new(potion, legal, [], Bound(r, 38, potions: 1))], 38));
            proof.Observe(new(50, [new(card, legal, [], Bound(r, 38))], 38));
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win([potion]), r, proof.Status.Certificate), "An unpotioned continuation can tie");
            Check(LocalSearchPolicy.CanStopAtMinimum(Win([card]), r, proof.Status.Certificate), "Zero-potion 12-loss victory is solved");
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win([card]), r with { StopOnZeroLoss = false }, proof.Status.Certificate), "Disabled means disabled");
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win([card]), r with { TargetVictoryRounds = 6 }, proof.Status.Certificate), "A proof must not weaken an explicit zero-loss benchmark");
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win([card]), r with { NativeHash = "other" }, proof.Status.Certificate), "Stale root rejected");
        });
        test("minimum loss rejects contradicted bounds changed native offers and excluded cards", () =>
        {
            var r = Request(); var proof = new LocalMinimumLossProof(r); var t = FirstTurn(r, 0);
            proof.Observe(t); proof.Observe(t with { Won = true, Hp = 39 });
            Check(proof.Status.Certificate == null && proof.Status.InvalidReason.Length > 0, "Unexpected healing invalidates the certificate");
            proof = new(r); proof.Observe(t);
            proof.Observe(t with { Steps = [t.Steps[0] with { Legal = [Move(1), Move(2), Move(3)] }] });
            Check(proof.Status.Certificate == null, "New legal move at a supposedly complete history invalidates proof");
            proof = new(r with { ExcludedModels = ["card-3"] }); proof.Observe(t);
            Check(proof.Status.Certificate == null, "Excluding an unsupported model cannot prove global minimum");
            proof = new(r, 1); proof.Observe(t);
            Check(proof.Status.Certificate == null, "Storage limits never close an unobserved subtree");
        });
        test("minimum loss matches finite native-history oracle for every incomplete coverage prefix", () =>
        {
            var random = new Random(819);
            for (int run = 0; run < 40; run++)
            {
                var r = Request(); var proof = new LocalMinimumLossProof(r); var roots = new[] { Move(1), Move(2), Move(3) };
                var damages = Enumerable.Range(0, 9).Select(_ => random.Next(1, 24)).ToArray();
                var leaves = Enumerable.Range(0, 9).OrderBy(_ => random.Next()).ToArray();
                foreach (var index in leaves)
                {
                    int branch = index / 3, option = index % 3;
                    var next = new[] { Move(4, "state-" + branch, 2), Move(5, "state-" + branch, 2), Move(6, "state-" + branch, 2) };
                    proof.Observe(new(50, [new(roots[branch], roots, [], null), new(next[option], next, [], null)],
                        50 - damages[index], Won: true));
                    Check(proof.Status.Certificate!.MinimumNetHpLoss <= damages.Min(), "An incomplete tree cannot overestimate the true optimum");
                }
                Check(proof.Status.Certificate!.MinimumNetHpLoss == damages.Min(), "Complete finite coverage matches exhaustive oracle");
            }
        });
        asyncTest("minimum loss broker combines concurrent owners and requires final native gates", async () =>
        {
            var r = Request(); using var broker = new LocalMinimumLossBroker(r, 2);
            var command = r with { MinimumLossPipe = broker.PipeName };
            using var left = new LocalMinimumLossClient(command);
            using var right = new LocalMinimumLossClient(command with { Partition = 1 });
            await Task.WhenAll(Task.Run(() => left.Observe(FirstTurn(r, 0))), Task.Run(() => right.Observe(FirstTurn(r, 1))));
            Check(broker.Status.Certificate?.MinimumNetHpLoss == 12 && !broker.Status.Confirmed, "Running contributor is provisional");
            broker.ConfirmOwner(0); Check(!broker.Status.Confirmed, "The other contributor has not passed its gate");
            broker.ConfirmOwner(1); Check(broker.Status.Confirmed, "All native contributors confirmed");
            using var stale = new LocalMinimumLossClient(command with { NativeHash = "stale" });
            try { stale.Observe(FirstTurn(r, 0)); throw new Exception("Stale proof was accepted"); } catch (InvalidDataException) { }
            Check(broker.Status.Certificate?.MinimumNetHpLoss == 12, "A foreign root cannot change the proof");
            broker.RejectOwner(1); Check(broker.Status.Certificate == null, "Runtime failure invalidates all dependent proof");
        });
    }
}
