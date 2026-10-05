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
        var request = original with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded, MaxRounds = 64,
            TimelineOrigin = 0, InitialTrace = null };
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_DATA_COMBAT") == "1") request = request with { DataOnlyCombat = true };
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_DATA_RUN") == "1") request = request with { DataOnlyRun = true };
        if (int.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_WORKERS"), out var workers))
            request = request with { Workers = workers };
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_CORRELATED_ROLLOUTS") == "1")
        {
            if (request.InitialPlan is { Length: > 0 } || request.VerifyCandidate != null || request.RecordedReplayProbe != null)
                throw new InvalidOperationException("Policy comparison requires an unseeded frozen request");
            request = request with { CorrelatedRollouts = true };
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SHARED_WORK") is { Length: > 0 } sharing)
            request = request with { ShareSearchWork = sharing == "on" };
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEARCH_ORDER") is { Length: > 0 } ordering)
        {
            if (request.InitialPlan is { Length: > 0 } || request.VerifyCandidate != null || request.RecordedReplayProbe != null)
                throw new InvalidOperationException("Search-order comparison requires an unseeded frozen search request");
            if (!Enum.TryParse<LocalSearchOrder>(ordering, out var searchOrder) || !Enum.IsDefined(searchOrder))
                throw new InvalidOperationException("Unknown experimental search order");
            request = request with { SearchOrder = searchOrder,
                ShareSearchWork = searchOrder is LocalSearchOrder.MonteCarlo or LocalSearchOrder.TurnFrontier && request.ShareSearchWork };
        }
        // Frozen execution controls retain the same startup; bootstrap has its own paired
        // cold measurements. Ordinary integration fixtures use the product's default.
        var installation = new LocalInstallation(game, directories, MinimalWorkerBootstrap: false);
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_REPLAY_FAILURE_TEST") == "1")
        {
            await ReplayFailureIntegration.Run(root, pool, request, installation with { MinimalWorkerBootstrap = true },
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_REPLAY_CONSISTENCY_TEST") == "1")
        {
            await ReplayConsistencyIntegration.Run(root, original,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SNAPSHOT_METADATA_TEST") == "1")
        {
            await SnapshotMetadataIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_PASSIVE_FACTORY_TEST") == "1")
        {
            await PassiveFactoryIntegration.Run(root, pool, request with { MaxRounds = original.MaxRounds }, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ANIMATION_PRESENTATION_TEST") == "1")
        {
            await AnimationPresentationIntegration.Run(root, pool, request with { MaxRounds = original.MaxRounds }, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_FINISHER_RETENTION_TEST") == "1")
        {
            await FinisherRetentionIntegration.Run(root, pool, original, request, installation);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_EVENT_ENTRY_TEST") == "1")
        {
            await EventEntryIntegration.Run(root, pool, request with { MaxRounds = original.MaxRounds }, installation);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SUMMON_PRESENTATION_TEST") == "1")
        {
            await SummonPresentationIntegration.Run(root, pool, request with { MaxRounds = original.MaxRounds }, installation);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_COACH_RECOVERY_AUDIT") == "1")
        {
            installation = installation with { GameDirectory = Path.Combine(root, "game") };
            request = request with { Workers = 1, AdaptiveWorkers = false };
            try { await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None)); }
            catch (CoachException ex) when (ex.Category == "local_audit_complete")
            { /* An audit is deliberately not a usable combat result. */ }
            var auditFiles = Directory.EnumerateFiles(Path.Combine(root, ".spire-ai-coach-workers"),
                "recovery-audit.json", SearchOption.AllDirectories).Where(p =>
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(p));
                    return doc.RootElement.GetProperty("Id").GetString() == request.Id;
                }).ToArray();
            if (auditFiles.Length != 1) throw new InvalidOperationException("One current native recovery audit was not produced");
            File.Copy(auditFiles[0], Path.Combine(root, "integration-recovery-audit.json"), true);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_RECENT_SEARCH_TEST") == "1")
        {
            await RecentSearchIntegration.Run(root, pool, original, request, installation);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SELECTION_PAGING_TEST") == "1")
        {
            // Keep the incident's round/time/trial limits. Two owned lanes are
            // sufficient to exercise shared prefix replay, without an 8-lane benchmark.
            request = request with { MaxRounds = original.MaxRounds };
            var sample = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, "integration-paging-incident-private.json"), sample);
            if (sample.Status != "done" || sample.Best == null || sample.Rejected != 0 ||
                (sample.RecoveredFailures?.Length ?? 0) != 0 || sample.Trace!.Spans.Any(s => s.Phase == "fallback"))
                throw new InvalidOperationException("Paged incident still restarted or rejected native replay: " + sample.Message);
            LocalWire.Write(Path.Combine(root, "integration-paging-incident-summary.json"), new {
                version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), sample.Status, sample.Workers,
                sample.Evaluated, sample.Victories, sample.ElapsedMs, request.MaxNodes, request.MaxRounds, request.BudgetSeconds,
                sample.Best.Won, sample.Best.Hp, sample.Best.NetHpLoss, sample.Best.Rounds,
                recoveredFailures = sample.RecoveredFailures?.Length ?? 0, fallback = false,
                nativeChoiceSteps = sample.Best.Actions.Sum(a => a.Choices?.Length ?? 0),
                admissions = sample.Trace.Spans.Where(s => s.Phase == "admit_worker").Select(s => new {
                    worker = s.Worker + 1, s.StartMs }),
                sessions = sample.Trace.Spans.Where(s => s.Phase == "session").Select(s => new {
                    worker = s.Worker + 1, s.StartMs, s.DurationMs }) });
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_REPLAY_TEST") == "1")
        {
            await ExecutionReplayIntegration.Run(root, original,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_DRAW_GATE_TEST") == "1")
                await DrawGateIntegration.Run(root, pool, request, installation,
                    System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!,
                    System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_REPORT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_PROGRESS_TRANSPORT_TEST") == "1")
        {
            await ProgressTransportIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ROUTE_FEEDBACK_TEST") == "1")
        {
            await RouteFeedbackIntegration.Run(root, pool, request with { MaxRounds = original.MaxRounds }, installation);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SCENE_OVERHEAD_TEST") == "1")
        {
            await SceneOverheadIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_NATIVE_OVERHEAD_TEST") == "1")
        {
            await NativeOverheadIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_VISUAL_FACTORY_TEST") == "1")
        {
            await VisualFactoryIntegration.Run(root, pool, request with { MaxRounds = original.MaxRounds }, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_LIMITS_TEST") == "1")
        {
            await LimitsIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_LEAN_CHECKSUM_TEST") == "1")
        {
            await LeanChecksumIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEARCH_CASES") is { Length: > 0 } cases)
        {
            await SelfSearchIntegration.Run(root, pool, request, installation, cases);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_INCIDENT_VERIFICATION") == "1")
        {
            await IncidentVerification.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_FINAL_VERIFICATION_TEST") == "1")
        {
            await FinalVerificationIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")
                    ?? throw new InvalidOperationException("A fixed native route is required"));
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ALGORITHM_TEST") == "1")
        {
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ALGORITHM_GOAL_TEST") == "1")
                await AlgorithmIntegration.RunGoal(root, pool, request, installation);
            else await AlgorithmIntegration.Run(root, pool, request, installation);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_EARLY_STOP_TEST") == "1")
        {
            await EarlyStopIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")
                    ?? throw new InvalidOperationException("A fixed winning seed is required"));
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_VERIFICATION_BENCHMARK") == "1")
        {
            await VerificationIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_OVERHEAD_BENCHMARK") == "1")
        {
            await OverheadIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")
                    ?? throw new InvalidOperationException("A fixed native route is required"));
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_NUMERICAL_BENCHMARK") == "1")
        {
            await NumericalIntegration.Run(root, pool, request, installation,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_RECORDED_REPLAY")
                    ?? throw new InvalidOperationException("A recorded native route is required"));
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_RECORDED_REPLAY") is { Length: > 0 } recordedPath)
        {
            await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
            // Standalone probe of an owned worker. Its status is not accepted by Analyze and
            // cannot publish an executable recommendation in the real game.
            var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(pool)!).GetValue(0)!;
            var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
            if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned calculation instance");
            request = request with { Id = Guid.NewGuid().ToString("N"), Partition = 0, Partitions = 1,
                RecordedReplayProbe = File.ReadAllBytes(recordedPath) };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), request);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                var file = Path.Combine(workerRoot, "result.json");
                if (!File.Exists(file)) continue;
                var recordedResult = LocalWire.Read<LocalSearchResult>(file);
                if (recordedResult.Id != request.Id || recordedResult.Status == "running") continue;
                LocalWire.Write(Path.Combine(root, "integration-recorded-private.json"), recordedResult);
                LocalWire.Write(Path.Combine(root, "integration-recorded-summary.json"), new
                    { recordedResult.Status, recordedResult.Message, recordedResult.ElapsedMs, recordedResult.Best?.Won, recordedResult.Best?.StartingHp,
                        recordedResult.Best?.Hp, recordedResult.Best?.HpLost, recordedResult.Best?.NetHpLoss, enabled_in_product = false });
                return;
            }
            throw new TimeoutException("Recorded replay probe did not finish");
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_BOOTSTRAP_BENCHMARK") == "1")
        {
            request = request with { Workers = 1, MaxNodes = 1, BudgetSeconds = 60,
                InitialPlan = LocalWire.Read<LocalSearchResult>(System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!).Best!.Actions };
            LocalCandidate? baseline = null;
            var records = new List<object>();
            bool reverse = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_BOOTSTRAP_REVERSE") == "1";
            foreach (var minimal in reverse ? new[] { true, false } : new[] { false, true })
            {
                // A separate owned pool/configuration per mode includes cold startup and the
                // first native restoration; moving resource loading later does not count as a win.
                using var cold = new LocalWorkerPool(Path.Combine(root, "bootstrap-" + minimal));
                var sample = await Task.Run(() => cold.Analyze(request with { Id = Guid.NewGuid().ToString("N") },
                    installation with { MinimalWorkerBootstrap = minimal }, _ => { }, CancellationToken.None));
                var best = sample.Best ?? throw new InvalidOperationException("Bootstrap returned no route");
                if (sample.Status != "done" || sample.Rejected != 0 || !best.Won || best.Continuation?.Length != best.Actions.Length ||
                    sample.Timing?.Verifications != 1 || request.DataOnlyCombat && sample.Message.StartsWith("常规执行", StringComparison.Ordinal))
                    throw new InvalidOperationException("Bootstrap did not restore and independently verify a complete line");
                if (baseline != null && (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                    !best.Continuation!.SequenceEqual(baseline.Continuation!) || best.Hp != baseline.Hp || best.HpLost != baseline.HpLost ||
                    best.Gold != baseline.Gold || best.MaxHp != baseline.MaxHp))
                    throw new InvalidOperationException("Bootstrap changed native states/history or final settlement");
                baseline ??= best;
                LocalWire.Write(Path.Combine(root, $"integration-bootstrap-private-{records.Count}.json"), sample);
                var firstDecision = sample.Trace!.Spans.First(s => s.Stage == "search" && s.Phase == "decision").StartMs;
                records.Add(new { minimal_bootstrap = minimal, request.DataOnlyCombat, request.DataOnlyRun, sample.ElapsedMs, sample.Timing,
                    first_decision_ms = firstDecision, sample.WorkerMemoryBytes, best.Hp, best.HpLost,
                    native_states_and_history_match = true });
            }
            LocalWire.Write(Path.Combine(root, "integration-bootstrap-summary.json"), records);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_QUALITY_BENCHMARK") == "1")
        {
            var warmupSeedPath = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")
                ?? throw new InvalidOperationException("A warmup line is required");
            await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"), MaxNodes = 1,
                InitialPlan = LocalWire.Read<LocalSearchResult>(warmupSeedPath).Best!.Actions }, installation, _ => { }, CancellationToken.None));
            var records = new List<object>();
            // Both searches keep the frozen full budgets, no added line, same fast execution.
            foreach (var strategic in new[] { false, true })
            {
                var sample = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"),
                    StrategicRollouts = strategic }, installation, _ => { }, CancellationToken.None));
                var best = sample.Best ?? throw new InvalidOperationException("Quality search returned no route");
                LocalWire.Write(Path.Combine(root, $"integration-quality-private-{records.Count}.json"), sample);
                if (sample.Status != "done" || sample.Rejected != 0 || !best.Won ||
                    best.Continuation?.Length != best.Actions.Length || sample.Timing?.Verifications != 1)
                    throw new InvalidOperationException("Quality search did not complete every lane and verify its victory");
                records.Add(new { strategic_rollouts = strategic, sample.Workers, request.BudgetSeconds, request.MaxNodes,
                    sample.Status, sample.Evaluated, sample.Victories, sample.Rejected, sample.ElapsedMs, sample.Timing,
                    sample.WorkerMemoryBytes, best.Won, best.StartingHp, best.Hp, best.NetHpLoss, best.HpLost, best.Rounds,
                    used_potion = best.Actions.Any(a => a.PotionSlot.HasValue), verified_steps = best.Continuation.Length });
            }
            LocalWire.Write(Path.Combine(root, "integration-quality-summary.json"), records);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_NATIVE_DATA_PROBE") == "1")
        {
            request = request with { Workers = 1, MaxNodes = 1, BudgetSeconds = 60,
                InitialPlan = LocalWire.Read<LocalSearchResult>(System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!).Best!.Actions };
            var baseline = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"), ExperimentalNativeData = true },
                installation, _ => { }, CancellationToken.None));
            var records = new List<object>();
            foreach (var native in new[] { false, true, true, false })
            {
                var probe = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"),
                    ExperimentalNativeData = native }, installation, _ => { }, CancellationToken.None));
                LocalWire.Write(Path.Combine(root, $"integration-native-data-private-{records.Count}.json"), probe);
                bool matches = probe.Status == "done" && probe.Rejected == 0 && baseline.Best is { } before && probe.Best is { } after &&
                    before.Won == after.Won && before.Hp == after.Hp && before.HpLost == after.HpLost && before.Gold == after.Gold &&
                    before.MaxHp == after.MaxHp && before.Rounds == after.Rounds &&
                    JsonSerializer.Serialize(before.Actions) == JsonSerializer.Serialize(after.Actions) &&
                    before.Continuation!.SequenceEqual(after.Continuation!);
                records.Add(new { native_noninteractive = native, probe.Status, probe.Message, probe.ElapsedMs, probe.Timing,
                    matches, enabled_in_product = false });
            }
            LocalWire.Write(Path.Combine(root, "integration-native-data-summary.json"), records);
            // A rejected native-mode experiment is an observed result, never an instruction to relax checks.
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_WORK_BENCHMARK") == "1")
        {
            var warmupSeedPath = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")
                ?? throw new InvalidOperationException("A warmup line is required");
            // The fixed seed and one-route limit apply only to warmup. Every measured
            // search keeps the original full route/time budgets and has no added seed.
            var warm = request with { Id = Guid.NewGuid().ToString("N"), MaxNodes = 1,
                InitialPlan = LocalWire.Read<LocalSearchResult>(warmupSeedPath).Best!.Actions, ShareSearchWork = false };
            await Task.Run(() => pool.Analyze(warm, installation, _ => { }, CancellationToken.None));
            var records = new List<object>();
            foreach (var shareWork in new[] { true, false, false, true })
            {
                var sample = await Task.Run(() => pool.Analyze(request with
                    { Id = Guid.NewGuid().ToString("N"), ShareSearchWork = shareWork }, installation, _ => { }, CancellationToken.None));
                var best = sample.Best ?? throw new InvalidOperationException("Shared search returned no route");
                if (sample.Status != "done" || sample.Rejected != 0 || !best.Won ||
                    best.Continuation?.Length != best.Actions.Length || sample.Timing?.Verifications != 1)
                    throw new InvalidOperationException("Shared search did not complete all lanes and verify its victory");
                LocalWire.Write(Path.Combine(root, $"integration-work-private-{records.Count}.json"), sample);
                records.Add(new { share_search_work = shareWork, sample.Workers, request.BudgetSeconds, request.MaxNodes,
                    sample.Evaluated, sample.Victories, sample.Rejected, sample.ElapsedMs, sample.Timing, sample.Work,
                    sample.WorkerMemoryBytes, best.Won, best.StartingHp, best.Hp, best.NetHpLoss, best.HpLost, best.Rounds,
                    used_potion = best.Actions.Any(a => a.PotionSlot.HasValue), verified_steps = best.Continuation.Length });
            }
            LocalWire.Write(Path.Combine(root, "integration-work-summary.json"), records);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_SEARCH_BENCHMARK") == "1")
        {
            var seed = LocalWire.Read<LocalSearchResult>(System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")!).Best!.Actions;
            foreach (var data in new[] { false, true })
                await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"), MaxNodes = 1, InitialPlan = seed,
                    DataOnlyCombat = data, DataOnlyRun = data }, installation, _ => { }, CancellationToken.None));
            var records = new List<object>();
            foreach (var data in new[] { false, true })
            {
                var sample = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"), InitialPlan = null,
                    DataOnlyCombat = data, DataOnlyRun = data }, installation, _ => { }, CancellationToken.None));
                LocalWire.Write(Path.Combine(root, $"integration-execution-search-private-{records.Count}.json"), sample);
                var best = sample.Best ?? throw new InvalidOperationException(sample.Message);
                if (sample.Status != "done" || sample.Rejected != 0 || !best.Won || best.Continuation?.Length != best.Actions.Length ||
                    sample.Timing?.Verifications != 1 || data && sample.Message.StartsWith("常规执行", StringComparison.Ordinal))
                    throw new InvalidOperationException("Full search did not complete on its requested execution mode");
                records.Add(new { data_execution = data, request.BudgetSeconds, request.MaxNodes, request.MaxRounds,
                    sample.Workers, sample.Evaluated, sample.Victories, sample.ElapsedMs, sample.SearchElapsedMs, sample.Timing,
                    sample.WorkerMemoryBytes, best.Hp, best.HpLost, best.NetHpLoss, best.Rounds,
                    used_potion = best.Actions.Any(a => a.PotionSlot.HasValue), steps = best.Actions.Length });
                LocalWire.Write(Path.Combine(root, "integration-execution-search-summary.json"), records);
            }
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT") is { Length: > 0 } seedPath)
            request = request with { InitialPlan = LocalWire.Read<LocalSearchResult>(seedPath).Best!.Actions };
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_DATA_COMBAT_BENCHMARK") == "1")
        {
            if (request.InitialPlan is not { Length: > 0 }) throw new InvalidOperationException("A fixed complete line is required");
            bool requireDeath = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_DEATH_ROUTE") == "1";
            if (requireDeath)
            {
                // Let the real enemy commands kill the player. This covers pending loss,
                // native death hooks, run settlement and reuse after the losing route.
                var firstRound = request.InitialPlan[0].Round;
                var end = request.InitialPlan.First(a => a.EndTurn);
                request = request with { InitialPlan = Enumerable.Range(firstRound, request.MaxRounds)
                    .Select(round => end with { Round = round }).ToArray() };
            }
            request = request with { Workers = 1, MaxNodes = 1, BudgetSeconds = 60 };
            LocalCandidate? baseline = null;
            var records = new List<object>();
            async Task<LocalSearchResult> Sample(bool data)
            {
                var sample = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"),
                    DataOnlyCombat = data, DataOnlyRun = data && request.DataOnlyRun }, installation, _ => { }, CancellationToken.None));
                LocalWire.Write(Path.Combine(root, $"integration-data-combat-private-{records.Count}.json"), sample);
                var best = sample.Best ?? throw new InvalidOperationException(sample.Message);
                if (sample.Status != "done" || (requireDeath ? !best.Dead || best.Won : !best.Won) || sample.Rejected != 0 ||
                    requireDeath && best.Actions.Any(a => !a.EndTurn) ||
                    best.Continuation?.Length != best.Actions.Length || sample.Timing?.Verifications != 1 ||
                    data && (sample.Message.StartsWith("常规执行", StringComparison.Ordinal) ||
                        !sample.Trace!.Spans.Any(s => s.Stage == "search" && s.Phase == "scene" && s.Detail == "原生战斗初始化")))
                    throw new InvalidOperationException("Data combat sample did not produce a verified complete route: " + sample.Message);
                if (baseline != null && (best.Hp != baseline.Hp || best.HpLost != baseline.HpLost || best.Gold != baseline.Gold ||
                    best.MaxHp != baseline.MaxHp || best.Rounds != baseline.Rounds || best.StartingHp != baseline.StartingHp ||
                    JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                    !best.Continuation!.SequenceEqual(baseline.Continuation!)))
                    throw new InvalidOperationException("Data combat changed actions, native states, history or settlement");
                baseline ??= best;
                records.Add(new { data_combat = data, data_run = data && request.DataOnlyRun, best.Won, best.Dead, sample.ElapsedMs, sample.Timing, sample.WorkerMemoryBytes,
                    best.Hp, best.HpLost, best.NetHpLoss, best.Rounds, steps = best.Actions.Length,
                    native_states_and_history_match = true, final_verification_uses_regular_scene = true });
                LocalWire.Write(Path.Combine(root, "integration-data-combat-summary.json"), records);
                return sample;
            }
            await Sample(false); await Sample(true);
            records.Clear();
            foreach (var data in new[] { false, true, true, false }) await Sample(data);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SETTLE_BENCHMARK") == "1")
        {
            if (request.InitialPlan is not { Length: > 0 }) throw new InvalidOperationException("A fixed complete line is required");
            // One fixed route isolates execution cost. The product's full search budget is unchanged.
            request = request with { Workers = 1, MaxNodes = 1, BudgetSeconds = 60, SimulationSpeed = 8 };
            await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
            LocalCandidate? baseline = null;
            async Task<LocalSearchResult> Sample(bool events, bool collection)
            {
                var sample = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"),
                    FastStateSettling = events, FastAssetCollection = collection }, installation, _ => { }, CancellationToken.None));
                var best = sample.Best ?? throw new InvalidOperationException("Settlement sample returned no route");
                if (sample.Status != "done" || !best.Won || sample.Rejected != 0 || best.Continuation?.Length != best.Actions.Length || sample.Timing?.Verifications != 1)
                    throw new InvalidOperationException("Settlement sample did not produce independently verified victory");
                if (baseline != null && (best.Hp != baseline.Hp || best.HpLost != baseline.HpLost || best.Gold != baseline.Gold ||
                    best.MaxHp != baseline.MaxHp || best.Rounds != baseline.Rounds || best.StartingHp != baseline.StartingHp ||
                    JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                    !best.Continuation!.SequenceEqual(baseline.Continuation!)))
                    throw new InvalidOperationException("Settlement optimization changed actions, native states, history or final settlement");
                baseline ??= best;
                return sample;
            }
            await Sample(false, false); await Sample(true, false); await Sample(false, true); await Sample(true, true);
            var records = new List<object>();
            foreach (var (events, collection) in new[] { (false, false), (true, false), (false, true), (true, true),
                (true, true), (false, true), (true, false), (false, false) })
            {
                var sample = await Sample(events, collection);
                var best = sample.Best!;
                LocalWire.Write(Path.Combine(root, $"integration-settle-private-{records.Count}.json"), sample);
                records.Add(new { event_driven_settling = events, avoid_forced_asset_gc = collection,
                    request.SimulationSpeed, sample.ElapsedMs, sample.Timing, sample.WorkerMemoryBytes,
                    best.HpLost, best.Hp, best.NetHpLoss, best.Rounds, steps = best.Actions.Length,
                    native_states_and_history_match = true });
            }
            LocalWire.Write(Path.Combine(root, "integration-settle-summary.json"), records);
            return;
        }
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
            request.FastStateSettling, request.FastAssetCollection,
            request.ShareSearchWork, request.SearchOrder, result.Work, result.TurnSearch,
            result.Best.Won, result.Best.StartingHp, result.Best.Hp, result.Best.NetHpLoss, result.Best.HpLost, result.Best.Rounds,
            result.Best.EnemyHp, result.Best.StopReason,
            used_potion = result.Best.Actions.Any(a => a.PotionSlot.HasValue),
            choices = result.Best.Actions.Sum(a => a.Choices?.Length ?? 0),
            verified_steps = result.Best.Continuation!.Length
        });
    }
}
