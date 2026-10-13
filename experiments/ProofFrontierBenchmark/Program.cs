
using System.Diagnostics;
using System.Text.Json;
using SpireAiCoach.Core;
var output = new List<object>();
foreach (int depth in new[] { 400, 600 })
{
    var request = new LocalSearchRequest("bench", "battle", [], "root", 1, ["sts2:fixture"], false);
    LocalAction Move(int i, string hash) => new(i, "card-" + i, null, "card", "", hash, 1, CombatCardIndex: (uint)i);
    LocalHealthEnvelope Bound(int hp) => new("battle:root", 50, hp, 0, 0);
    var roots = new[] { Move(1, "root"), Move(2, "root") };
    var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
    var proof = new LocalMinimumLossProof(request);
    proof.Observe(new(50, [new(roots[0], roots, [], Bound(38), BeforeHint: hint)], 38, Won: true));
    var steps = new List<LocalLossProofStep> { new(roots[1], roots, [], Bound(50), BeforeHint: hint) };
    for (int i = 1; i <= depth; i++)
    {
        var action = Move(i + 10, "state-" + i);
        var legal = i == depth ? new[] { action, Move(999, action.BeforeHash) } : [action];
        steps.Add(new(action, legal, [], Bound(50), BeforeHint: hint));
    }
    var trial = new LocalLossProofTrial(50, steps.ToArray(), 50);
    double milliseconds = 0; long bytes = 0; int focus = 0;
    for (int i = 0; i < 120; i++)
    {
        proof.Observe(trial);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var status = proof.Status;
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long used = GC.GetAllocatedBytesForCurrentThread() - allocated;
        focus = status.Focus!.Length;
        if (i >= 20) { milliseconds += elapsed; bytes += used; }
        if (status.Certificate?.MinimumNetHpLoss != 0) throw new Exception("Unclosed tree acquired a positive proof");
    }
    output.Add(new { depth, calls = 100, focus, milliseconds, allocatedBytes = bytes });
}
Console.WriteLine(JsonSerializer.Serialize(output));
