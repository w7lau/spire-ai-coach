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
        var result = await Task.Run(() => pool.Analyze(request, new(game, directories), _ => { }, CancellationToken.None));
        LocalWire.Write(Path.Combine(root, "integration-replay-private.json"), result);
        if (result.Best == null || result.Rejected != 0 || result.Best.Continuation?.Length != result.Best.Actions.Length)
            throw new InvalidOperationException("Incident replay did not produce a verified route");
        LocalWire.Write(Path.Combine(root, "integration-replay-summary.json"), new
        {
            result.Status, result.Evaluated, result.Rejected, result.Victories, result.ElapsedMs, result.Timing,
            result.Workers, result.MaxRounds, request.BudgetSeconds, request.MaxNodes, request.IncludePotions,
            result.Best.Won, result.Best.Hp, result.Best.HpLost, result.Best.Rounds,
            result.Best.EnemyHp, result.Best.StopReason,
            used_potion = result.Best.Actions.Any(a => a.PotionSlot.HasValue),
            choices = result.Best.Actions.Sum(a => a.Choices?.Length ?? 0),
            verified_steps = result.Best.Continuation!.Length
        });
    }
}
