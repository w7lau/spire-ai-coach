# Owned Task callable native regression

Build the product with `scripts/Update.ps1 -BuildOnly` first. Build this launcher and LocalIntegration with `-p:BuildProjectReferences=false` so they reference that exact DLL. The observer stays in private worker Mod directories.

The launcher takes `root game mod-directories.json frozen-request baseline|fixed`. The root must be under this checkout's ignored `work` directory and contain `.task-callable-owner`. The JSON directory list identifies the exact frozen external Mods; original model/RNG/history and native identity checks remain mandatory.

Baseline disables only the five audited callback-factory transpilers inside owned workers and requires the original Task-to-Variant runtime failure. Fixed runs both algorithms with the original eight workers, trial and time limits, explicitly preserving ordinary independent final replay. A complete loss is a valid replay result; it cannot be presented as a victory. No answer seed or live game is used.

The native observer records actual VFX entry invocations and checks once-only asynchronous callback execution, a Nil Godot return, and an unchanged integer callback. It requires five audited registration sites and current evidence per owned worker. Full requests, outputs, traces, Mod paths and host logs belong in ignored locations; publish only the sanitized receipt under LocalIntegration/results.
