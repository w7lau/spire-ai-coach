using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Uses the native screen's selection handler: native choice IDs, replay and resume hooks remain intact.
// Created only around an owned simulation or an explicitly requested live plan action.
public sealed class LocalChoices
{
    private readonly LocalCardChoice[]? _expected;
    private readonly Func<LocalCardChoice[], LocalCardChoice>? _choose;
    private readonly Queue<int>? _history;
    private readonly List<LocalCardChoice> _completed = [];
    private readonly HashSet<ulong> _handled = [];
    public LocalCardChoice[] Completed => _completed.ToArray();
    public LocalChoices(LocalCardChoice[]? expected = null, Func<LocalCardChoice[], LocalCardChoice>? choose = null,
        IEnumerable<int>? history = null)
    { _expected = expected ?? []; _choose = choose; _history = history == null ? null : new(history); }

    private static T Field<T>(object owner, string name) =>
        typeof(NChooseACardSelectionScreen).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) is T value
            ? value : throw new InvalidOperationException("游戏选牌接口已变化：" + name);

    public void Tick(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var top = NOverlayStack.Instance?.Peek();
        if (top is not NChooseACardSelectionScreen screen || top.GetType() != typeof(NChooseACardSelectionScreen)) return;
        if (!screen.IsNodeReady() || _handled.Contains(screen.GetInstanceId()) || Field<bool>(screen, "_screenComplete")) return;
        // Respect native input readiness, including during live playback.
        if (Godot.Time.GetTicksMsec() - Field<ulong>(screen, "_openedTicks") <= 350) return;
        var cards = Field<IReadOnlyList<CardModel>>(screen, "_cards");
        var canSkip = Field<bool>(screen, "_canSkip");
        var writer = new PacketWriter();
        writer.WriteBool(canSkip);
        writer.WriteInt(cards.Count);
        foreach (var card in cards) card.ToSerializable().Serialize(writer);
        var identity = Convert.ToHexString(writer.Buffer.AsSpan(0, (writer.BitPosition + 7) / 8));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LocalCapture.Fingerprint() + identity)));
        var options = cards.Select((c, i) => new LocalCardChoice(hash, i, c.Id.ToString(), c.Title)).ToList();
        if (canSkip) options.Add(new(hash, -1, "", "跳过"));
        LocalCardChoice selected;
        if (_history != null)
        {
            if (!_history.TryDequeue(out var index)) throw new InvalidOperationException("重放选牌记录缺失。");
            selected = options.SingleOrDefault(c => c.Index == index) ?? throw new InvalidOperationException("重放选牌索引已变化。");
        }
        else if (_choose != null) selected = _choose(options.ToArray());
        else
        {
            if (_completed.Count >= _expected!.Length) throw new InvalidOperationException("出现了方案之外的选牌，已停止执行。");
            var expected = _expected[_completed.Count];
            selected = options.SingleOrDefault(c => c.Index == expected.Index && c.ModelId == expected.ModelId && c.OfferHash == expected.OfferHash)
                ?? throw new InvalidOperationException("选牌内容与模拟不一致，已停止执行。");
        }
        if (!options.Contains(selected)) throw new InvalidOperationException("无效的选牌分支。");
        token.ThrowIfCancellationRequested();
        _handled.Add(screen.GetInstanceId());
        if (selected.Index < 0) screen.Call("OnSkipButtonReleased", default(Variant));
        else
        {
            var row = Field<Control>(screen, "_cardRow");
            var holder = row.GetChildren().OfType<NCardHolder>().Single(h => ReferenceEquals(h.CardModel, cards[selected.Index]));
            screen.Call("SelectHolder", holder);
        }
        _completed.Add(selected);
    }

    public void Finish()
    {
        if (_history is { Count: > 0 } || _history == null && _choose == null && _completed.Count != _expected!.Length)
            throw new InvalidOperationException("选牌次数与记录不一致，已停止执行。");
    }

    public static string PendingDescription() => NOverlayStack.Instance?.Peek() is { } screen ? screen.GetType().Name : "无选牌页面";
}
