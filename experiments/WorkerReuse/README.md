# Worker reuse protocol acceptance

Run from this checkout:

```powershell
dotnet run --project tests/SpireAiCoach.Tests -c Release -- --filter 'worker reuse'
dotnet run --project tests/SpireAiCoach.Tests -c Release
```

The Windows process cases execute the test apphost through the production pool and isolated launcher. Each fixture has a unique directory, fake installation and endpoint scope. The copied `SlayTheSpire2.exe` is the test executable, not the game. Its synthetic marker is required before it responds as a worker. Only handles to children created by these fixtures are observed. All owned children are stopped and joined before fixture deletion; no existing process is enumerated or changed.

The portable tests cover cancellation and idle identity matching, stale file rejection, interrupted turn work return and fresh broker isolation. Windows adds actual PID/start-time continuity, joined preparations, cancellation in search/verification, early goals during preparation/search, reduced capacity, startup failure, configuration changes, unexpected exits, missing cleanup, runtime errors and disposal. Native game code is built separately, and its behavior is not mocked into a claimed native acceptance.

`results/2026-10-04-windows-tests.txt` contains the final 207-check run and measured synthetic timings. `results/2026-10-04-build.txt` contains the Mod/LocalIntegration build. `results/2026-10-04-summary.json` includes the recorded worker PID/start time/generation and the validation limits. The artificial worker waits 650 ms at startup: cold/hot numbers describe protocol path selection, not game speed.

Real game reuse, real menu/save transitions and the six-round native goal remain unverified for this patch. A future native acceptance needs a new owned install/userdata tree, unique worker roots and brokers, fixed synthetic captures and measured PID/preparation/memory/cleanup evidence. It must avoid loading real saves or touching existing installations/processes. Directory and IPC isolation alone cannot guarantee absence of CPU/memory contention with existing game activity; use authorized evidence before starting that layer. No installation or release is included here.
