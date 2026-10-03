using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpireAiCoach.Core;

public static class LocalWorkerFileSharing
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity identity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    private static bool AlreadyShared(string source, string target)
    {
        using var original = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var existing = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(original, out var a) || !GetFileInformationByHandle(existing, out var b))
            throw new IOException("Cannot inspect shared game file identity", Marshal.GetLastWin32Error());
        return a.Volume == b.Volume && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow;
    }

    // Caller owns/validates the destination installation and lock. Never overwrite the
    // shared file: unlink a stale destination, or reuse the exact existing hardlink.
    public static void Share(string source, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(target) && AlreadyShared(source, target)) return;
        if (File.Exists(target)) File.Delete(target);
        if (!CreateHardLink(target, source, IntPtr.Zero))
            throw new IOException("无法共享游戏资源文件（需要同盘 NTFS）；未回退为复制整套资源。", Marshal.GetLastWin32Error());
    }
}
