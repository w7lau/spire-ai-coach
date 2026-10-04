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
