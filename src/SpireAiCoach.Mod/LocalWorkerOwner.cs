using System.Diagnostics;
using System.Globalization;

namespace SpireAiCoach.Mod;

// Process identity prevents a recycled PID from keeping an orphaned worker alive.
internal sealed class LocalWorkerOwner(Process process, long started) : IDisposable
{
    public static void Attach(ProcessStartInfo info)
    {
        using var current = Process.GetCurrentProcess();
        info.Environment["SPIRE_COACH_OWNER_PID"] = current.Id.ToString(CultureInfo.InvariantCulture);
        info.Environment["SPIRE_COACH_OWNER_STARTED"] = current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
    }

    public static LocalWorkerOwner? Open()
    {
        var pid = System.Environment.GetEnvironmentVariable("SPIRE_COACH_OWNER_PID");
        if (pid == null) return null; // Standalone owned probes retain their bounded idle exit.
        if (!int.TryParse(pid, out var id) || !long.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_COACH_OWNER_STARTED"), out var started))
            throw new InvalidDataException("Invalid calculation owner identity");
        return new(Process.GetProcessById(id), started);
    }

    public bool IsAlive
    {
        get
        {
            try { return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public void Dispose() => process.Dispose();
}
