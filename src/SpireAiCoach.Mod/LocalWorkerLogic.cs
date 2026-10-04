using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Runs;

namespace SpireAiCoach.Mod;

// Installed only in an owned worker. Native effects, hooks, choices, action events
// and victory checks stay native; their continuations use a serial data loop.
internal static class LocalWorkerLogic
{
    private static readonly MethodInfo StateNotification = AccessTools.Method(typeof(CombatStateTracker), "CallCombatStateChangedDeferred");
    private static readonly ConditionalWeakTable<ActionExecutor, Callbacks> ExecutorCallbacks = new();
    private static Runtime? _current;
    private static bool _dispatchingNotification;
    public static bool Available { get; private set; }
    public static bool Enabled { get; set; }
    public static bool Active => Enabled && Available && LocalWorkerDataMode.MinimalRun && _current != null;
    public static long Operations { get; private set; }
    public static long Continuations { get; private set; }
    public static long Notifications { get; private set; }
    public static long DirectActions { get; private set; }
    public static long RealFrames { get; private set; }
    private static long _revision;
    public static long Revision => Interlocked.Read(ref _revision);

    public static void Install()
    {
        var harmony = new Harmony("SpireAiCoach.owned-worker.logic");
        try
        {
            var executor = AccessTools.Method(typeof(ActionExecutor), "ExecuteActions");
            harmony.Patch(AccessTools.Method(executor.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext"),
                transpiler: new(AccessTools.Method(typeof(LocalWorkerLogic), nameof(DirectExecution))));
            harmony.Patch(StateNotification, prefix: new(AccessTools.Method(typeof(LocalWorkerLogic), nameof(DeferNotification))));
            Available = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id); Available = false;
            Godot.GD.Print("[SpireAiCoach] Direct rule execution unavailable: " + ex.Message);
        }
    }

    public static void ResetCounters() => Operations = Continuations = Notifications = DirectActions = RealFrames = 0;
    public static object Counters() => new { available = Available, enabled = Enabled, Operations, Continuations, Notifications, DirectActions, RealFrames };
    public static Task Run(Func<Task> operation, Action tick, Func<Task> frame, int timeoutSeconds = 8) =>
        !Enabled || !Available || !LocalWorkerDataMode.MinimalRun || _current != null
            ? operation() : Pump(operation, tick, frame, timeoutSeconds);

    private static async Task Pump(Func<Task> operation, Action tick, Func<Task> frame, int timeoutSeconds)
    {
        var previous = SynchronizationContext.Current;
        var runtime = new Runtime(previous);
        _current = runtime;
        Operations++;
        var timer = Stopwatch.StartNew();
        try
        {
            SynchronizationContext.SetSynchronizationContext(runtime);
            var pending = operation();
            while (true)
            {
                tick();
                runtime.Poll();
                if (timer.Elapsed.TotalSeconds > timeoutSeconds)
                    throw new InvalidOperationException("后台规则执行超时，停止这组计算。");
                if (runtime.TryExecute()) { Continuations++; continue; }
                if (pending.IsCompleted) { await pending; return; }
                // Preserve unknown Godot/external dependencies and count them. Only
                // empty data queues require a real frame; queued rules never do.
                RealFrames++;
                using var waiting = LocalWorker.TraceLogicFrame();
                SynchronizationContext.SetSynchronizationContext(previous);
                await frame();
                SynchronizationContext.SetSynchronizationContext(runtime);
            }
        }
        finally
        {
            runtime.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
            _current = null;
        }
    }

    public static Task Until(Func<bool> condition)
    {
        if (!Active) throw new InvalidOperationException("No owned numerical execution context");
        return _current!.Until(condition);
    }

