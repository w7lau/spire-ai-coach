using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

public static class LocalTimingArchive
{
    // One immutable compressed record per run, plus the existing readable latest
    // file. Serialize once; no provider settings, credentials or raw AI bodies.
    public static string Write(string directory, string requestId, object record)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Invalid timing request identity", nameof(requestId));
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(record);
        string path = Path.Combine(directory, $"local-timing-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{requestId}.json.gz");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false))) writer.Write(json);
        LocalWire.WriteJson(Path.Combine(directory, "local-timing-latest.json"), json);
        return path;
    }
}
