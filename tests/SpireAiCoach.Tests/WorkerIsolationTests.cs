using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SpireAiCoach.Mod;

internal static class WorkerIsolationTests
{
    public static async Task<int> Child(string[] args)
    {
        File.WriteAllText(args[1], JsonSerializer.Serialize(new
        { Args = args.Skip(3).ToArray(), Directory = Environment.CurrentDirectory,
            Roaming = Environment.GetEnvironmentVariable("APPDATA"),
            Local = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Marker = Environment.GetEnvironmentVariable("SPIRE_ISOLATION_TEST") }));
        var watch = Stopwatch.StartNew();
        while (!File.Exists(args[2]) && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(25);
        return File.Exists(args[2]) ? 0 : 3;
    }

    public static async Task Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.GetFullPath(Path.Combine("work", "worker isolation 中文 " + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string[] samples = ["", "hello world", "中文", "quoted\"text", "C:\\space here\\", "back\\\"quote", "a\\\\b"];
        try
        {
            // Control reproduces the incident without a game or real save. Treatment must allow
            // replacement while the child is alive after the parent's original handle is closed.
            foreach (bool isolated in new[] { false, true })
            {
                var path = Path.Combine(root, "synthetic.save");
                File.WriteAllText(path, "old");
                var ready = Path.Combine(root, "ready.json"); var stop = Path.Combine(root, "stop");
                File.Delete(ready); File.Delete(stop);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (!SetHandleInformation(stream.SafeFileHandle.DangerousGetHandle(), 1, 1))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
                if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(WorkerIsolationTests).Assembly.Location);
                foreach (var arg in new[] { "--isolation-child", ready, stop }.Concat(samples)) start.ArgumentList.Add(arg);
                start.Environment["APPDATA"] = Path.Combine(root, "Roaming");
                start.Environment["LOCALAPPDATA"] = Path.Combine(root, "Local");
                start.Environment["SPIRE_ISOLATION_TEST"] = "隔离环境=value";
                using var process = isolated ? IsolatedProcess.Start(start) : Process.Start(start)!;
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(ready) && timer.Elapsed < TimeSpan.FromSeconds(10) && !process.HasExited) await Task.Delay(25);
                    if (!File.Exists(ready)) throw new Exception("Child did not become ready.");
                    // The child finished writing before this wait; retry reading only transient sharing.
                    JsonDocument? document = null;
                    for (int i = 0; i < 20 && document == null; i++)
                    {
                        try { document = JsonDocument.Parse(File.ReadAllText(ready)); }
                        catch (IOException) { await Task.Delay(25); }
                    }
                    using var payload = document ?? throw new Exception("Child payload unavailable.");
                    var data = payload.RootElement;
                    if (!data.GetProperty("Args").EnumerateArray().Select(x => x.GetString()).SequenceEqual(samples) ||
                        data.GetProperty("Directory").GetString() != root ||
                        data.GetProperty("Roaming").GetString() != start.Environment["APPDATA"] ||
                        data.GetProperty("Local").GetString() != start.Environment["LOCALAPPDATA"] ||
                        data.GetProperty("Marker").GetString() != start.Environment["SPIRE_ISOLATION_TEST"])
                        throw new Exception("Child arguments/environment changed.");
                    stream.Dispose();
                    var replacement = path + ".tmp"; File.WriteAllText(replacement, "new");
                    bool blocked = false;
                    try { File.Move(replacement, path, true); }
                    catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { blocked = true; }
                    catch (UnauthorizedAccessException) when (!isolated) { blocked = true; }
                    if (blocked == isolated) throw new Exception($"Unexpected file lock: isolated={isolated}, blocked={blocked}");
                    if (isolated && (process.HasExited || File.ReadAllText(path) != "new"))
                        throw new Exception("Replacement must succeed with child still alive.");
                    File.WriteAllText(stop, "stop");
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (process.ExitCode != 0) throw new Exception("Child failed.");
                    Console.WriteLine($"  worker isolation: isolated={isolated}, replacement_blocked={blocked}");
                }
                finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            }
            try
            {
                using var unexpected = IsolatedProcess.Start(new ProcessStartInfo(Path.Combine(root, "absent.exe")) { UseShellExecute = false });
                throw new Exception("Missing executable unexpectedly started.");
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        finally { Directory.Delete(root, true); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
