using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace SpireAiCoach.Mod;

// Diagnostic input only. The native recorder anonymizes player identities on disk.
// Restore an identity only when the entire serialized native starting run matches,
// including RNG, deck instances, relic fields and the initial synchronization counters.
internal static class LocalRecordedProbe
{
    public static (byte[] Replay, int PrefixEvents) Bind(byte[] recordedBytes, byte[] frozenBytes)
    {
        CombatReplay Read(byte[] bytes)
        {
            var reader = new PacketReader(); reader.Reset(bytes);
            return reader.Read<CombatReplay>();
        }
        byte[] Packet(IPacketSerializable item)
        {
            var writer = new PacketWriter(); item.Serialize(writer);
            return writer.Buffer.AsSpan(0, (writer.BitPosition + 7) / 8).ToArray();
        }
        var recorded = Read(recordedBytes); var frozen = Read(frozenBytes);
        if (recorded.serializableRun.Players.Count != 1 || frozen.serializableRun.Players.Count != 1 ||
            recorded.version != frozen.version || recorded.gitCommit != frozen.gitCommit || recorded.modelIdHash != frozen.modelIdHash ||
            recorded.nextActionId != frozen.nextActionId || recorded.nextHookId != frozen.nextHookId ||
            recorded.nextChecksumId != frozen.nextChecksumId || !recorded.choiceIds.SequenceEqual(frozen.choiceIds) ||
            !recorded.rewardIds.SequenceEqual(frozen.rewardIds))
            throw new InvalidOperationException("已记录路线不属于这个单人战斗起点。");
        var originalId = frozen.serializableRun.Players[0].NetId;
        var anonymousId = recorded.serializableRun.Players[0].NetId;
        var identities = (Dictionary<ulong, ulong>)typeof(IdAnonymizer)
            .GetField("_idToAnonymized", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var saved = identities.ToArray();
        try
        {
            identities[originalId] = anonymousId;
            if (!Packet(frozen.serializableRun.Anonymized()).SequenceEqual(Packet(recorded.serializableRun)))
                throw new InvalidOperationException("已记录路线的完整原生起点与冻结输入不同。");
        }
        finally
        {
            identities.Clear();
            foreach (var (key, value) in saved) identities.Add(key, value);
        }
        recorded.serializableRun = frozen.serializableRun;
        for (int i = 0; i < recorded.events.Count; i++)
        {
            var item = recorded.events[i];
            if (item.playerId is { } id)
            {
                if (id != anonymousId) throw new InvalidOperationException("已记录路线包含其他玩家的操作。");
                item.playerId = originalId;
                recorded.events[i] = item;
            }
        }
        if (frozen.events.Count > recorded.events.Count || frozen.events.Where((e, i) =>
            !Packet(e).SequenceEqual(Packet(recorded.events[i]))).Any())
            throw new InvalidOperationException("已记录路线不包含冻结输入的原生操作前缀。");
        return (Packet(recorded), frozen.events.Count);
    }
}
