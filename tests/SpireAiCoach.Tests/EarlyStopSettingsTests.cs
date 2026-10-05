using System.Text.Json.Nodes;
using SpireAiCoach.Mod;

static class EarlyStopSettingsTests
{
    public static void Register(Action<string, Action> test)
    {
        test("local early stop persists independently and preserves old settings and opaque key", () =>
        {
            var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "spire-early-stop-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            try
            {
                void Check(bool ok) { if (!ok) throw new Exception("Early-stop setting persistence failed"); }
                var store = new SettingsStore(directory);
                Check(store.Load().Settings.LocalStopOnZeroLoss);
                Check(!store.Load().Settings.LocalStopOnFirstWin);
                var config = Path.Combine(directory, "config.json");
                File.WriteAllText(config, """{"model":"existing","remember_key":false,"unrelated":{"preserve":17}}""");
                var key = Path.Combine(directory, "api-key.dpapi");
                File.WriteAllBytes(key, [7, 8, 9]);
                Check(store.Load().Settings.LocalStopOnZeroLoss);
                store.SaveLocalOptions(3, true, false, 6);
                Check(!store.Load().Settings.LocalStopOnZeroLoss);
                Check(store.Load().Settings.LocalTargetVictoryRounds == 6);
                store.SaveLocalWorkers(4);
                Check(!store.Load().Settings.LocalStopOnZeroLoss && store.Load().Settings.LocalIncludePotions);
                store.SaveLocalOptions(4, null, true);
                Check(store.Load().Settings.LocalStopOnZeroLoss && store.Load().Settings.Model == "existing");
                Check(store.Load().Settings.LocalTargetVictoryRounds == 6);
                store.SaveLocalOptions(4, null, null, 0);
                Check(store.Load().Settings.LocalTargetVictoryRounds == 0);
                store.SaveLocalOptions(4, null, playCardModelId: "fixture:play", finisherCardModelId: "fixture:finish",
                    cardGoalThresholdEnabled: true, cardGoalHpLossThreshold: 5);
                store.SaveLocalOptions(4, null, stopOnFirstWin: true);
                Check(store.Load().Settings.LocalStopOnFirstWin && store.Load().Settings.LocalStopOnZeroLoss);
                store.SaveLocalWorkers(3);
                Check(store.Load().Settings.LocalStopOnFirstWin);
                store.SaveLocalOptions(3, null, stopOnFirstWin: false);
                Check(!store.Load().Settings.LocalStopOnFirstWin);
                Check(store.Load().Settings.LocalPlayCardModelId == "fixture:play" &&
                    store.Load().Settings.LocalFinisherCardModelId == "fixture:finish" &&
                    store.Load().Settings.LocalCardGoalThresholdEnabled && store.Load().Settings.LocalCardGoalHpLossThreshold == 5);
                Check(JsonNode.Parse(File.ReadAllText(config))!["unrelated"]!["preserve"]!.GetValue<int>() == 17);
                Check(File.ReadAllBytes(key).SequenceEqual(new byte[] { 7, 8, 9 }));
            }
            finally { Directory.Delete(directory, true); }
        });
    }
}
