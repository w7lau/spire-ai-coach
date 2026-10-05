using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

static class LocalLimitsTests
{
    static void Check(bool ok) { if (!ok) throw new Exception("Local search limits assertion failed"); }

    public static void Register(Action<string, Action> test)
    {
        test("local search limits old configurations retain original budgets", () =>
        {
            var settings = JsonSerializer.Deserialize<CoachSettings>("{\"local_workers\":8}", Wire.Json)!;
            Check(settings.LocalWorkers == 8 && settings.LocalMaxAttempts == 64 &&
                settings.LocalMaxRounds == 64 && settings.LocalSearchSeconds == 60 && settings.LocalSkipFinalVerification);
            var optedIn = JsonSerializer.Deserialize<CoachSettings>("{\"local_skip_final_verification\":false}", Wire.Json)!;
            Check(!optedIn.LocalSkipFinalVerification && !new CoachSettings { LocalSkipFinalVerification = false }.LocalSkipFinalVerification);
        });
        test("local search limits freeze configurable budgets in both algorithms and on the wire", () =>
        {
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var captured = new LocalSearchRequest("id", "snapshot", [], "native", 1, [], true);
                var request = LocalCalculation.Configure(captured, order, 8, true, false, true, 120,
                    maxAttempts: 257, maxRounds: 130, searchSeconds: 125);
                var copy = JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(request))!;
                Check(copy.SearchOrder == order && copy.Workers == 8 && copy.MaxNodes == 257 &&
                    copy.MaxRounds == 130 && copy.BudgetSeconds == 125 && copy.TargetVictoryRounds == 120 &&
                    copy.AdaptiveWorkers && copy.IncludePotions && !copy.StopOnZeroLoss && copy.SkipFinalVerification);
                Check(copy.NativeHash == captured.NativeHash && copy.SnapshotId == captured.SnapshotId &&
                    copy.NumericalExecution && copy.DataOnlyCombat && copy.DataOnlyRun && copy.TrimWorkerOverhead);
            }
        });
        test("interface preference defaults hidden and preserves credentials and unrelated settings", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "spire-interface-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                var config = Path.Combine(directory, "config.json");
                File.WriteAllText(config, "{\"model\":\"\",\"local_workers\":8,\"extension_setting\":{\"value\":7}}");
                var key = Path.Combine(directory, "api-key.dpapi");
                File.WriteAllBytes(key, [1, 2, 3, 4]);
                var store = new SettingsStore(directory);
                Check(!store.Load().Settings.ShowOverlayButton && !new CoachSettings().ShowOverlayButton);
                var before = JsonNode.Parse(File.ReadAllText(config))!;
                store.SaveInterfaceOptions(true);
                before["show_overlay_button"] = true;
                Check(JsonNode.DeepEquals(before, JsonNode.Parse(File.ReadAllText(config))));
                Check(File.ReadAllBytes(key).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
                store.SaveLocalWorkers(4);
                Check(store.Load().Settings.ShowOverlayButton && store.Load().Settings.LocalWorkers == 4);
                store.SaveInterfaceOptions(false);
                Check(!store.Load().Settings.ShowOverlayButton && store.Load().Settings.Model == "");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        test("local search limits persist without AI credentials and survive a concurrency-only save", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "spire-limits-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                var config = Path.Combine(directory, "config.json");
                File.WriteAllText(config, "{\"model\":\"\",\"extension_setting\":{\"value\":7}}");
                var key = Path.Combine(directory, "api-key.dpapi");
                File.WriteAllBytes(key, [1, 2, 3, 4]);
                var store = new SettingsStore(directory);
                store.SaveLocalOptions(8, true, false, 120, 257, 130, 125, skipFinalVerification: false);
                var before = JsonNode.Parse(File.ReadAllText(config))!;
                store.SaveLocalWorkers(16);
                var after = JsonNode.Parse(File.ReadAllText(config))!;
                before["local_workers"] = 16;
                Check(JsonNode.DeepEquals(before, after));
                Check(File.ReadAllBytes(key).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
                var loaded = store.Load().Settings;
                Check(loaded.LocalWorkers == 16 && loaded.LocalMaxAttempts == 257 && loaded.LocalMaxRounds == 130 &&
                    loaded.LocalSearchSeconds == 125 && loaded.LocalTargetVictoryRounds == 120 &&
                    loaded.LocalIncludePotions && !loaded.LocalStopOnZeroLoss && !loaded.LocalSkipFinalVerification && loaded.Model == "");
                store.SaveLocalOptions(16, null, skipFinalVerification: true);
                Check(store.Load().Settings.LocalSkipFinalVerification && File.ReadAllBytes(key).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
                store.Save(store.Load().Settings with { Model = "fixture", LocalSkipFinalVerification = false }, "");
                Check(!store.Load().Settings.LocalSkipFinalVerification && store.Load().Settings.LocalMaxAttempts == 257);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        test("local search limits reject invalid bounds before altering persisted preferences", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "spire-invalid-limits-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SettingsStore(directory);
                store.SaveLocalOptions(8, null, maxAttempts: 257, maxRounds: 130, searchSeconds: 125);
                var config = Path.Combine(directory, "config.json");
                var before = File.ReadAllBytes(config);
                foreach (var (attempts, rounds, seconds) in new[] { (0, 130, 125), (10_001, 130, 125),
                    (257, 0, 125), (257, 1_025, 125), (257, 130, 0), (257, 130, 3_601) })
                {
                    bool rejected = false;
                    try { store.SaveLocalOptions(4, null, maxAttempts: attempts, maxRounds: rounds, searchSeconds: seconds); }
                    catch (ArgumentOutOfRangeException) { rejected = true; }
                    Check(rejected && before.SequenceEqual(File.ReadAllBytes(config)));
                }
                Check(LocalCalculation.ValidLimits(1, 1, 1) && LocalCalculation.ValidLimits(10_000, 1_024, 3_600));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        test("local search limits extend the parent timeout for larger budgets and long final routes", () =>
        {
            Check(LocalCalculation.WorkerTimeoutSeconds(60) == 180);
            Check(LocalCalculation.WorkerTimeoutSeconds(125) == 245);
            Check(LocalCalculation.WorkerTimeoutSeconds(600) == 720);
            Check(LocalCalculation.WorkerTimeoutSeconds(60, 600) == 1320);
            Check(LocalCalculation.WorkerTimeoutSeconds(3600, 60) == 3720);
        });
    }
}
