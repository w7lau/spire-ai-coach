# Incremental proof frontier native acceptance

Build the exact package with `scripts/Update.ps1 -BuildOnly`, then build this launcher with `-p:BuildProjectReferences=false` against that DLL. Arguments: `root game mod-directories.json frozen-request`. The root must be inside this checkout's ignored `work` directory and contain `.hotpath-owner`.

Runs two independent searches against one frozen native request and eight owned isolated workers. Keeps the captured algorithm, budget and node limits, removes any initial answer, and requires ordinary independent final replay plus complete execution checkpoints. Any reported minimum stop must pass the existing confirmed proof contract. The launcher checks its DLL hash against the prepared package. No player game execution, save editing, RNG editing or worker-cache cleanup.

Version comparisons have nondeterministic parallel scheduling and search-budget limits; report candidate quality, proof closure, node/branch counts and replay separately. Elapsed time includes final verification and is not a proof-scheduler microbenchmark. Do not infer general speedup or optimality from repeated equal candidates. Keep raw requests and outputs private; publish only small aggregate evidence.
