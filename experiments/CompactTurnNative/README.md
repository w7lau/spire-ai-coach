# Compact exact branch comparison

Prepare with `scripts/Update.ps1 -BuildOnly`, then build this launcher with `-p:BuildProjectReferences=false` against the prepared DLL. Arguments: `root game mod-directories.json frozen-request`. The root must be under this checkout's ignored `work` and contain `.hotpath-owner`.

Runs both algorithms on the same frozen input and resident owned worker for 32 completed attempts, with independent final replay. Early return goals are disabled. Only `CompactTurnOffers` differs. Both modes warm before both comparison orders. Require identical complete actions, choices, native checkpoints and settlement. Other improvements are enabled in both modes; this is not an old-version or eight-worker wall-time comparison.

Repeats the real observed alternatives through the actual broker, verifying exact task order, duplicate accounting and reporting serialized payload size separately from native game time. No live game execution, save changes or cache cleanup. Only sanitized aggregate receipts belong in Git.

An optional fifth argument `parallel` instead runs eight owned workers, 16 attempts per worker, against the same package with ordinary final replay. This validates concurrent compact proposal ownership and state settlement; it is not a comparative speed measurement.
