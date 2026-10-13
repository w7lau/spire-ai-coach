# Proof frontier component measurement

Run against a specific prepared Coach DLL, e.g. `dotnet run --project experiments/ProofFrontierBenchmark -c Release -p:ProofDll=<absolute-coach-dll> -p:OutputPath=bin/baseline/`. Use separate output folders for the two versions. No game assemblies or native game execution are needed.

Constructs the same incomplete synthetic proof tree at depths 400 and 600, each with one unresolved sibling and an incumbent 12-loss victory. Repeated settled observations invalidate the cached focus list. After 20 warmup queries, report 100 calls to `Status` with elapsed time, thread allocations and returned focus count. Both versions must retain the unresolved zero lower bound. Observe/tree insertion and native execution are deliberately outside the measurement.

Depth 400 compares successful task discovery in both versions; depth 600 demonstrates the previous 512-node scan failure (zero tasks) versus one reachable task. These artificial deep histories isolate scheduler overhead and are not a claim about typical game depth, total search speed, memory residency or native optimality.
