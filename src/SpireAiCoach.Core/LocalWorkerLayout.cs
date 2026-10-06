using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace SpireAiCoach.Core;

// Code generations retain their full version identity. An exclusively leased
// resource layout can outlive them without adding another set of native links.
public static class LocalWorkerLayout
{
    private const string Owner = "SpireAiCoach shared local worker v2";
    private const string ScopeFile = ".coach-layout";
    public static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    public static (string Root, FileStream Lock) Acquire(string sharedRoot, string scope, int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (index is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(index));
        sharedRoot = Path.GetFullPath(sharedRoot);
        Directory.CreateDirectory(sharedRoot);
        Unlinked(sharedRoot);
        var binding = "layout-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        var preferred = Path.Combine(sharedRoot, Key("layout-v1|" + scope), "worker-" + index);
        if (Directory.Exists(preferred) && TryLease(preferred, false) is { } existing) return existing;
        foreach (var configuration in Directory.EnumerateDirectories(sharedRoot))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(configuration);
            if (name.Length != 16 || name.Any(c => !char.IsAsciiHexDigit(c))) continue;
            if ((File.GetAttributes(configuration) & FileAttributes.ReparsePoint) != 0) continue;
            var candidate = Path.Combine(configuration, "worker-" + index);
            if (candidate == preferred || !Directory.Exists(candidate)) continue;
            if (TryLease(candidate, false) is { } legacy) return legacy;
        }
        if (TryLease(preferred, true) is { } fresh) return fresh;
        throw new IOException("相同配置的本地实例资源目录仍在使用中。");

        (string Root, FileStream Lock)? TryLease(string root, bool create)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(root))
            {
                Unlinked(Path.GetDirectoryName(root)!); Unlinked(root);
                var owner = Path.Combine(root, ".coach-worker");
                if (!File.Exists(owner))
                {
                    if (Directory.EnumerateFileSystemEntries(root).Any())
                    {
                        if (root == preferred) throw new IOException("Refusing an unowned worker directory");
                        return null;
                    }
                    if (!create) return null;
                }
                else { Unlinked(owner); if (File.ReadAllText(owner) != Owner) return null; }
            }
            else if (!create) return null;
            if (create) Directory.CreateDirectory(root);
            Unlinked(Path.GetDirectoryName(root)!); Unlinked(root);
            var lockPath = Path.Combine(root, ".lock");
            if (File.Exists(lockPath)) Unlinked(lockPath);
            FileStream lease;
            try { lease = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return null; }
            bool accepted = false;
            try
            {
                var tag = Path.Combine(root, ScopeFile);
                bool empty = !File.Exists(Path.Combine(root, ".coach-worker"));
                if (File.Exists(tag))
                {
                    Unlinked(tag);
                    if (File.ReadAllText(tag) != binding) return null;
                }
                else if (!empty && !LegacyMatches(root, scope)) return null;
                token.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(root, ".coach-worker"), Owner);
                File.WriteAllText(tag, binding);
                accepted = true;
                return (root, lease);
            }
            finally { if (!accepted) lease.Dispose(); }
        }
    }

    private static bool LegacyMatches(string root, string scope)
    {
        var mods = Path.Combine(root, "game", "mods");
        if (!Directory.Exists(mods)) return false;
        Unlinked(Path.Combine(root, "game")); Unlinked(mods);
        foreach (var directory in Directory.EnumerateDirectories(mods))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var assembly = Path.Combine(directory, "SpireAiCoach.dll");
            if (!File.Exists(assembly)) continue;
            Unlinked(assembly);
            try
            {
                using var file = File.OpenRead(assembly);
                using var pe = new PEReader(file);
                if (!pe.HasMetadata) continue;
                var metadata = pe.GetMetadataReader();
                var module = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
                if (Path.GetFileName(Path.GetDirectoryName(root)) == Key(module + "|" + scope)) return true;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            { /* An unreadable legacy identity does not authorize adopting its layout. */ }
        }
        return false;
    }

    private static void Unlinked(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked worker resource paths are not supported");
    }
}
