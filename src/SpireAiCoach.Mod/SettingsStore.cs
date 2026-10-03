using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

public sealed class SettingsStore
{
    private readonly string _directory;
    public SettingsStore(string directory) => _directory = directory;
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string KeyPath => Path.Combine(_directory, "api-key.dpapi");

    public (CoachSettings Settings, string Key) Load()
    {
        if (!File.Exists(ConfigPath)) return (new(), "");
        var settings = JsonSerializer.Deserialize<CoachSettings>(File.ReadAllText(ConfigPath), Wire.Json)
            ?? throw new IOException("配置内容为空。");
        // Encryption binds the key to this Windows account; no machine-wide or repository secrets.
        var key = settings.RememberKey && File.Exists(KeyPath)
            ? Encoding.UTF8.GetString(Protect(File.ReadAllBytes(KeyPath), false)) : "";
        return (settings, key);
    }

    public void Save(CoachSettings settings, string key)
    {
        settings.Validate();
        Directory.CreateDirectory(_directory);
        if (settings.RememberKey && key.Length > 0)
            AtomicWrite(KeyPath, Protect(Encoding.UTF8.GetBytes(key), true));
        else if (File.Exists(KeyPath)) File.Delete(KeyPath);
        AtomicWrite(ConfigPath, Encoding.UTF8.GetBytes(Wire.Serialize(settings)));
    }

    public void SaveLocalWorkers(int workers) => SaveLocalOptions(workers, null);

    public void SaveLocalOptions(int workers, bool? includePotions, bool? stopOnZeroLoss = null,
        int? targetVictoryRounds = null)
    {
        if (workers is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(workers));
        if (targetVictoryRounds is < 0 or > LocalCalculation.Rounds) throw new ArgumentOutOfRangeException(nameof(targetVictoryRounds));
        var settings = File.Exists(ConfigPath) ? JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject() : new JsonObject();
        settings["local_workers"] = workers;
        if (includePotions.HasValue) settings["local_include_potions"] = includePotions.Value;
        if (stopOnZeroLoss.HasValue) settings["local_stop_on_zero_loss"] = stopOnZeroLoss.Value;
        if (targetVictoryRounds.HasValue) settings["local_target_victory_rounds"] = targetVictoryRounds.Value;
        Directory.CreateDirectory(_directory);
        AtomicWrite(ConfigPath, Encoding.UTF8.GetBytes(settings.ToJsonString()));
        // Local-only preferences must not require AI credentials or rewrite the encrypted key.
    }

    private static void AtomicWrite(string path, byte[] content)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);

    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("记住密钥仅支持 Windows；可取消勾选后仅在本次游戏中使用。");
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, "SpireAiCoach", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
