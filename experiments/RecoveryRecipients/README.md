# Recovery recipient and permanent-deck metadata checks

Build against the exact DLL prepared by `scripts/Update.ps1 -BuildOnly`, using `NativeCoachDll` and `GameDir`. Pass the output JSON path as the single program argument. This executable only reads native/fixture IL and installs temporary diagnostic patches in its own console process; it never executes an effect callback or touches a player's combat.

Checks cover native FurCoat, BookOfFiveRings and SoulsPower; enemy enumeration and side guards; synchronous and asynchronous paths; owner/player/unknown/mixed recipients; reference mutation; strict-analysis retention; real permanent-deck removal, curse addition and transformation; and patched getters, writers, source callbacks and keyword removal. The negative cases protect against converting uncertain player healing into an early-return certificate.

These metadata checks are separate from native combat validation. The latest frozen 66/78 incident must also pass an owned root audit and real search/final-replay checks against the same prepared DLL. Raw captures and native assembly metadata stay in ignored `work/` directories; only explicitly selected aggregate evidence belongs in `results/`.
