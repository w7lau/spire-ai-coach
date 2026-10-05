# Spire AI Coach · 尖塔 AI 教练

[中文](README.md) · [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3814010843) · [Download](https://github.com/w7lau/spire-ai-coach/releases/latest) · [Issues](https://github.com/w7lau/spire-ai-coach/issues)

A **Slay the Spire 2** battle assistant with **AI advice for the current turn**, **local whole-battle planning**, and **plan execution**. Built for fun, research and comparing strategies.

## Getting started

1. Subscribe through the Workshop, launch the game and enable the mod. Alternatively, place the release's `SpireAiCoach` folder in the game's `mods` directory. Choose one installation method.
2. Press **F8** to open or close the panel.
3. Under “本地整场计算” (Local whole-battle planning), choose “路线采样算法” (Rollout Sampling) or “分支扩展算法” (Branch Expansion). Follow the plan manually or click “执行方案” (Execute plan). **Esc stops execution**.
4. For AI advice, enter a Chat Completions compatible URL, model and API key under “AI 指导与设置”, save, and click “AI 分析”.

## Local battle planning

Both algorithms plan the whole battle from the current state without an API.

| Algorithm | Search approach |
| --- | --- |
| **Rollout Sampling / 路线采样** | Repeatedly simulates whole-battle routes, adjusts play order, targets and choices, and compares outcomes |
| **Branch Expansion / 分支扩展** | Expands alternative action branches, schedules exploration by turn, and compares whole-battle outcomes |

- Prioritizes net HP loss after combat, including healing during and after battle. Equal-loss routes prefer preserving potions.
- Configure concurrency, attempts, search time, turn limits, stopping conditions, potion use and card goals.
- Inspect progress, routes and timing, or continue searching.
- Zero-loss early return requires full HP after combat resolution, using the final HP cap. Zero net loss alone keeps searching.
- Plan execution supports singleplayer and stops when the actual state diverges from the plan.

To support different mods, local search aims to reuse underlying game rules and loaded mod hooks for plays and resolution, reducing per-card adapters.

## AI advice

Uses your hand, piles, energy, HP, buffs, relics, potions and enemy intents to suggest plays for the current turn. Replies stream into the panel.

Clicking AI analysis sends battle information to your configured provider and may incur API charges. API keys can be saved locally with encryption.

## Compatibility

Validated on **Windows x64 / game v0.111.0**. In-game labels are mainly Chinese. Some mods may need additional integration. Search is limited by time and attempts, and results are not guaranteed globally optimal.

## Help improve the algorithms

Issues and PRs are welcome. Help find complete winning routes with the lowest damage, approaching or proving optimal play, as quickly as possible. See [CONTRIBUTING.md](CONTRIBUTING.md) to contribute.

## Development

Use the .NET 9 SDK. Building the mod requires a local game installation.

```powershell
./scripts/Update.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
```

This entry point validates, packages and installs. Passed checks are reused only for identical source and SDK inputs; matching packages are verified by hash before reuse. A successful run remembers the local game path, so later runs need only `./scripts/Update.ps1`. If the game is running, the prepared package is retained; use `-InstallOnly` after exiting. Each stage records its duration. Use `-BuildOnly` before any necessary native validation. See the [update workflow](docs/local-update.md).

[Architecture](docs/architecture.md) · [Search policy](docs/local-search-policy.md) · [Performance](docs/local-simulation-performance.md) · [Validation](docs/validation.md)

## Credits and license

Development referenced CombatSolver, autoSpire, SpireVibePlaying and ModTemplate-StS2. See [ACKNOWLEDGEMENTS.md](ACKNOWLEDGEMENTS.md) for sources and relationships.

Original code uses the [MIT License](LICENSE). This is an independent community mod.
