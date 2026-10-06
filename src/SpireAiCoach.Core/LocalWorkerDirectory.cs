namespace SpireAiCoach.Core;

// Process/configuration identity is independent of the leased disk installation.
// Reuse idle v2 launch trees before adding more NTFS aliases of the same SDK.
public sealed class LocalWorkerDirectory : IDisposable
{
    public const string Ownership = "SpireAiCoach shared local worker v2";
    private readonly FileStream _lock;
    public string Root { get; }
    public bool Reused { get; }

    private LocalWorkerDirectory(string root, FileStream lease, bool reused)
    { Root = root; _lock = lease; Reused = reused; }

    public void Dispose() => _lock.Dispose();

    public static LocalWorkerDirectory Acquire(string sharedRoot, string preferredRoot, CancellationToken token)
    {
        sharedRoot = Path.GetFullPath(sharedRoot);
        preferredRoot = Path.GetFullPath(preferredRoot);
        Validate(sharedRoot, preferredRoot);
        token.ThrowIfCancellationRequested();
        if (Reusable(preferredRoot) && TryAcquire(preferredRoot, false) is { } prior) return prior;
        if (Directory.Exists(sharedRoot))
        {
            // Only the two known layout levels; never walk SDK/Mod/cache contents.
            foreach (var group in new DirectoryInfo(sharedRoot).EnumerateDirectories()
                .Where(d => HexGroup(d.Name) && (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(d => d.LastWriteTimeUtc))
                for (var i = 0; i < 16; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var candidate = Path.Combine(group.FullName, "worker-" + i);
                    if (candidate == preferredRoot || !Reusable(candidate)) continue;
                    if (TryAcquire(candidate, false) is { } idle) return idle;
                }
        }
        return TryAcquire(preferredRoot, true) ?? throw new LocalWorkerResourceException("计算资源目录正在被其它实例使用。");

        LocalWorkerDirectory? TryAcquire(string root, bool create)
        {
            Validate(sharedRoot, root);
            bool owned = Owned(root);
            if (!owned && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                if (create) throw new LocalWorkerResourceException("拒绝使用没有所有权标记的计算目录。");
                return null;
            }
            if (!owned && !create) return null;
            if (create) Directory.CreateDirectory(root);
            FileStream lease;
            try { lease = new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
            try
            {
                token.ThrowIfCancellationRequested();
                if (owned && !Owned(root)) throw new LocalWorkerResourceException("计算目录所有权已改变。");
                if (!owned) File.WriteAllText(Path.Combine(root, ".coach-worker"), Ownership);
                return new(root, lease, owned && Reusable(root));
            }
            catch { lease.Dispose(); throw; }
        }
    }

    private static bool HexGroup(string name) => name.Length == 16 && name.All(Uri.IsHexDigit);
    private static bool Owned(string root)
    {
        var marker = Path.Combine(root, ".coach-worker");
        return File.Exists(marker) && !Linked(marker) && File.ReadAllText(marker) == Ownership;
    }
    private static bool Reusable(string root) => Directory.Exists(root) && !Linked(root) &&
        !Linked(Path.Combine(root, "game")) && Owned(root) &&
        File.Exists(Path.Combine(root, "game", "SlayTheSpire2.exe")) &&
        File.Exists(Path.Combine(root, "game", "SlayTheSpire2.pck"));
    private static bool Linked(string path) => (File.Exists(path) || Directory.Exists(path)) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void Validate(string sharedRoot, string root)
    {
        var group = Path.GetDirectoryName(root)!;
        var name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(group), sharedRoot, StringComparison.OrdinalIgnoreCase) ||
            !HexGroup(Path.GetFileName(group)) || !name.StartsWith("worker-", StringComparison.Ordinal) ||
            !int.TryParse(name.AsSpan(7), out var index) || index is < 0 or > 15 || name != "worker-" + index ||
            Linked(sharedRoot) || Linked(group) || Linked(root) || Linked(Path.Combine(root, ".lock")) ||
            Linked(Path.Combine(root, ".coach-worker")) || Linked(Path.Combine(root, "game")))
            throw new LocalWorkerResourceException("计算资源目录路径或链接不符合隔离要求。");
    }
}

public sealed class LocalWorkerResourceException(string message, Exception? inner = null) : IOException(message, inner);
