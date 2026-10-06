using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using HarmonyLib;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// This frozen route is a native preparation/replay acceptance check, not a
// speed or unseeded algorithm benchmark. Never submit it to the live player.
internal static class ResourceReuseIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest frozen,
        LocalInstallation installation, string seedPath)
    {
        var source = LocalWire.Read<LocalSearchResult>(seedPath);
        if (source.SnapshotId != frozen.SnapshotId || source.Best is not { Won: true, Dead: false } seed ||
            !LocalSearchPolicy.HasExecutionPoints(source)) throw new InvalidOperationException("Resource regression requires a same-root native seed");
        // Other owned experiments can add/remove aliases of the real SDK.
        // Give only its tiny release metadata an independent NTFS identity;
        // all native executable/runtime/PCK files remain the existing SDK aliases.
        var privateGame = Path.GetFullPath(Path.Combine(root, "game"));
        var metadata = Path.Combine(privateGame, "release_info.json");
        if (!File.Exists(Path.Combine(root, ".spire-native-probe-owner")) ||
            string.Equals(privateGame, Path.GetFullPath(installation.GameDirectory), StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(privateGame) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Release metadata fixture requires the exact private native host");
        var release = File.ReadAllBytes(Path.Combine(installation.GameDirectory, "release_info.json"));
        File.Delete(metadata); File.WriteAllBytes(metadata, release); // Unlink the private alias before writing its replacement.
        installation = installation with { GameDirectory = privateGame };
        var files = Directory.EnumerateFiles(installation.GameDirectory)
            .Where(p => new[] { ".exe", ".dll", ".pck", ".json" }.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
        var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        object Worker() => workers.GetValue(0)!;
        T Value<T>(string name) => (T)Worker().GetType().GetProperty(name)!.GetValue(Worker())!;
        var samples = new List<object>();
        var active = installation with { MinimalWorkerBootstrap = true };
        await pool.Prepare(active, 2, CancellationToken.None);
        var saturation = Path.GetFullPath(Path.Combine(root, "resource-saturation-" + Guid.NewGuid().ToString("N")));
        if (!saturation.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe saturation fixture path");
        Directory.CreateDirectory(saturation);
        using var aliases = new AliasFixture(metadata, saturation, 1024 - ReadIdentity(metadata).Links);
        var before = files.Select(ReadIdentity).ToArray();
        if (ReadIdentity(metadata).Links != 1024) throw new InvalidOperationException("Isolated native SDK metadata was not saturated");
        try
        {
            LocalWorkerFileSharing.Share(metadata, Path.Combine(saturation, "one-too-many"), CancellationToken.None);
            throw new InvalidOperationException("Native SDK saturation did not reject a new alias");
        }
        catch (LocalWorkerResourceException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1142 }) { }
        var cached = Value<string>("Root");
        int pid = Value<Process>("Process").Id;
        string generation = Value<string>("Generation");
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            await Check(order, active, false);
            if (Value<Process>("Process").Id != pid || Value<string>("Generation") != generation || Value<string>("Root") != cached)
                throw new InvalidOperationException("Algorithm switch recreated the native resource owner");
        }
        // A native configuration change must restart the process while keeping
        // its physical SDK tree. Stale private Mod directories cannot survive it.
        var obsolete = Path.Combine(cached, "game", "mods", "obsolete-fixture");
        Directory.CreateDirectory(obsolete);
        File.WriteAllText(Path.Combine(obsolete, "obsolete.json"), "{\"id\":\"ObsoleteFixture\",\"version\":\"0.0.1\",\"has_dll\":false,\"has_pck\":false}");
        active = installation with { MinimalWorkerBootstrap = false, LimitRuntimeThreads = false };
        using (var pause = new PreparationPause(cached))
        {
            var preparing = Task.Run(() => pool.Prepare(active, 1, CancellationToken.None));
            try
            {
                await pause.Entered.WaitAsync(TimeSpan.FromSeconds(20));
                Worker().GetType().GetMethod("Stop")!.Invoke(Worker(), ["Owned preparation cancellation probe", null, 0]);
                try
                {
                    using var prematurelyReleased = new FileStream(Path.Combine(cached, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    throw new InvalidOperationException("Cancelled preparation released its resource directory while file work was still pending");
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { }
            }
            finally { pause.Release(); }
            try { await preparing; throw new InvalidOperationException("Stopped preparation completed successfully"); }
            catch (OperationCanceledException) { }
            await (Task)Worker().GetType().GetMethod("DrainPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Worker(), null)!;
            using var released = new FileStream(Path.Combine(cached, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        await pool.Prepare(active, 1, CancellationToken.None);
        if (Value<string>("Root") != cached || Value<Process>("Process").Id == pid || Value<string>("Generation") == generation ||
            Directory.Exists(obsolete) || File.Exists(Path.Combine(cached, "game", "override.cfg")))
            throw new InvalidOperationException("Configuration change failed to reuse SDK files or retained stale private configuration");
        await Check(LocalSearchOrder.MonteCarlo, active, true);
        var after = files.Select(ReadIdentity).ToArray();
        if (!before.Select(f => f.File == "release_info.json" ? f : f with { Links = 0 })
            .SequenceEqual(after.Select(f => f.File == "release_info.json" ? f : f with { Links = 0 })) ||
            !File.ReadAllBytes(metadata).SequenceEqual(release))
            throw new InvalidOperationException("Native acceptance changed SDK identity/metadata or the isolated saturated file's aliases");
        LocalWire.Write(Path.Combine(root, "integration-resource-reuse-summary.json"), new
        {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true,
            saturatedSources = 1, saturatedFile = "release_info.json", isolatedSourceCopiedExactly = true,
            saturatedSourceLinksBefore = 1024, saturatedSourceLinksAfter = ReadIdentity(metadata).Links,
            sourceIdentityAndMetadataUnchanged = true, saturatedSourceLinksUnchanged = true,
            algorithmSwitchKeptPidAndGeneration = true, configurationChangeRestartedProcessAndReusedDiskTree = true,
            cancelledPreparationRetainedLeaseUntilDrained = true,
            stalePrivateModRemoved = true, obsoleteThreadOverrideRemoved = true, samples,
            scope = "Native SDK with one tiny isolated metadata copy saturated to 1024 links; original native executable/runtime/PCK; frozen seeded route in both algorithms; independent ordinary replay; configuration-change restart. Shared global link counts are not a fixture assertion. No unseeded speed or current-combat optimality claim."
        });

        async Task Check(LocalSearchOrder order, LocalInstallation current, bool ordinary)
        {
            var request = frozen with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order, Workers = 1,
                MaxNodes = 2, BudgetSeconds = 10, InitialPlan = seed.Actions, StopOnZeroLoss = true,
                StopOnFirstWin = false, ShareSearchWork = false, SkipFinalVerification = false,
                FastVerification = false, DeferVerification = false, VerifyCandidate = null,
                DataOnlyCombat = !ordinary, DataOnlyRun = !ordinary, NumericalExecution = !ordinary,
                InitialTrace = null, TimelineOrigin = 0 };
            var result = await Task.Run(() => pool.Analyze(request, current, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-resource-reuse-{samples.Count}-private.json"), result);
            if (result.Status != "done" || result.Best is not { Won: true, Dead: false } best || best.Hp != seed.Hp ||
                best.MaxHp != seed.MaxHp || result.Evaluated != 1 || !result.StoppedOnHealthTarget ||
                result.Timing?.Verifications != 1 || !LocalSearchPolicy.HasExecutionPoints(result) ||
                result.RecoveredFailures is { Length: > 0 } || Value<string>("Root") != cached)
                throw new InvalidOperationException("Saturated resource reuse failed native search/verification: " + result.Message);
            samples.Add(new { algorithm = order.ToString(), ordinary, result.Evaluated, best.Won, best.Hp,
                best.MaxHp, checkpoints = best.Actions.Length, independentOrdinaryReplay = true,
                result.ElapsedMs, result.StoppedOnHealthTarget, fallback = false });
        }
    }

    private sealed class PreparationPause : IDisposable
    {
        private static PreparationPause? _current;
        private readonly string _staging;
        private readonly ManualResetEventSlim _resume = new(false);
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Harmony _harmony = new("SpireLocalIntegration.owned-file-preparation-pause");
        private int _armed = 1;
        public Task Entered => _entered.Task;
        public PreparationPause(string root)
        {
            if (!File.Exists(Path.Combine(root, ".coach-worker"))) throw new InvalidOperationException("File pause requires an owned worker");
            _staging = root + Path.DirectorySeparatorChar + "mods-";
            _current = this;
            var worker = typeof(LocalWorkerPool).GetNestedType("Worker", BindingFlags.NonPublic)!;
            _harmony.Patch(worker.GetMethod("CopyFile", BindingFlags.Static | BindingFlags.NonPublic)!,
                prefix: new HarmonyMethod(typeof(PreparationPause).GetMethod(nameof(Hold), BindingFlags.Static | BindingFlags.NonPublic)!));
        }
        private static void Hold(string target)
        {
            var current = _current;
            if (current == null || !target.StartsWith(current._staging, StringComparison.OrdinalIgnoreCase) ||
                Interlocked.CompareExchange(ref current._armed, 0, 1) != 1) return;
            current._entered.TrySetResult(true);
            if (!current._resume.Wait(TimeSpan.FromSeconds(25))) throw new TimeoutException("Owned file preparation pause was not released");
        }
        public void Release() => _resume.Set();
        public void Dispose() { Release(); _harmony.UnpatchAll(_harmony.Id); _current = null; _resume.Dispose(); }
    }

    private sealed class AliasFixture : IDisposable
    {
        private readonly List<string> _paths = [];
        public AliasFixture(string source, string root, uint count)
        {
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    var path = Path.Combine(root, "alias-" + i);
                    LocalWorkerFileSharing.Share(source, path, CancellationToken.None);
                    _paths.Add(path);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var path in _paths) File.Delete(path); }
    }

    private sealed record Identity(string File, uint Links, uint Volume, uint IndexHigh, uint IndexLow, long Length, DateTime Written);
    private static Identity ReadIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var method = typeof(LocalWorkerFileSharing).GetMethod("GetFileInformationByHandle", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] args = [handle, null];
        if (!(bool)method.Invoke(null, args)!) throw new IOException("Cannot read native SDK identity");
        var data = args[1]!;
        uint Field(string name) => (uint)data.GetType().GetField(name)!.GetValue(data)!;
        var file = new FileInfo(path);
        return new(file.Name, Field("Links"), Field("Volume"), Field("IndexHigh"), Field("IndexLow"), file.Length, file.LastWriteTimeUtc);
    }
}
