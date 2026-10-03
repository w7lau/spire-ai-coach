using SpireAiCoach.Core;

internal static class WorkerSharingTests
{
    public static Task Run()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(parent, "spire-shared-" + Guid.NewGuid().ToString("N")));
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe test directory");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        var target = Path.Combine(root, "target.bin");
        var separate = Path.Combine(root, "same-metadata.bin");
        void Link(string destination) => LocalWorkerFileSharing.Share(source, destination, CancellationToken.None);
        try
        {
            File.WriteAllText(source, "original");
            Link(target);
            // Windows denies deleting any alias of this locked file. An unchanged hardlink
            // must remain usable while a different owned process maps the shared image.
            using (File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read)) Link(target);
            File.Copy(source, separate);
            File.SetLastWriteTimeUtc(separate, File.GetLastWriteTimeUtc(source));
            Link(separate); // Identical size/time/content does not establish shared file identity.
            File.AppendAllText(source, "-updated");
            if (File.ReadAllText(target) != "original-updated" || File.ReadAllText(separate) != "original-updated")
                throw new InvalidOperationException("Game files were copied or an unrelated equal-metadata file was incorrectly reused");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
