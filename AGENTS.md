# Spire AI Coach

- Capture and AI advice remain read-only. The user explicitly authorized a button to execute a verified local plan (2026-10-02): only on click, singleplayer, matching combat/mods/native state/action history before every step, with cancellation and stop-on-divergence. Never auto-execute AI text, edit saves or mutate RNG directly.
- Simulation/replay stays in owned isolated workers. Live execution uses a separate guarded executor; never invoke LocalWorker simulation/reset actions in the live game. Preserve replay mismatch, stale-result, cancellation and OS-handle isolation checks.
- Read `docs/architecture.md` before changing snapshots, prompts or parsers. Keep the prompt, projection, validation and display contract synchronized.
- Game assemblies are local references only. Never commit DLLs, PCKs, decompiled game source, saves, credentials or real API configurations.
- Prefer codebase-memory graph tools for code discovery; fall back to local search if they are unavailable or insufficient.
- Run `dotnet run --project tests/SpireAiCoach.Tests -c Release` after core changes. Build the mod against the installed game with `-p:GameDir=...` after game adapter changes.
- Do not equate compile success or mocked HTTP tests with in-game / provider validation. Record unverified work in `docs/validation.md`.
- Keep experimental forecasts explicitly conditional. Use stable instance identities; never map actions by display name.
