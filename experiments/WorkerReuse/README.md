# Worker reuse protocol acceptance

Run from this checkout:

```powershell
dotnet run --project tests/SpireAiCoach.Tests -c Release -- --filter 'worker reuse'
dotnet run --project tests/SpireAiCoach.Tests -c Release
```

The Windows process cases execute the test apphost through the production pool and isolated launcher. Each fixture has a unique directory, fake installation and endpoint scope. The copied `SlayTheSpire2.exe` is the test executable, not the game. Its synthetic marker is required before it responds as a worker. Only handles to children created by these fixtures are observed. All owned children are stopped and joined before fixture deletion; no existing process is enumerated or changed.

The portable tests cover cancellation and idle identity matching, stale file rejection, interrupted turn work return and fresh broker isolation. Windows adds actual PID/start-time continuity, joined preparations, cancellation in search/verification, early goals during preparation/search, reduced capacity, startup failure, configuration changes, unexpected exits, missing cleanup, runtime errors and disposal. Native game code is built separately, and its behavior is not mocked into a claimed native acceptance.

`results/2026-10-04-windows-tests.txt` contains the final 207-check run and measured synthetic timings. `results/2026-10-04-build.txt` contains the Mod/LocalIntegration build. `results/2026-10-04-summary.json` includes the recorded worker PID/start time/generation and the validation limits. The artificial worker waits 650 ms at startup: cold/hot numbers describe protocol path selection, not game speed.

Native acceptance now uses `NativeRunner`, a console coordinator that calls the production pool with exactly one real headless game worker. `prepare_native.py` first copies game binaries/assets and the six frozen-request Mods into a new `work` directory and checks their bytes; it never launches a process. The pool creates separate userdata, scoped endpoints and worker ownership beneath that private installation. Only the Coach version/MVID in the approved frozen request is adapted to this build, as in the existing replay integration. Model, Mod, replay and state checks remain enabled. No candidate route is supplied.

```powershell
dotnet build experiments/WorkerReuse/NativeRunner/NativeRunner.csproj -c Release '-p:GameDir=<installed game>'
python experiments/WorkerReuse/prepare_native.py --root work/native-reuse-new --game '<installed game>' --request '<approved frozen request>' --workshop '<workshop content root>'
dotnet experiments/WorkerReuse/NativeRunner/bin/Release/net9.0/WorkerReuseNativeRunner.dll work/native-reuse-new
python experiments/WorkerReuse/verify_native.py --root work/native-reuse-new --game '<installed game>' --workshop '<workshop content root>'
```

The driver checks cold/hot preparation, a first unseeded six-round goal, goal/research, cancellation after real search progress, cancellation after real independent-verification progress, and another strict goal after each cancellation. Every observation checks PID/start-time/generation continuity and one live owned lane. Disposal must end that known child and release its lock. The verification script rehashes the read-only source assets/installed Coach and records diagnostics. Raw replay, results and logs stay under ignored `work`; committed results contain aggregate metrics and hashes only.

Recorded single-worker Windows native runs are in `results/2026-10-04-native-run-a.json` and `results/2026-10-04-native-run-b.json`. Two-worker evidence is in `results/2026-10-04-native-two-workers.json`; the following mode uses another newly prepared private directory and the same approved frozen request:

```powershell
dotnet experiments/WorkerReuse/NativeRunner/bin/Release/net9.0/WorkerReuseNativeRunner.dll work/native-reuse-two-new --two-workers
python experiments/WorkerReuse/verify_native.py --root work/native-reuse-two-new --game '<installed game>' --workshop '<workshop content root>'
```

The two-worker driver forces fixed two-lane admission to observe a genuine cold peer: a goal is verified while that peer is still booting, then its original preparation is joined and its identity retained. Warm scenarios check both lanes' request/partition/generation cleanup, rejection of other-lane acknowledgments, a stale request's stop signal followed by continuing native progress, cancellation during search and verification, and fresh brokers for later requests. A controlled `NtSuspendProcess` call targets only the handle returned for this pool's lane 1, after native progress from both lanes. The normal two-second cancellation fallback must retire that child alone; lane 0 must retain its PID/start/generation, and rebuilding lane 1 must recover another independently verified goal. All three cumulative child handles are joined and both locks reopened on exit. No existing process is enumerated, selected by name, suspended or stopped. An OS rejection fails the acceptance and is not bypassed.

Five two-lane unseeded goals passed on Windows. The bad peer was retired after 2126 ms; launches per lane were `[1, 2]`, with zero healthy-lane rebuilds and one deliberately faulted-lane rebuild. The final applicable regression was 207/207 (`results/2026-10-04-two-worker-regression.txt`). Review found no additional production change necessary.

These tests demonstrate native reuse and independent replay for this frozen fixture and its six-Mod set. They do not establish foreground menu/save behavior or arbitrary Mod compatibility. Default adaptive admission, broader startup/exit failures and menu/save identities remain covered by automated cases; native parallel lifecycle acceptance deliberately uses fixed admission. Fault injection proves the timeout/recovery mechanism, not every third-party callback defect. The verifier retains engine startup diagnostics and rejects engine errors after Common preload completion. Shared-machine CPU/memory contention was not controlled, so timings are observations, not a paired speedup benchmark. No installation or release is included here.
