using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

public sealed class LocalWorkerPool(string directory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Worker[] _workers = Enumerable.Range(0, 16).Select(_ => new Worker()).ToArray();
    private bool _disposed;

    public async Task<LocalSearchResult> Analyze(LocalSearchRequest request, LocalInstallation installation,
        Action<string> progress, CancellationToken cancellation, Action<LocalProgress>? simulationProgress = null)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var totalTime = Stopwatch.StartNew();
            var memory = new MemoryStatus();
            if (!GlobalMemoryStatusEx(memory)) throw new IOException("Cannot determine available memory for local workers");
            // Keep automatic sizing stable when reusing our own already allocated worker heaps.
            ulong reusable = (ulong)_workers.Where(w => w.Process?.HasExited == false).Sum(w => w.Process!.PrivateMemorySize64);
            int count = LocalSearchPolicy.WorkerCount(Environment.ProcessorCount, memory.AvailablePhysical + reusable, request.Workers);
            foreach (var idle in _workers.Skip(count)) idle.Stop();
            progress($"正在准备 {count} 个独立工作进程，共享游戏资源文件；搜索整场战斗（最多 {request.MaxRounds} 轮）…");
            var results = await Task.WhenAll(_workers.Take(count).Select((worker, index) => Run(worker, index)));
            cancellation.ThrowIfCancellationRequested();
            var valid = results.Where(r => r.Status is "done" or "partial" && r.Best != null).ToArray();
            if (valid.Length == 0)
                throw new CoachException("local_failed", string.Join("\n", results.Select(r => r.Message).Distinct()));
            var best = valid.Aggregate((a, b) => LocalSearchPolicy.Better(b.Best!, a.Best) ? b : a);
            return best with { Evaluated = results.Sum(r => r.Evaluated), Rejected = results.Sum(r => r.Rejected),
                Duplicates = results.Sum(r => r.Duplicates), BudgetPruned = results.Sum(r => r.BudgetPruned),
                Victories = valid.Sum(r => r.Victories), Workers = count,
                ElapsedMs = totalTime.ElapsedMilliseconds, SearchElapsedMs = results.Max(r => r.ElapsedMs),
                WorkerMemoryBytes = results.Sum(r => r.WorkerMemoryBytes),
                IncludePotions = request.IncludePotions,
                Timing = new(results.Sum(r => r.Timing?.RestoreMs ?? 0), results.Sum(r => r.Timing?.ActionMs ?? 0),
                    results.Sum(r => r.Timing?.DecisionMs ?? 0), results.Sum(r => r.Timing?.VerificationMs ?? 0),
                    results.Sum(r => r.Timing?.StartupMs ?? 0), results.Sum(r => r.Timing?.Actions ?? 0), results.Sum(r => r.Timing?.Restores ?? 0)),
                Status = results.All(r => r.Status == "done") ? "done" : "partial",
                Message = "本地整场计算完成。" + (valid.Length < count ? "部分工作进程未完成，仅保留已验证的路线。" : "") +
                    "预算内候选，不保证最优；未知奖励机制仍按预算搜索。" };

            async Task<LocalSearchResult> Run(Worker worker, int index)
            {
                try
                {
                    simulationProgress?.Invoke(new(request.Id, request.SnapshotId, index, count, 0, 0, 0, request.MaxNodes, 0,
                        0, request.BudgetSeconds, "准备静音工作进程", null, []));
                    var preparation = Stopwatch.StartNew();
                    await worker.Ensure(directory, index, installation, cancellation);
                    preparation.Stop();
                    var command = request with { Partition = index, Partitions = count };
                    LocalWire.Write(Path.Combine(worker.Root, "request.json"), command);
                    var timer = Stopwatch.StartNew();
                    long seenSequence = 0;
                    while (timer.Elapsed.TotalSeconds < 180)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var previewPath = Path.Combine(worker.Root, "progress.json");
                        if (simulationProgress != null && File.Exists(previewPath))
                        {
                            var preview = LocalWire.Read<LocalProgress>(previewPath);
                            if (preview.Id == request.Id && preview.SnapshotId == request.SnapshotId && preview.Worker == index &&
                                preview.Workers == count && preview.Sequence > seenSequence)
                            { seenSequence = preview.Sequence; simulationProgress(preview); }
                        }
                        var file = Path.Combine(worker.Root, "result.json");
                        if (File.Exists(file))
                        {
                            var result = LocalWire.Read<LocalSearchResult>(file);
                            if (result.Id == request.Id && result.SnapshotId == request.SnapshotId)
                            {
                                if (result.Status != "running")
                                {
                                    result = result with { Timing = (result.Timing ?? new()) with { StartupMs = preparation.ElapsedMilliseconds } };
                                    if (worker.Process?.HasExited == false)
                                    { worker.Process.Refresh(); result = result with { WorkerMemoryBytes = worker.Process.PrivateMemorySize64 }; }
                                    if (worker.GameErrors())
                                    {
                                        worker.Stop();
                                        return result with { Status = "failed", Best = null, Message = "后台游戏报告运行错误，未采用该进程的结果。" };
                                    }
                                    if (result.Status != "done") worker.Stop();
                                    return result;
                                }
                                progress($"本地进程 {index + 1}/{count}：已评估 {result.Evaluated} 条路线。{result.Message}");
                            }
                        }
                        if (worker.Process?.HasExited != false)
                            throw new CoachException("local_exit", "本地工作进程已退出，未取得完整结果。");
                        await Task.Delay(250, cancellation);
                    }
                    throw new CoachException("local_timeout", "本地工作进程超时，已停止；可以重试或使用 AI 模式。");
                }
                catch (OperationCanceledException) { worker.Stop(); throw; }
                catch (Exception ex)
                {
                    worker.Stop();
                    return new(request.Id, request.SnapshotId, "failed", ex is CoachException ? ex.Message :
                        $"本地进程准备失败（{ex.GetType().Name}）：{ex.Message}", 0, 0, 0, null);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var worker in _workers) worker.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

    private sealed class Worker
    {
        public string Root { get; private set; } = "";
        public Process? Process { get; private set; }
        private FileStream? _lock;
        private string? _configuration;

        public async Task Ensure(string directory, int index, LocalInstallation installation, CancellationToken token)
        {
            var signature = typeof(LocalWorker).Assembly.ManifestModule.ModuleVersionId + "|" +
                installation.GameDirectory + "|" + Path.GetFullPath(directory) + "|" + string.Join("|", installation.ModDirectories);
            var configuration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
            if (Process?.HasExited == false && _configuration == configuration) return;
            Stop();
            _configuration = configuration;
            // NTFS hardlinks require one volume. Keep tiny launch trees beside the installation, never in it.
            var sharedRoot = Path.Combine(Directory.GetParent(installation.GameDirectory)!.FullName, ".spire-ai-coach-workers");
            Root = Path.GetFullPath(Path.Combine(sharedRoot, configuration, "worker-" + index));
            if (Root.StartsWith(Path.GetFullPath(installation.GameDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Worker must not be inside game installation");
            if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any() && !File.Exists(Path.Combine(Root, ".coach-worker")))
                throw new IOException("Refusing an unowned worker directory");
            Directory.CreateDirectory(Root);
            _lock = new FileStream(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None);
            File.WriteAllText(Path.Combine(Root, ".coach-worker"), "SpireAiCoach shared local worker v2");
            var game = Path.Combine(Root, "game");
            Directory.CreateDirectory(game);
            foreach (var source in Directory.EnumerateFiles(installation.GameDirectory))
                if (new[] { ".exe", ".dll", ".pck", ".json" }.Contains(Path.GetExtension(source).ToLowerInvariant()))
                    ShareFile(source, Path.Combine(game, Path.GetFileName(source)), token);
            foreach (var source in Directory.EnumerateDirectories(installation.GameDirectory, "data_sts2_*"))
                ShareTree(source, Path.Combine(game, Path.GetFileName(source)), token);
            for (var i = 0; i < installation.ModDirectories.Length; i++)
                CopyTree(installation.ModDirectories[i], Path.Combine(game, "mods", "loaded-" + i), token);
            var roaming = Path.Combine(Root, "Roaming");
            var local = Path.Combine(Root, "Local");
            var settings = Path.Combine(roaming, "SlayTheSpire2", "default", "1");
            Directory.CreateDirectory(settings); Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(settings, "settings.save"), "{\"volume_master\":0,\"volume_bgm\":0,\"volume_sfx\":0,\"volume_ambience\":0,\"skip_intro_logo\":true,\"mod_settings\":{\"mods_enabled\":true,\"mod_list\":[]}}");
            var saves = Path.Combine(settings, "modded", "profile1", "saves"); Directory.CreateDirectory(saves);
            File.WriteAllText(Path.Combine(saves, "progress.save"), "{\"schema_version\":24,\"enable_ftues\":false,\"ftue_completed\":[\"combat_rules_ftue\"]}");
            foreach (var name in new[] { "ready", "fatal.txt", "result.json", "request.json", "progress.json", "audio.json" }) File.Delete(Path.Combine(Root, name));
            var start = new ProcessStartInfo(Path.Combine(game, "SlayTheSpire2.exe"))
            { WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--headless", "--audio-driver", "Dummy", "--disable-vsync", "--max-fps", "120", "--force-steam=off", "--log-file", Path.Combine(Root, "game.log") }) start.ArgumentList.Add(arg);
            start.Environment["APPDATA"] = roaming; start.Environment["LOCALAPPDATA"] = local;
            start.Environment["SPIRE_COACH_WORKER"] = Root;
            start.Environment.Remove("SPIRE_NATIVE_PROBE_ROOT");
            token.ThrowIfCancellationRequested();
            Process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Worker did not start");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(Root, "ready")))
            {
                token.ThrowIfCancellationRequested();
                if (Process.HasExited || timer.Elapsed.TotalSeconds > 90) throw new IOException("Worker startup failed or timed out");
                await Task.Delay(250, token);
            }
        }

        public bool GameErrors()
        {
            var path = Path.Combine(Root, "game.log");
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("[ERROR]", StringComparison.Ordinal);
        }

        private static void CopyFile(string source, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(source);
            var prior = new FileInfo(target);
            if (!prior.Exists || prior.Length != info.Length || prior.LastWriteTimeUtc != info.LastWriteTimeUtc)
                File.Copy(source, target, true); // Never hardlink a writable worker to the live installation.
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

        private static void ShareFile(string source, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Remove the old directory entry, never overwrite a shared inode (including after Steam updates).
            if (File.Exists(target)) File.Delete(target);
            if (!CreateHardLink(target, source, IntPtr.Zero))
                throw new IOException("无法共享游戏资源文件（需要同盘 NTFS）；未回退为复制整套资源。", Marshal.GetLastWin32Error());
        }
        private static void ShareTree(string source, string target, CancellationToken token)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source)) ShareFile(file, Path.Combine(target, Path.GetFileName(file)), token);
            foreach (var child in Directory.EnumerateDirectories(source))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked game directories are not supported");
                ShareTree(child, Path.Combine(target, Path.GetFileName(child)), token);
            }
        }
        private static void CopyTree(string source, string target, CancellationToken token)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source)) CopyFile(file, Path.Combine(target, Path.GetFileName(file)), token);
            foreach (var child in Directory.EnumerateDirectories(source))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Mod directories are not supported");
                CopyTree(child, Path.Combine(target, Path.GetFileName(child)), token);
            }
        }
        public void Stop()
        {
            try
            {
                if (Process?.HasExited == false) { Process.Kill(entireProcessTree: true); Process.WaitForExit(5000); }
            }
            catch (InvalidOperationException) { }
            Process?.Dispose(); Process = null;
            _lock?.Dispose(); _lock = null;
        }
    }
}
