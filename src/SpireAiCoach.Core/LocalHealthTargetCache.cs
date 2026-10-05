using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// Only owned workers use this request-scoped estimate. The strict recovery
// certificate stays local to each process and is never imported from this file.
public static class LocalHealthTargetCache
{
    public static LocalHealthTarget? Get(string directory, LocalSearchRequest request, int startingHp,
        Func<LocalHealthTarget> calculate)
    {
        string scope = LocalMinimumLossProof.Scope(request);
        string mutexName = "SpireAiCoach-HealthTarget-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(directory))));
        using var gate = new Mutex(false, mutexName);
        bool owned = false;
        try
        {
            try { owned = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return null;
            string path = Path.Combine(directory, "health-target.json");
            if (File.Exists(path))
            {
                var cached = JsonSerializer.Deserialize<LocalHealthTarget>(File.ReadAllText(path));
                if (cached?.Scope == scope && cached.StartingHp == startingHp) return cached;
            }
            var target = calculate();
            if (target.Scope != scope || target.StartingHp != startingHp)
                throw new InvalidDataException("Health target belongs to another restored root");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(target));
            File.Move(path + ".tmp", path, true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return null; } // No estimate available: existing full-health/proof stops still apply.
        finally { if (owned) gate.ReleaseMutex(); }
    }
}
