using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

public static class ChoiceIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, StateCapture capture, Player player)
    {
        var snapshot = capture.Capture(true)!;
        var request = LocalCapture.Capture(snapshot.Fingerprint(), true) with
            { Workers = 1, MaxNodes = 12, MaxRounds = 1, BudgetSeconds = 30, IncludePotions = true };
        var installation = LocalCapture.Installation();
        var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
        LocalWire.Write(Path.Combine(root, "integration-choices-search.json"), result);
        var best = result.Best ?? throw new InvalidOperationException("No result with attack potion");
        if (LocalCapture.Fingerprint() != request.NativeHash || !best.Actions.Any(a => a.Choices is { Length: > 0 }) ||
            best.Continuation?.Length != best.Actions.Length || result.Rejected != 0)
            throw new InvalidOperationException("Need isolated, verified choice route without unsupported branches");
        var plan = new LocalContinuation(snapshot.CombatId, request.LoadedMods, result);
        var messages = new List<string>();
        var final = await new LocalPlanExecutor(tree).Execute(plan, () => capture.Capture(false)?.CombatId, true,
            messages.Add, CancellationToken.None);
        if (messages.Count != best.Actions.Length || player.Creature.CurrentHp != best.Hp)
            throw new InvalidOperationException("Choice execution differed from verification");
        var after = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true) with
            { Workers = 1, MaxNodes = 1, MaxRounds = 1, BudgetSeconds = 15 };
        var reader = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader(); reader.Reset(after.Replay);
        var recorded = reader.Read<MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplay>();
        LocalWire.Write(Path.Combine(root, "integration-choices-history.json"), recorded.events.Select(e => new
            { kind = e.eventType.ToString(), action = e.action?.GetType().Name, choice = e.playerChoiceResult?.ToString() }));
        var replay = await Task.Run(() => pool.Analyze(after, installation, _ => { }, CancellationToken.None));
        if (replay.Best?.Continuation?.Length != replay.Best?.Actions.Length || replay.Best == null ||
            after.NativeHash != LocalCapture.Fingerprint()) throw new InvalidOperationException("Replay after choice failed");
        LocalWire.Write(Path.Combine(root, "integration-choices.json"), new
            { result, final, executed_steps = messages.Count, native_history = after.History,
                recapture_replay_verified = true, unchanged_after_search = true, replay });
    }
}
