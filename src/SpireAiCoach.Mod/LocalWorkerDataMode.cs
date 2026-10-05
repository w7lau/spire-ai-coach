using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Forms;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Installed only after LocalWorker verifies its isolated directory and executable.
// Model commands, synchronization, hooks, RNG and combat settlement are never replaced.
internal static class LocalWorkerDataMode
{
    public static bool Active { get; set; }
    public static bool Available { get; private set; }
    public static bool MinimalRun { get; set; }
    public static string[] VisualFactories => LocalWorkerOverhead.OptionalFactories;
    private static NOverlayStack? _emptyOverlays;
    private static readonly Dictionary<Node, Func<IEnumerable<CardModel>>> Selections = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<Node, Func<IEnumerable<IReadOnlyList<CardModel>>>> Bundles = new(ReferenceEqualityComparer.Instance);
    public static string[] SummonPresentationBoundaries { get; private set; } = [];
    public static void Install(Harmony _)
    {
        var harmony = new Harmony("SpireAiCoach.owned-worker.data");
        try { InstallBoundaries(harmony); Available = true; }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id); Available = false;
            GD.Print("[SpireAiCoach] Native presentation boundaries unavailable; using regular execution: " + ex.Message);
        }
    }
    private static void InstallBoundaries(Harmony harmony)
    {
        var missing = LocalSelectionCoverage.Audit();
        if (missing.Length > 0) throw new InvalidOperationException(string.Join("; ", missing));
        void Prefix(Type type, string name, string patch, Type[]? arguments = null) => harmony.Patch(
            arguments == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, arguments),
            prefix: new(AccessTools.Method(typeof(LocalWorkerDataMode), patch)));
        Prefix(typeof(NCombatRoom), nameof(NCombatRoom.Create), nameof(CombatScene));
        Prefix(typeof(PreloadManager), "LoadRoomAssets", nameof(RoomAssets));
        Prefix(typeof(PreloadManager), nameof(PreloadManager.LoadRunAssets), nameof(RunAssets));
        Prefix(typeof(PreloadManager), nameof(PreloadManager.LoadActAssets), nameof(RunAssets));
        Prefix(typeof(RunManager), "ClearScreens", nameof(RunPresentation));
        Prefix(typeof(RunManager), "FadeIn", nameof(PresentationTask));
        Prefix(typeof(RunManager), "FadeOut", nameof(PresentationTask));
        Prefix(typeof(CardPileCmd), "CreateCardNodeAndUpdateVisuals", nameof(CardNode));
        Prefix(typeof(CardPileCmd), nameof(CardPileCmd.GetTweenForCardsChangingPiles), nameof(PilePresentation),
            [typeof(IEnumerable<CardPileAddResult>), typeof(bool)]);
        Prefix(typeof(CardModel), "PlayPowerCardFlyVfx", nameof(PresentationTask));
        Prefix(typeof(NGame), nameof(NGame.ScreenShake), nameof(PresentationVoid));
        foreach (var factory in typeof(NDamageNumVfx).GetMethods().Where(m => m.Name == nameof(NDamageNumVfx.Create)))
            harmony.Patch(factory, prefix: new(AccessTools.Method(typeof(LocalWorkerDataMode), nameof(DamageVisual))));
        Prefix(typeof(PlayerHurtVignetteHelper), nameof(PlayerHurtVignetteHelper.Play), nameof(PresentationVoid));
        // Form factories are guarded once by LocalWorkerOverhead. Other VFX
        // factories can own pile/selection completion callbacks and stay native.
        // The native death callback removes its subscription before obtaining
        // an optional animation node. Keep that cleanup and every death hook;
        // only guard the missing presentation receiver in scene-free workers.
        harmony.Patch(AccessTools.Method(typeof(SoulNexus), "AfterDeath", [typeof(Creature)]),
            transpiler: new(AccessTools.Method(typeof(LocalWorkerDataMode), nameof(DeathCreatureVisual))));
        InstallSummonPresentation(harmony);
        LocalEnemyPresentation.Install(harmony);
        Prefix(typeof(CardCmd), "PreviewInternal", nameof(Preview));
        Prefix(typeof(ForgeCmd), "PreviewSovereignBlade", nameof(PresentationVoid));
        var transform = typeof(CardCmd).GetMethods().Single(m => m.Name == nameof(CardCmd.Transform) &&
            m.GetParameters()[0].ParameterType.IsGenericType &&
            m.GetParameters()[0].ParameterType.GetGenericArguments()[0].Name == "CardTransformation");
        harmony.Patch(AccessTools.Method(transform.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext"),
            transpiler: new(AccessTools.Method(typeof(LocalWorkerDataMode), nameof(TransformPresentation))));
        Prefix(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen.ShowScreen), nameof(Offer));
        Prefix(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen.CardsSelected), nameof(Selected));
        Prefix(typeof(NChooseABundleSelectionScreen), nameof(NChooseABundleSelectionScreen.ShowScreen), nameof(Bundle));
        Prefix(typeof(NChooseABundleSelectionScreen), nameof(NChooseABundleSelectionScreen.CardsSelected), nameof(SelectedBundle));
        Prefix(typeof(NDeckCardSelectScreen), nameof(NDeckCardSelectScreen.Create), nameof(Deck));
        Prefix(typeof(NDeckUpgradeSelectScreen), nameof(NDeckUpgradeSelectScreen.ShowScreen), nameof(DeckUpgrade));
        Prefix(typeof(NDeckTransformSelectScreen), nameof(NDeckTransformSelectScreen.ShowScreen), nameof(DeckTransform));
        Prefix(typeof(NDeckEnchantSelectScreen), nameof(NDeckEnchantSelectScreen.ShowScreen), nameof(DeckEnchant));
        Prefix(typeof(NSimpleCardSelectScreen), nameof(NSimpleCardSelectScreen.Create), nameof(Grid),
            [typeof(IReadOnlyList<CardModel>), typeof(CardSelectorPrefs)]);
        Prefix(typeof(NSimpleCardSelectScreen), nameof(NSimpleCardSelectScreen.Create), nameof(CreatedGrid),
            [typeof(IReadOnlyList<CardCreationResult>), typeof(CardSelectorPrefs)]);
        Prefix(typeof(NCombatPileCardSelectScreen), nameof(NCombatPileCardSelectScreen.Create), nameof(Pile));
        Prefix(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected), nameof(Selected));
        Prefix(typeof(NOverlayStack), nameof(NOverlayStack.Push), nameof(Push));
        Prefix(typeof(NOverlayStack), "get_Instance", nameof(OverlayReceiver));
        var tracker = AccessTools.Method(typeof(CombatStateTracker), "CallCombatStateChangedDeferred");
        harmony.Patch(AccessTools.Method(tracker.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext"),
            transpiler: new(AccessTools.Method(typeof(LocalWorkerDataMode), nameof(TrackerPresentationOwner))));
        var kill = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Kill), [typeof(IReadOnlyCollection<Creature>), typeof(bool)]);
        harmony.Patch(AccessTools.Method(kill.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext"),
            transpiler: new(AccessTools.Method(typeof(LocalWorkerDataMode), nameof(DeathPresentation))));
        foreach (var name in new[] { nameof(CardSelectCmd.FromHand), nameof(CardSelectCmd.FromHandForUpgrade) })
        {
            var machine = AccessTools.Method(typeof(CardSelectCmd), name).GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
            harmony.Patch(AccessTools.Method(machine, "MoveNext"),
                transpiler: new(AccessTools.Method(typeof(LocalWorkerDataMode), nameof(HandPresentation))));
        }
    }
    public static void FreeSelections()
    {
        foreach (var node in Selections.Keys.Concat(Bundles.Keys)) if (GodotObject.IsInstanceValid(node)) node.Free();
        Selections.Clear(); Bundles.Clear();
    }
    private static void InstallSummonPresentation(Harmony harmony)
    {
        var enabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
        // This native helper changes only a scene node's fall position. Match
        // its complete optional room/node lookup block, independent of monster
        // names. Base initialization and subsequent effects remain original.
        var calls = SummonVisualCalls();
        var boundaries = new List<string>();
        foreach (var type in typeof(MonsterModel).Assembly.GetTypes().Where(t => typeof(MonsterModel).IsAssignableFrom(t)))
        {
            var callback = type.GetMethod(nameof(MonsterModel.AfterAddedToRoom), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var machine = callback?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            var method = machine == null ? callback : AccessTools.Method(machine, "MoveNext");
            if (method == null || !LocalPresentationGuard.OptionalGuardedCalls(method, enabled, calls)) continue;
            harmony.Patch(method, transpiler: new(AccessTools.Method(typeof(LocalWorkerDataMode), nameof(SummonPresentation))));
            boundaries.Add(type.FullName!);
        }
        SummonPresentationBoundaries = boundaries.ToArray();
        if (boundaries.Count == 0) throw new InvalidOperationException("Native optional summon presentation block changed");
    }
    private static MethodInfo[] SummonVisualCalls() => [AccessTools.PropertyGetter(typeof(NCombatRoom), nameof(NCombatRoom.Instance)),
        AccessTools.PropertyGetter(typeof(MonsterModel), nameof(MonsterModel.Creature)),
        AccessTools.Method(typeof(NCombatRoom), nameof(NCombatRoom.GetCreatureNode), [typeof(Creature)]),
        AccessTools.Method(typeof(FabricatorNormal), nameof(FabricatorNormal.SetBotFallPosition))];
    private static bool SummonPresentationEnabled()
    {
        if (Active) LocalWorker.SkipMethod("Summon.OptionalNodeInitialization");
        return !Active && TestMode.IsOff;
    }
    private static IEnumerable<CodeInstruction> SummonPresentation(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToArray();
        int Offset(System.Reflection.Emit.Label label)
        {
            int target = Array.FindIndex(code, c => c.labels.Contains(label));
            if (target < 0) throw new InvalidOperationException("Unresolved native summon branch");
            while (target < code.Length && code[target].opcode == OpCodes.Nop) target++;
            return target;
        }
        object? Operand(object? operand) => operand switch
        {
            System.Reflection.Emit.Label label => Offset(label),
            System.Reflection.Emit.Label[] labels => labels.Select(Offset).ToArray(),
            LocalBuilder local => local.LocalIndex,
            _ => operand
        };
        var enabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
        // Validate the actual Harmony input too. If another Mod inserts rules
        // into this display block, retain compatibility rather than skipping them.
        if (!LocalPresentationGuard.OptionalGuardedCalls(code.Select((c, i) => new LocalInstruction(i, c.opcode, Operand(c.operand))), enabled, SummonVisualCalls()))
            throw new InvalidOperationException("Modified native summon block contains more than optional presentation");
        int count = 0;
        foreach (var instruction in code)
        {
            if (instruction.Calls(enabled))
            {
                count++;
                yield return new CodeInstruction(instruction) { operand = AccessTools.Method(typeof(LocalWorkerDataMode), nameof(SummonPresentationEnabled)) };
            }
            else yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Native optional summon presentation gate changed");
    }
    public static void Reset()
    {
        Active = MinimalRun = false; FreeSelections();
        if (_emptyOverlays != null && GodotObject.IsInstanceValid(_emptyOverlays)) _emptyOverlays.Free();
        _emptyOverlays = null;
    }
    private static bool CombatScene(ref NCombatRoom? __result)
    { if (!Active) return true; __result = null; return false; }
    private static bool RoomAssets(ref Task __result)
    { if (!Active) return true; __result = Task.CompletedTask; return false; }
    private static bool RunAssets(ref Task __result)
    { if (!MinimalRun) return true; __result = Task.CompletedTask; return false; }
    private static bool RunPresentation() => !MinimalRun;
    private static bool PresentationTask(ref Task __result)
    { if (!Active) return true; __result = Task.CompletedTask; return false; }
    private static bool CardNode(ref NCard? __result)
    { if (!Active) return true; __result = null; return false; }
    private static bool PresentationVoid() => !Active;
    private static bool DamageVisual(ref NDamageNumVfx? __result)
    { if (!Active) return true; __result = null; return false; }
    private static NCreature? FindCreatureVisual(NCombatRoom? room, Creature creature) =>
        Active && (room == null || !GodotObject.IsInstanceValid(room)) ? null : room!.GetCreatureNode(creature);
    private static IEnumerable<CodeInstruction> DeathCreatureVisual(IEnumerable<CodeInstruction> instructions)
    {
        var lookup = AccessTools.Method(typeof(NCombatRoom), nameof(NCombatRoom.GetCreatureNode), [typeof(Creature)]);
        int calls = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(lookup))
            {
                calls++;
                yield return new CodeInstruction(instruction)
                {
                    opcode = OpCodes.Call,
                    operand = AccessTools.Method(typeof(LocalWorkerDataMode), nameof(FindCreatureVisual))
                };
            }
            else yield return instruction;
        }
        if (calls != 1) throw new InvalidOperationException("Native death animation lookup boundary changed");
    }
    private static bool Preview(CardModel card, bool isAddingCardsToPile, ref TaskCompletionSource? __result)
    {
        if (!Active) return true;
        if (isAddingCardsToPile && !CombatManager.Instance.IsEnding && LocalContext.IsMine(card))
            card.Pile?.InvokeCardAddFinished();
        __result = null;
        return false;
    }
    private static bool PilePresentation(IEnumerable<CardPileAddResult> results, bool fromSilentAdd, ref (Tween?, bool) __result)
    {
        if (!Active) return true;
        foreach (var result in results)
        {
            if (!result.success) continue;
            // Native silent movement defers these notifications to the presentation
            // boundary. Keep them even though there is no tween/card node to finish.
            if (fromSilentAdd)
            {
                result.oldPile?.InvokeCardRemoved(result.cardAdded);
                result.oldPile?.InvokeCardRemoveFinished();
                result.oldPile?.InvokeContentsChanged();
            }
            var card = result.cardAdded;
            if (card.Pile?.Type is PileType.Draw or PileType.Discard or PileType.Exhaust or PileType.Deck &&
                (LocalContext.IsMe(card.Owner) || result.oldPile?.Type == PileType.Play))
                card.Pile?.InvokeCardAddFinished();
        }
        __result = (null, false);
        return false;
    }
    private static bool Offer(IReadOnlyList<CardModel> cards, bool canSkip, ref NChooseACardSelectionScreen __result)
    {
        if (!Active) return true;
        __result = new NChooseACardSelectionScreen();
        Selections.Add(__result, () => LocalWorker.CurrentChoices.OfferWithoutPresentation(cards, canSkip));
        return false;
    }
    private static CardModel[] Ordered(IEnumerable<CardModel> cards, CardSelectorPrefs prefs)
    {
        var ordered = cards.ToList();
        if (prefs.Comparison != null) ordered.Sort(prefs.Comparison);
        return ordered.ToArray();
    }
    private static bool Grid(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, ref NSimpleCardSelectScreen __result)
    {
        if (!Active) return true;
        var ordered = Ordered(cards, prefs);
        __result = new NSimpleCardSelectScreen();
        Selections.Add(__result, () => LocalWorker.CurrentChoices.SelectWithoutPresentation("grid", ordered, prefs));
        return false;
    }
    private static bool CreatedGrid(IReadOnlyList<CardCreationResult> cards, CardSelectorPrefs prefs, ref NSimpleCardSelectScreen __result) =>
        Grid(cards.Select(c => c.Card).ToArray(), prefs, ref __result);
    private static T DeckSelection<T>(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, string kind,
        Func<CardModel, CardTransformation>? preview = null) where T : NCardGridSelectionScreen, new()
    {
        var screen = new T();
        Selections.Add(screen, () =>
        {
            var selected = LocalWorker.CurrentChoices.SelectWithoutPresentation(kind, cards.ToArray(), prefs).ToArray();
            if (preview != null && prefs.RequireManualConfirmation)
                foreach (var card in selected) _ = preview(card);
            return selected;
        });
        return screen;
    }
    private static bool Deck(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, ref NDeckCardSelectScreen __result)
    { if (!Active) return true; __result = DeckSelection<NDeckCardSelectScreen>(cards, prefs, "deck"); return false; }
    private static bool DeckUpgrade(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, ref NDeckUpgradeSelectScreen __result)
    { if (!Active) return true; __result = DeckSelection<NDeckUpgradeSelectScreen>(cards, prefs, "deck-upgrade"); return false; }
    private static bool DeckTransform(IReadOnlyList<CardModel> cards, Func<CardModel, CardTransformation> cardToTransformation,
        CardSelectorPrefs prefs, ref NDeckTransformSelectScreen __result)
    { if (!Active) return true; __result = DeckSelection<NDeckTransformSelectScreen>(cards, prefs, "deck-transform", cardToTransformation); return false; }
    private static bool DeckEnchant(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, ref NDeckEnchantSelectScreen __result)
    { if (!Active) return true; __result = DeckSelection<NDeckEnchantSelectScreen>(cards, prefs, "deck-enchant"); return false; }
    private static bool Bundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles, ref NChooseABundleSelectionScreen __result)
    {
        if (!Active) return true;
        __result = new NChooseABundleSelectionScreen();
        Bundles.Add(__result, () => LocalWorker.CurrentChoices.BundleWithoutPresentation(bundles));
        return false;
    }
    private static bool SelectedBundle(Node __instance, ref Task<IEnumerable<IReadOnlyList<CardModel>>> __result)
    {
        if (!Bundles.TryGetValue(__instance, out var select)) return true;
        __result = Task.FromResult(select()); return false;
    }
    private static bool Pile(CardPile pile, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, ref NCombatPileCardSelectScreen __result)
    {
        if (!Active) return true;
        __result = new NCombatPileCardSelectScreen();
        Selections.Add(__result, () => LocalWorker.CurrentChoices.SelectWithoutPresentation("pile",
            pile.Cards.Where(filter ?? (_ => true)).ToArray(), prefs, true));
        return false;
    }
    private static bool Push(IOverlayScreen screen) => screen is not Node node || !Selections.ContainsKey(node) && !Bundles.ContainsKey(node);
    private static bool OverlayReceiver(ref NOverlayStack? __result)
    {
        if (!MinimalRun || NRun.Instance != null) return true;
        __result = _emptyOverlays ??= new NOverlayStack();
        return false;
    }
    private static bool Selected(Node __instance, ref Task<IEnumerable<CardModel>> __result)
    {
        if (!Selections.TryGetValue(__instance, out var select)) return true;
        __result = Task.FromResult(select());
        return false;
    }
    // Replace only the UI receiver/call in the original async commands. The enclosing
    // commands still signal and record the same native player choices.
    private static IEnumerable<CodeInstruction> HandPresentation(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var room = AccessTools.PropertyGetter(typeof(NCombatRoom), nameof(NCombatRoom.Instance));
        var ui = AccessTools.PropertyGetter(typeof(NCombatRoom), nameof(NCombatRoom.Ui));
        var hand = AccessTools.PropertyGetter(typeof(NCombatUi), nameof(NCombatUi.Hand));
        var select = AccessTools.Method(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards));
        int receivers = 0, calls = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (i + 2 < code.Count && code[i].Calls(room) && code[i + 1].Calls(ui) && code[i + 2].Calls(hand))
            {
                var replacement = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocalWorkerDataMode), nameof(HandReceiver)));
                replacement.labels.AddRange(code[i].labels); replacement.blocks.AddRange(code[i].blocks);
                if (code[i + 1].labels.Count > 0 || code[i + 2].labels.Count > 0 || code[i + 1].blocks.Count > 0 || code[i + 2].blocks.Count > 0)
                    throw new InvalidOperationException("Native selection presentation boundary changed");
                yield return replacement; i += 2; receivers++;
            }
            else if (code[i].Calls(select))
            {
                var replacement = new CodeInstruction(code[i]) { opcode = OpCodes.Call,
                    operand = AccessTools.Method(typeof(LocalWorkerDataMode), nameof(SelectHand)) };
                yield return replacement; calls++;
            }
            else yield return code[i];
        }
        if (receivers != 1 || calls != 1) throw new InvalidOperationException("Native hand choice boundary changed");
    }
    private static NPlayerHand? HandReceiver() => Active ? null : NCombatRoom.Instance!.Ui.Hand;
    private static Node? TrackerOwner() => LocalWorkerLogic.Active ? null : MinimalRun ? NGame.Instance : NRun.Instance;
    private static void StopRunMusic() { if (!MinimalRun) NRun.Instance!.RunMusicController.StopMusic(); }
    private static void ShowGameOver(NRun? run, SerializableRun state) { if (!MinimalRun) run!.ShowGameOverScreen(state); }
    private static IEnumerable<CodeInstruction> DeathPresentation(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var run = AccessTools.PropertyGetter(typeof(NRun), nameof(NRun.Instance));
        var music = AccessTools.PropertyGetter(typeof(NRun), nameof(NRun.RunMusicController));
        var stop = AccessTools.Method(music.ReturnType, "StopMusic");
        var screen = AccessTools.Method(typeof(NRun), nameof(NRun.ShowGameOverScreen));
        int musicCalls = 0, screenCalls = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (i + 2 < code.Count && code[i].Calls(run) && code[i + 1].Calls(music) && code[i + 2].Calls(stop))
            {
                var replacement = new CodeInstruction(code[i]) { operand = AccessTools.Method(typeof(LocalWorkerDataMode), nameof(StopRunMusic)) };
                if (code[i + 1].labels.Count > 0 || code[i + 2].labels.Count > 0 || code[i + 1].blocks.Count > 0 || code[i + 2].blocks.Count > 0)
                    throw new InvalidOperationException("Native death presentation boundary changed");
                yield return replacement; i += 2; musicCalls++;
            }
            else if (code[i].Calls(screen))
            {
                yield return new CodeInstruction(code[i]) { opcode = OpCodes.Call,
                    operand = AccessTools.Method(typeof(LocalWorkerDataMode), nameof(ShowGameOver)) };
                screenCalls++;
            }
            else yield return code[i];
        }
        // OnEnded, death prevention, death hooks, pending loss and CombatEnded remain native.
        if (musicCalls != 1 || screenCalls != 1) throw new InvalidOperationException("Native death presentation boundary changed");
    }
    private static IEnumerable<CodeInstruction> TrackerPresentationOwner(IEnumerable<CodeInstruction> instructions)
    {
        var owner = AccessTools.PropertyGetter(typeof(NRun), nameof(NRun.Instance));
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(owner))
            {
                count++;
                yield return new CodeInstruction(instruction) { operand = AccessTools.Method(typeof(LocalWorkerDataMode), nameof(TrackerOwner)) };
            }
            else yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Native state notification boundary changed");
    }
    private static bool PresentationIsOn() => Active || TestMode.IsOn;
    private static bool PresentationIsOff() => !Active && TestMode.IsOff;
    private static IEnumerable<CodeInstruction> TransformPresentation(IEnumerable<CodeInstruction> instructions)
    {
        var on = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOn));
        var off = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(on) || instruction.Calls(off))
            {
                count++;
                yield return new CodeInstruction(instruction) { operand = AccessTools.Method(typeof(LocalWorkerDataMode),
                    instruction.Calls(on) ? nameof(PresentationIsOn) : nameof(PresentationIsOff)) };
            }
            else yield return instruction;
        }
        if (count != 2) throw new InvalidOperationException("Native transform presentation boundary changed");
    }
    private static Task<IEnumerable<CardModel>> SelectHand(NPlayerHand? hand, CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter, AbstractModel? source, NPlayerHand.Mode mode)
    {
        if (!Active) return hand!.SelectCards(prefs, filter, source, mode);
        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
        return Task.FromResult(LocalWorker.CurrentChoices.SelectWithoutPresentation("hand",
            player.PlayerCombatState!.Hand.Cards.Where(filter ?? (_ => true)).ToArray(), prefs));
    }
}
