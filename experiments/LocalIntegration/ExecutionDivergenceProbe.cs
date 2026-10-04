using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Installed only by the owned execution fixture, with private input and APPDATA.
// Observe the native draw arguments and gate without changing their results.
internal sealed class ExecutionDivergenceProbe : IDisposable
{
    private static readonly List<object> Events = [];
    private static string _phase = "restore";
    private static int _drawCommands, _drawGates;
    private readonly string _root;
    private readonly Harmony _patch = new("SpireLocalIntegration.owned-execution-draw-probe");
    private readonly object? _learner;
    private object? _pending;
    private LocalAction[] _actions = [];
    private int _step;

    public ExecutionDivergenceProbe(string root)
    {
        _root = root; Events.Clear(); _phase = "restore"; _drawCommands = _drawGates = 0;
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_LEARN") == "1")
            _learner = Activator.CreateInstance(typeof(ModEntry).Assembly.GetType("SpireAiCoach.Mod.LocalNativeLearning")!, [true, true]);
        _patch.Patch(AccessTools.Method(typeof(CardPileCmd), "DrawInternal"),
            prefix: new(AccessTools.Method(typeof(ExecutionDivergenceProbe), nameof(Draw))));
        _patch.Patch(AccessTools.Method(typeof(Hook), nameof(Hook.ShouldDraw)),
            postfix: new(AccessTools.Method(typeof(ExecutionDivergenceProbe), nameof(Gate))));
        var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(Hook), nameof(Hook.ShouldDraw)));
        Events.Add(new { kind = "draw-patches", prefixes = info?.Prefixes.Select(p => p.PatchMethod.DeclaringType!.FullName),
            postfixes = info?.Postfixes.Select(p => p.PatchMethod.DeclaringType!.FullName) });
    }

    public void BeforeAction(string message)
    {
        _phase = message;
        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
        if (_learner != null)
        {
            _learner.GetType().GetMethod("After")!.Invoke(_learner, [_pending, player]);
            var action = _actions[_step];
            _pending = action.EndTurn || action.PotionSlot.HasValue ? null :
                _learner.GetType().GetMethod("Before")!.Invoke(_learner, [player.PlayerCombatState!.Hand.Cards[action.HandIndex], player]);
        }
        _step++;
        Events.Add(new { phase = _phase, kind = "before-action", state = State(player) });
    }

    public void SetActions(LocalAction[] actions) => _actions = actions;

    private static object State(Player p) => new
    {
        round = p.Creature.CombatState?.RoundNumber, hp = p.Creature.CurrentHp,
        energy = p.PlayerCombatState?.Energy, stars = p.PlayerCombatState?.Stars,
        piles = p.PlayerCombatState!.AllPiles.Select(pile => new { type = pile.Type.ToString(),
            cards = pile.Cards.Select(c => new { id = c.Id.ToString(), instance = NetCombatCard.FromModel(c).CombatCardIndex,
                vars = c.DynamicVars.Select(v => new { name = v.Key, value = v.Value.BaseValue }).ToArray() }).ToArray() }).ToArray(),
        powers = p.Creature.Powers.Select(b => new { id = b.Id.ToString(), amount = b.Amount }).ToArray(),
        relics = p.Relics.Select(r => r.Id.ToString()).ToArray(),
    };

    private static void Draw(Player player, decimal count, bool fromHandDraw)
    {
        _drawCommands++;
        Events.Add(new { phase = _phase, kind = "draw", count, fromHandDraw, state = State(player) });
    }

    private static void Gate(bool __result, AbstractModel? modifier, bool fromHandDraw)
    {
        _drawGates++;
        Events.Add(new { phase = _phase, kind = "draw-gate", allowed = __result,
            modifier = modifier?.GetType().FullName, fromHandDraw });
    }

    public void Save(LocalExecutionReport report, Exception? failure)
    {
        LocalWire.Write(Path.Combine(_root, "integration-execution-draw-private.json"), Events);
        LocalWire.Write(Path.Combine(_root, "integration-execution-replay-private.json"), report);
        LocalWire.Write(Path.Combine(_root, "integration-execution-replay-summary.json"), new
        {
            report.Stage, report.SettledActions, report.PlannedActions, report.BattleEnded,
            report.ExpectedHp, report.ExpectedHash, report.ActualHash,
            learning_enabled = _learner != null, draw_commands = _drawCommands, draw_gate_calls = _drawGates,
            failure = failure?.Message, searches = 0,
        });
    }

    public void Dispose() => _patch.UnpatchAll(_patch.Id);
}
