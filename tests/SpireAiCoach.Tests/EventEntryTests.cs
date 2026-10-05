using System.Text.Json;
using SpireAiCoach.Core;

internal static class EventEntryTests
{
    public static void Register(Action<string, Action> test)
    {
        static void Check(bool ok) { if (!ok) throw new Exception("Event entry identity failed"); }
        static void Reject(Action action) { try { action(); } catch (CoachException ex) when (ex.Category == "local_event_entry") { return; } throw new Exception("Unproven event option was accepted"); }
        var first = new LocalEventOption("events", "entry.first.title");
        var second = new LocalEventOption("events", "entry.fight.title");
        var entry = new LocalEventEntry("EVENT.ENTRY", [first, second]);
        test("event entry follows exact native history across option pages and reordered offers", () => {
            Check(entry.Resolve("EVENT.ENTRY", 0, [second, first]) == 1);
            Check(entry.Resolve("EVENT.ENTRY", 1, [second]) == 0);
        });
        test("event entry rejects duplicate localization identities and locked options", () => {
            Reject(() => entry.Resolve("EVENT.ENTRY", 0, [first, first]));
            Reject(() => entry.Resolve("EVENT.ENTRY", 0, [first with { Locked = true }]));
        });
        test("event entry rejects a different event missing option or invalid history position", () => {
            Reject(() => entry.Resolve("EVENT.OTHER", 0, [first]));
            Reject(() => entry.Resolve("EVENT.ENTRY", 0, [second]));
            Reject(() => entry.Resolve("EVENT.ENTRY", 2, [first]));
        });
        test("event entry survives request IPC and legacy requests retain no fabricated entry", () => {
            var request = new LocalSearchRequest("r", "s", [], "h", 0, [], false, EventEntry: entry);
            var restored = JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(request))!;
            Check(restored.EventEntry!.Resolve("EVENT.ENTRY", 1, [second]) == 0);
            Check(JsonSerializer.Deserialize<LocalSearchRequest>("{\"Id\":\"old\"}")!.EventEntry == null);
        });
        test("event entry failure preserves a structured cause category rather than classifying translated text", () => {
            var failure = LocalSimulationFailure.Capture(new AggregateException(new CoachException("local_event_entry", "localized message")));
            Check(failure.Category == "local_event_entry");
            Check(LocalSimulationFailure.Capture(new InvalidOperationException("事件" )).Category == null);
        });
    }
}
