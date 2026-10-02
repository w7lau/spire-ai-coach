using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SpireAiCoach.Mod;

/// <summary>Worker launch must isolate OS handles as well as userdata directories.</summary>
internal static class IsolatedProcess
{
    public static Process Start(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (start.UseShellExecute || start.RedirectStandardInput || start.RedirectStandardOutput ||
            start.RedirectStandardError || start.Arguments.Length != 0 || !Path.IsPathFullyQualified(start.FileName))
            throw new ArgumentException("Isolated workers require an absolute executable and ArgumentList without shell or redirected streams.");
        var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
        if (command.Length >= 32767) throw new ArgumentException("Worker command line is too long.");
        var environment = string.Join("\0", start.Environment.Where(p => p.Value != null)
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0";
        var block = Marshal.StringToHGlobalUni(environment);
        var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
        try
        {
            // Process.Start on Windows can inherit unrelated inheritable Godot file handles.
            // In a live incident a worker retained current_run.save and prevented all later saves.
            // FALSE is essential: do not clear flags on the host's handles (that would race other mods).
            // Start suspended so the Process object binds its handle before even a fast child exits.
            if (!CreateProcessW(start.FileName, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x08000000 | 0x00000400 | 0x00000004, block, start.WorkingDirectory, ref info, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start isolated worker");
            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)created.ProcessId);
                _ = process.Handle;
                if (ResumeThread(created.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume isolated worker");
                return process;
            }
            catch
            {
                TerminateProcess(created.Process, 1); // Only the newly created, still owned child.
                process?.Dispose();
                throw;
            }
            finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL in worker argument.");
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string directory, ref StartupInfo startupInfo, out ProcessInfo processInfo);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
