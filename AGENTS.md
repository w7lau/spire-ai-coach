# Spire AI Coach

- Keep the mod advisory and read-only. Never play cards, use potions, end turns, mutate RNG or edit saves without a new product decision.
- Read `docs/architecture.md` before changing snapshots, prompts or parsers. Keep the prompt, projection, validation and display contract synchronized.
- Game assemblies are local references only. Never commit DLLs, PCKs, decompiled game source, saves, credentials or real API configurations.
- Prefer codebase-memory graph tools for code discovery; fall back to local search if they are unavailable or insufficient.
- Run `dotnet run --project tests/SpireAiCoach.Tests -c Release` after core changes. Build the mod against the installed game with `-p:GameDir=...` after game adapter changes.
- Do not equate compile success or mocked HTTP tests with in-game / provider validation. Record unverified work in `docs/validation.md`.
- Keep experimental forecasts explicitly conditional. Use stable instance identities; never map actions by display name.
