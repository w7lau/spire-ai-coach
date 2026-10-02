using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Read a locally frozen incident request; simulate it only in the existing owned worker infrastructure.
// Neither the replay nor installed game assets are changed. Do not commit the input or full output.
public static class ReplayIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, string replayPath, string game)
    {
        var original = LocalWire.Read<LocalSearchRequest>(replayPath);
        var coach = Path.Combine(root, "game", "mods", "SpireAiCoach");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(coach, "SpireAiCoach.json")));
        var coachId = manifest.RootElement.GetProperty("id").GetString()!;
        var coachVersion = manifest.RootElement.GetProperty("version").GetString()!;
        var loaded = original.LoadedMods.Select(m => m.StartsWith(coachId + ":", StringComparison.Ordinal)
            ? $"{coachId}:{coachVersion}:{typeof(ModEntry).Assembly.ManifestModule.ModuleVersionId}" : m).Order(StringComparer.Ordinal).ToArray();
        var modRoot = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_REPLAY_MODS");
        var directories = Directory.EnumerateDirectories(string.IsNullOrEmpty(modRoot) ? Path.Combine(game, "mods") : modRoot).Where(d =>
            Directory.EnumerateFiles(d, "*.json").Any(p =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(p));
                    var id = doc.RootElement.GetProperty("id").GetString();
                    return id != coachId && original.LoadedMods.Any(m => m.StartsWith(id + ":", StringComparison.Ordinal));
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
            })).Append(coach).ToArray();
        // Preserve the frozen user's search/potion settings; only raise the old turn horizon.
        var request = original with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded, MaxRounds = 64 };
        var installation = new LocalInstallation(game, directories);
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT") is { Length: > 0 } seedPath)
            request = request with { InitialPlan = LocalWire.Read<LocalSearchResult>(seedPath).Best!.Actions };
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_VISUAL_BENCHMARK") == "1")
        {
            if (request.InitialPlan is not { Length: > 0 }) throw new InvalidOperationException("A fixed complete line is required");
            // Fixed-route experiment only. Product search limits are unchanged.
            request = request with { Workers = 1, MaxNodes = 1, BudgetSeconds = 60, SimulationSpeed = 8 };
            await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
            LocalCandidate? baseline = null;
            async Task<LocalSearchResult> Sample(bool fast, bool eventWaits)
            {
                var sample = await Task.Run(() => pool.Analyze(request with
                    { Id = Guid.NewGuid().ToString("N"), FastCardPresentation = fast, FastNativeWaits = eventWaits }, installation, _ => { }, CancellationToken.None));
                var best = sample.Best ?? throw new InvalidOperationException("Presentation sample returned no route");
                if (!best.Won || sample.Rejected != 0 || best.Continuation?.Length != best.Actions.Length || sample.Timing?.Verifications != 1)
                    throw new InvalidOperationException("Presentation sample did not produce independently verified victory");
                if (baseline != null && (best.Hp != baseline.Hp || best.HpLost != baseline.HpLost || best.Gold != baseline.Gold ||
                    best.MaxHp != baseline.MaxHp || best.Rounds != baseline.Rounds || best.StartingHp != baseline.StartingHp ||
                    JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                    !best.Continuation!.SequenceEqual(baseline.Continuation!)))
                    throw new InvalidOperationException("Presentation optimization changed actions, native states, history or settlement");
                baseline ??= best;
                return sample;
            }
            // Warm every path. Reverse the three-mode order to reduce timing/order bias.
            await Sample(false, false); await Sample(true, false); await Sample(true, true);
            var records = new List<object>();
            foreach (var (fast, eventWaits) in new[] { (false, false), (true, false), (true, true), (true, true), (true, false), (false, false) })
            {
                var sample = await Sample(fast, eventWaits);
                var best = sample.Best!;
                LocalWire.Write(Path.Combine(root, $"integration-visual-private-{records.Count}.json"), sample);
                records.Add(new { fast_card_presentation = fast, event_driven_waits = eventWaits, request.SimulationSpeed, sample.ElapsedMs, sample.Timing,
                    best.HpLost, best.Hp, best.NetHpLoss, best.Rounds, steps = best.Actions.Length,
                    native_states_and_history_match = true });
            }
            LocalWire.Write(Path.Combine(root, "integration-visual-summary.json"), records);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SPEED_BENCHMARK") == "1")
        {
            request = request with { Workers = 1, MaxNodes = 1, BudgetSeconds = 60 };
            await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
            // Warm run/act assets before comparing the same complete fixed line.
            await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"), SimulationSpeed = 1 }, installation, _ => { }, CancellationToken.None));
            var records = new List<object>();
            LocalCandidate? baseline = null;
            foreach (var speed in new[] { 1, 8 })
            {
                var sample = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"), SimulationSpeed = speed },
                    installation, _ => { }, CancellationToken.None));
                var best = sample.Best ?? throw new InvalidOperationException("Speed sample returned no route");
                if (!best.Won || sample.Rejected != 0 || best.Continuation?.Length != best.Actions.Length)
                    throw new InvalidOperationException("Speed sample did not produce verified victory");
                if (baseline != null && (best.HpLost != baseline.HpLost || best.Hp != baseline.Hp ||
                    !best.Continuation!.SequenceEqual(baseline.Continuation!)))
                    throw new InvalidOperationException("Accelerated replay differed from baseline native states/history");
                baseline = best;
                records.Add(new { speed, sample.ElapsedMs, sample.Timing, best.HpLost, best.Hp, best.Rounds,
                    steps = best.Actions.Length, native_states_and_history_match = true });
            }
            LocalWire.Write(Path.Combine(root, "integration-speed-summary.json"), records);
            return;
        }
        var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
        LocalWire.Write(Path.Combine(root, "integration-replay-private.json"), result);
        if (result.Status != "done" || result.Best == null || result.Rejected != 0 ||
            result.Best.Continuation?.Length != result.Best.Actions.Length || result.Timing?.Verifications != 1)
            throw new InvalidOperationException("Incident replay did not complete every lane with a verified route: " + result.Message);
        LocalWire.Write(Path.Combine(root, "integration-replay-summary.json"), new
        {
            result.Status, result.Evaluated, result.Rejected, result.Victories, result.ElapsedMs, result.Timing,
            result.Workers, result.MaxRounds, request.BudgetSeconds, request.MaxNodes, request.IncludePotions,
            request.FastCardPresentation, request.FastNativeWaits,
            result.Best.Won, result.Best.StartingHp, result.Best.Hp, result.Best.NetHpLoss, result.Best.HpLost, result.Best.Rounds,
            result.Best.EnemyHp, result.Best.StopReason,
            used_potion = result.Best.Actions.Any(a => a.PotionSlot.HasValue),
            choices = result.Best.Actions.Sum(a => a.Choices?.Length ?? 0),
            verified_steps = result.Best.Continuation!.Length
        });
    }
}
