# Summon recovery recipient metadata

Build and run against the prepared product DLL with `-p:BuildProjectReferences=false` and the installed `GameDir`. Arguments: `mod-directories.json output.json`. Uses actual native game IL and the currently installed HextechRunes 0.111.0 variant. Inspect only; no model getters, battle effects or save writes are executed.

Checks direct and asynchronous summon recovery, helper argument propagation, max-HP growth, player and unknown recipients, mixed branches, by-reference mutation, and foreign Harmony patches on the recipient getter or Heal. Strict content closure must retain the recovery while the user-facing target can exclude a proved summon recipient. Reports 21 assertions and the product SHA256. This is metadata correctness evidence, not a native combat benchmark.