    private sealed class Runtime(SynchronizationContext? parent) : SynchronizationContext
    {
        private readonly object _postGate = new();
        private bool _closed;
        private CombatState? _closedState;
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _ready = new();
        private readonly List<(Func<bool> Condition, TaskCompletionSource Completion)> _waiting = [];
        public override SynchronizationContext CreateCopy() => this;
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_postGate)
            {
                if (!_closed) { _ready.Enqueue((d, state)); return; }
            }
            // The native turn loop lives across many player actions. Its captured
            // context may already be closed; route it into the current data loop
            // only for the exact same combat object. Old/cancelled combats go home.
            if (_current is { } current && current != this && Active && _closedState != null &&
                ReferenceEquals(_closedState, CombatManager.Instance.DebugOnlyGetState()))
            { current.Post(d, state); return; }
            // Native tasks may finish after this action (for example when CleanUp
            // cancels a previous run). Do not strand them in an abandoned data queue.
            if (parent != null) parent.Post(d, state);
            else Godot.Callable.From(() => d(state)).CallDeferred();
        }
        public void Close()
        {
            lock (_postGate)
            {
                _closedState = CombatManager.Instance.DebugOnlyGetState();
                _closed = true;
            }
            while (_ready.TryDequeue(out var late)) Post(late.Callback, late.State);
        }
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (SynchronizationContext.Current != this) throw new InvalidOperationException("Numerical rules must execute on their owning thread");
            d(state);
        }
        public bool TryExecute()
        {
            if (!_ready.TryDequeue(out var work)) return false;
            work.Callback(work.State); return true;
        }
        public Task Until(Func<bool> condition)
        {
            if (condition()) return Task.CompletedTask;
            var completion = new TaskCompletionSource();
            _waiting.Add((condition, completion));
            return completion.Task;
        }
        public void Poll()
        {
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                var waiter = _waiting[i];
                bool ready;
                try { ready = waiter.Condition(); }
                catch (Exception ex) { _waiting.RemoveAt(i); waiter.Completion.TrySetException(ex); continue; }
                if (!ready) continue;
                _waiting.RemoveAt(i); waiter.Completion.TrySetResult();
            }
        }
    }

    private sealed class Callbacks(ActionExecutor executor)
    {
        public Action<GameAction> Before { get; } = AccessTools.MethodDelegate<Action<GameAction>>(
            AccessTools.Method(typeof(ActionExecutor), "JustBeforeActionFinished"), executor);
        public Action<GameAction> After { get; } = AccessTools.MethodDelegate<Action<GameAction>>(
            AccessTools.Method(typeof(ActionExecutor), "AfterActionFinished"), executor);
    }

    // Select the direct Task-await branch only in this executor. Do not enable the
    // game's global NonInteractiveMode or alter native choices/model effects.
    private static bool DirectPath() => Active || NonInteractiveMode.IsActive;
    private static Task ExecuteAction(GameAction action)
    {
        Interlocked.Increment(ref _revision);
        if (Active)
        {
            var callbacks = ExecutorCallbacks.GetValue(RunManager.Instance.ActionExecutor, e => new(e));
            action.JustBeforeFinished += callbacks.Before;
            action.AfterFinished += callbacks.After;
            DirectActions++;
        }
        return action.Execute();
    }
    private static void FinishDirect(ActionExecutor executor, GameAction action)
    {
        var callbacks = ExecutorCallbacks.GetValue(executor, e => new(e));
        if (Active)
        {
            // Keep native callback timing, including an action paused for a choice.
            // No duplicate AfterActionExecuted and no early clearing of a paused action.
            action.JustBeforeFinished -= callbacks.Before;
            action.AfterFinished -= callbacks.After;
            return;
        }
        callbacks.After(action);
    }
    private static IEnumerable<CodeInstruction> DirectExecution(IEnumerable<CodeInstruction> instructions)
    {
        var active = AccessTools.PropertyGetter(typeof(NonInteractiveMode), nameof(NonInteractiveMode.IsActive));
        var execute = AccessTools.Method(typeof(GameAction), nameof(GameAction.Execute));
        var finished = AccessTools.Method(typeof(ActionExecutor), "AfterActionFinished");
        int branches = 0, actions = 0, finishes = 0;
        foreach (var instruction in instructions)
        {
            var replacement = new CodeInstruction(instruction);
            if (instruction.Calls(active)) { replacement.operand = AccessTools.Method(typeof(LocalWorkerLogic), nameof(DirectPath)); branches++; }
            else if (instruction.Calls(execute))
            { replacement.opcode = OpCodes.Call; replacement.operand = AccessTools.Method(typeof(LocalWorkerLogic), nameof(ExecuteAction)); actions++; }
            else if (instruction.Calls(finished))
            { replacement.opcode = OpCodes.Call; replacement.operand = AccessTools.Method(typeof(LocalWorkerLogic), nameof(FinishDirect)); finishes++; }
            yield return replacement;
        }
        if (branches != 1 || actions != 2 || finishes != 1) throw new InvalidOperationException("Native executor boundary changed");
    }

    private static bool DeferNotification(CombatStateTracker __instance, ref Task __result)
    {
        Interlocked.Increment(ref _revision);
        if (!Active || _dispatchingNotification) return true;
        var completion = new TaskCompletionSource();
        _current!.Post(_ => DispatchNotification(__instance, completion), null);
        __result = completion.Task;
        return false;
    }
    private static void DispatchNotification(CombatStateTracker tracker, TaskCompletionSource completion)
    {
        try
        {
            _dispatchingNotification = true;
            // Coalesce changes, then run the original card recalculation and every
            // subscriber. The original method's frame owner is the only removed step.
            var task = (Task)StateNotification.Invoke(tracker, null)!;
            if (!task.IsCompleted) throw new InvalidOperationException("State notification still depends on a frame");
            task.GetAwaiter().GetResult(); Notifications++; completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex); }
        finally { _dispatchingNotification = false; }
    }
}
