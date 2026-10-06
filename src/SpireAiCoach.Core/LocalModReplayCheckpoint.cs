using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// Supplemental replay data for explicitly supported Mod fields. No native objects
// or RNG, and the initial values belong to the recorder's original run root.
public sealed record LocalRelicReplayField(ulong PlayerId, int RelicIndex, string ModelId,
    string TypeName, string ModuleId, string FieldName, int Value);

public sealed record LocalModReplayCheckpoint(LocalRelicReplayField[] Initial, LocalRelicReplayField[] Current)
{
    public static string Fingerprint(string nativeHash, LocalRelicReplayField[] fields) => fields.Length == 0
        ? nativeHash
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nativeHash + "\nmod-replay-v1\n" +
            JsonSerializer.Serialize(fields))));
}
