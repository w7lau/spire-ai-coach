using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireAiCoach.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}

public sealed record EffectInfo(string ModelId, string Source, string Name, string? Description,
    decimal? Amount, IReadOnlyDictionary<string, decimal>? Variables = null, bool? UsedUp = null, int? StackCount = null);

public sealed record CardInfo(string InstanceId, string ModelId, string Source, string Name,
    string? Description, int? EnergyCost, bool XCost, int Stars, string Type, string TargetType,
    int UpgradeLevel, bool? PlayableNow, IReadOnlyList<string> LegalTargetsNow,
    IReadOnlyList<string> Keywords, IReadOnlyDictionary<string, decimal> Variables,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>?> TargetPreviews);

public sealed record CardCount(string ModelId, string Name, int UpgradeLevel, int Count);
public sealed record PileInfo(int Count, IReadOnlyList<CardInfo> Cards)
{
    public IReadOnlyList<CardCount> CountsByCard => Cards.GroupBy(c => (c.ModelId, c.Name, c.UpgradeLevel))
        .Select(g => new CardCount(g.Key.ModelId, g.Key.Name, g.Key.UpgradeLevel, g.Count()))
        .OrderBy(c => c.ModelId, StringComparer.Ordinal).ThenBy(c => c.UpgradeLevel).ToArray();
}
public sealed record FutureMoveInfo(string MoveId, IReadOnlyList<string> IntentTypes);
public sealed record IntentInfo(string Type, string? Description, decimal? DamagePerHit, int? Hits);
public sealed record CreatureInfo(string InstanceId, string Name, int Hp, int MaxHp, int Block,
    IReadOnlyList<EffectInfo> Powers, IReadOnlyList<IntentInfo> Intents,
    string? CurrentMove, IReadOnlyList<string> FollowingMoveIds,
    IReadOnlyList<FutureMoveInfo>? FixedFollowingMoves = null);
public sealed record PotionInfo(string InstanceId, string Name, string? Description,
    string TargetType, bool UsableNow, IReadOnlyList<string> LegalTargetsNow);
public sealed record PlayerInfo(string InstanceId, string Character, int Hp, int MaxHp, int Block,
    int Energy, int MaxEnergy, int Stars, IReadOnlyList<EffectInfo> Powers,
    IReadOnlyList<EffectInfo> Relics, IReadOnlyList<PotionInfo> Potions,
    IReadOnlyList<string> Orbs, IReadOnlyList<CreatureInfo> Pets);

public sealed record CombatSnapshot(string CombatId, int Round, bool CanAdvise, string Phase,
    int Floor, int Ascension, PlayerInfo Player, PileInfo Hand, PileInfo DrawPile,
    PileInfo DiscardPile, PileInfo ExhaustPile, PileInfo PlayPile,
    IReadOnlyList<CreatureInfo> Enemies, IReadOnlyList<CreatureInfo> Allies,
    bool DrawOrderKnown, IReadOnlyList<string> DrawOrderTopFirst,
    IReadOnlyList<string> Limitations, long ObservationRevision = 0)
{
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Serialize(this))));
    [JsonIgnore]
    public IEnumerable<CardInfo> AllCards => Hand.Cards.Concat(DrawPile.Cards).Concat(DiscardPile.Cards)
        .Concat(ExhaustPile.Cards).Concat(PlayPile.Cards);
}
