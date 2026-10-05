namespace SpireAiCoach.Core;

// Entry choices precede the native combat recorder. Use the native history's
// localization identity, never a translated title or a guessed option index.
public sealed record LocalEventOption(string Table, string Key, bool Locked = false);
public sealed record LocalEventEntry(string EventId, LocalEventOption[] Choices)
{
    public int Resolve(string currentEventId, int step, IReadOnlyList<LocalEventOption> offered)
    {
        if (currentEventId != EventId || step < 0 || step >= Choices.Length)
            throw new CoachException("local_event_entry", "事件战斗入口记录与当前事件不一致。");
        var expected = Choices[step];
        var matches = offered.Select((option, index) => (option, index)).Where(x =>
            !x.option.Locked && x.option.Table == expected.Table && x.option.Key == expected.Key).ToArray();
        if (matches.Length != 1)
            throw new CoachException("local_event_entry", "无法唯一还原进入战斗的事件选项，已停止本次计算。");
        return matches[0].index;
    }
}
