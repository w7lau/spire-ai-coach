using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

public sealed class LocalWorkerPool(string directory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Worker[] _workers = [new(), new()];
    private bool _disposed;

    public async Task<LocalSearchResult> Analyze(LocalSearchRequest request, LocalInstallation installation,
        Action<string> progress, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            progress("正在准备两个独立的本地工作进程；首次需复制游戏资源，约占 6 GB…");
            var results = await Task.WhenAll(_workers.Select((worker, index) => Run(worker, index)));
            cancellation.ThrowIfCancellationRequested();
            var valid = results.Where(r => r.Status is "done" or "partial" && r.Best != null).ToArray();
            if (valid.Length == 0)
                throw new CoachException("local_failed", string.Join("\n", results.Select(r => r.Message).Distinct()));
            var best = valid.Aggregate((a, b) => LocalSearchPolicy.Better(b.Best!, a.Best) ? b : a);
            return best with { Evaluated = results.Sum(r => r.Evaluated), Rejected = results.Sum(r => r.Rejected),
                Status = results.All(r => r.Status == "done") ? "done" : "partial",
                Message = "本地计算完成。" + (valid.Length < 2 ? "部分工作进程未完成，仅保留已验证的路线。" : "") +
                    "预算内候选，不保证最优；未知奖励机制仍按预算搜索。" };

            async Task<LocalSearchResult> Run(Worker worker, int index)
            {
                try
                {
                    await worker.Ensure(directory, index, installation, cancellation);
                    var command = request with { Partition = index, Partitions = 2 };
                    LocalWire.Write(Path.Combine(worker.Root, "request.json"), command);
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed.TotalSeconds < 180)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var file = Path.Combine(worker.Root, "result.json");
                        if (File.Exists(file))
                        {
                            var result = LocalWire.Read<LocalSearchResult>(file);
                            if (result.Id == request.Id && result.SnapshotId == request.SnapshotId)
                            {
                                if (result.Status != "running")
                                {
                                    if (worker.GameErrors())
                                    {
                                        worker.Stop();
                                        return result with { Status = "failed", Best = null, Message = "后台游戏报告运行错误，未采用该进程的结果。" };
                                    }
                                    if (result.Status != "done") worker.Stop();
                                    return result;
                                }
                                progress($"本地进程 {index + 1}：已结算 {result.Evaluated} 条路线。{result.Message}");
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

    private sealed class Worker
    {
        public string Root { get; private set; } = "";
        public Process? Process { get; private set; }
        private FileStream? _lock;
        private string? _configuration;

        public async Task Ensure(string directory, int index, LocalInstallation installation, CancellationToken token)
        {
            var signature = typeof(LocalWorker).Assembly.ManifestModule.ModuleVersionId + "|" +
                installation.GameDirectory + "|" + string.Join("|", installation.ModDirectories);
            var configuration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
            if (Process?.HasExited == false && _configuration == configuration) return;
            Stop();
            _configuration = configuration;
            Root = Path.GetFullPath(Path.Combine(directory, configuration, "worker-" + index));
            if (Root.StartsWith(Path.GetFullPath(installation.GameDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Worker must not be inside game installation");
            if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any() && !File.Exists(Path.Combine(Root, ".coach-worker")))
                throw new IOException("Refusing an unowned worker directory");
            Directory.CreateDirectory(Root);
            _lock = new FileStream(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None);
            File.WriteAllText(Path.Combine(Root, ".coach-worker"), "SpireAiCoach local worker v1");
            var game = Path.Combine(Root, "game");
            Directory.CreateDirectory(game);
            foreach (var source in Directory.EnumerateFiles(installation.GameDirectory))
                if (new[] { ".exe", ".dll", ".pck", ".json" }.Contains(Path.GetExtension(source).ToLowerInvariant()))
                    CopyFile(source, Path.Combine(game, Path.GetFileName(source)), token);
            foreach (var source in Directory.EnumerateDirectories(installation.GameDirectory, "data_sts2_*"))
                CopyTree(source, Path.Combine(game, Path.GetFileName(source)), token);
            for (var i = 0; i < installation.ModDirectories.Length; i++)
                CopyTree(installation.ModDirectories[i], Path.Combine(game, "mods", "loaded-" + i), token);
            var roaming = Path.Combine(Root, "Roaming");
            var local = Path.Combine(Root, "Local");
            var settings = Path.Combine(roaming, "SlayTheSpire2", "default", "1");
            Directory.CreateDirectory(settings); Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(settings, "settings.save"), "{\"mod_settings\":{\"mods_enabled\":true,\"mod_list\":[]}}");
            var saves = Path.Combine(settings, "modded", "profile1", "saves"); Directory.CreateDirectory(saves);
            File.WriteAllText(Path.Combine(saves, "progress.save"), "{\"schema_version\":24,\"enable_ftues\":false,\"ftue_completed\":[\"combat_rules_ftue\"]}");
            foreach (var name in new[] { "ready", "fatal.txt", "result.json", "request.json" }) File.Delete(Path.Combine(Root, name));
            var start = new ProcessStartInfo(Path.Combine(game, "SlayTheSpire2.exe"))
            { WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--headless", "--disable-vsync", "--max-fps", "120", "--force-steam=off", "--log-file", Path.Combine(Root, "game.log") }) start.ArgumentList.Add(arg);
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
                if (Process?.HasExited == false) { Process.Kill(); Process.WaitForExit(5000); }
            }
            catch (InvalidOperationException) { }
            Process?.Dispose(); Process = null;
            _lock?.Dispose(); _lock = null;
        }
    }
}
