using System.Text;

namespace SpireAiCoach.Core;

// An original native error can be logged without throwing. Keep that evidence
// separate from transport failures; never turn it into a usable candidate.
public sealed class LocalRuntimeLog
{
    private const int Limit = 64 * 1024;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _stack = new();
    private long _position;
    private string? _message;
    private bool _collecting;

    public LocalSimulationFailure? Read(Stream stream, int worker, string stage)
    {
        if (stream.Length < _position)
        {
            _position = 0; _decoder.Reset(); _line.Clear();
            _stack.Clear(); _message = null; _collecting = false;
        }
        stream.Position = _position;
        Span<byte> bytes = stackalloc byte[4096];
        Span<char> chars = stackalloc char[8192];
        int length;
        while ((length = stream.Read(bytes)) > 0)
        {
            _position += length;
            int count = _decoder.GetChars(bytes[..length], chars, flush: false);
            foreach (char character in chars[..count])
            {
                if (character == '\n')
                {
                    Line(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else if (_line.Length < Limit) _line.Append(character);
            }
        }
        string? message = _message;
        string stack = _stack.ToString();
        if (message == null && ErrorLine(_line.ToString()))
            message = stack = _line.ToString().TrimEnd('\r');
        return message == null ? null : new("native_runtime_log", message, null, stack, worker, stage, "local_runtime");
    }

    private static bool ErrorLine(string line) =>
        line.Contains("[ERROR]", StringComparison.Ordinal) ||
        line.StartsWith("System.", StringComparison.Ordinal) && line.Contains("Exception:", StringComparison.Ordinal) ||
        line.Contains("ERROR: FATAL:", StringComparison.Ordinal);

    private void Line(string line)
    {
        if (_message == null)
        {
            if (!ErrorLine(line)) return;
            _message = line; _collecting = true;
        }
        else if (!_collecting) return;
        else if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith("---", StringComparison.Ordinal))
        { _collecting = false; return; }

        int remaining = Limit - _stack.Length;
        if (remaining > 0)
        {
            if (_stack.Length > 0) { _stack.Append('\n'); remaining--; }
            _stack.Append(line.AsSpan(0, Math.Min(remaining, line.Length)));
        }
    }
}
