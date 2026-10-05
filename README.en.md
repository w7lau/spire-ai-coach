# Spire AI Coach · 尖塔 AI 教练

[中文](README.md) · [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3814010843) · [Download](https://github.com/w7lau/spire-ai-coach/releases/latest) · [Issues](https://github.com/w7lau/spire-ai-coach/issues) · [Contribute](CONTRIBUTING.md)

A **Slay the Spire 2** mod for AI advice on the current turn, local planning for the entire battle without an API, and optional execution of a computed plan. Built for fun, experimentation and comparing strategies. A candidate found within a budget is not necessarily globally optimal.

The design aims for **broad compatibility with different mods**: capture underlying game state and reuse native rules and loaded mod hooks to simulate actual plays, choices and resolution, rather than hand-code every card or relic. Recognized presentation work and animation waits are skipped in background simulation. This reduces per-content adapters but does not guarantee support for every mod.

## Getting started

1. Extract the release and place the `SpireAiCoach` folder in the game's `mods` directory, then start the game and enable the mod. Use either the Workshop installation or the manual installation to avoid duplicate loading.
2. Press **F8** to open or close the panel. The corner button is hidden by default; the panel opens automatically when entering combat. In “计算选项 → 高级设置” (advanced calculation settings), disable “进入战斗自动展开面板” for manual-only opening, or enable “显示左上角入口” for the corner button.
3. During your play phase, choose **路线采样 / Rollout Sampling** or **分支扩展 / Branch Expansion**. Follow a complete plan manually or click “执行方案” to execute it. **Esc stops an active plan execution**.
4. For AI advice, expand “AI 指导与设置”, enter a Chat Completions compatible URL, model and API key, save, and click “AI 分析”.

The current release is primarily validated on **Windows x64 and game v0.111.0**. Game updates and third-party mods can change interfaces or introduce unsupported effects. Include your game, mod list and coach versions in reports. The in-game UI currently uses Chinese labels.

## Local planning

| Search | How it explores | Scope |
| --- | --- | --- |
| **Rollout Sampling / 路线采样** | Expands complete battle candidates and refines action exploration using existing outcomes | A winning plan from the current state to the end of combat |
| **Branch Expansion / 分支扩展** | Organizes candidates by turn and extends and distributes continuations into later turns | The entire battle, rather than just the current turn |

Both use native rules for damage, block, draw, discard, exhaust, selections, powers, relics and enemy actions. Mods requiring UI nodes, external services or custom selection flows may need additional integration.

- Winning routes are compared by **net HP loss after combat**, including healing during and after combat. Equal-loss routes prefer preserving potions. Optional potion use is off by default; enabling it does not require drinking every potion.
- Stop on minimum loss returns after a zero-loss win or a sufficiently proven loss bound. Unknown bounds continue searching. Stop on first win is useful for final bosses.
- Configure concurrency, attempts per worker, search seconds and the turn limit. Defaults: 64 attempts, 60 search seconds per worker and a 64-turn horizon. Zero workers means automatic sizing; the manual cap is 16. Preparation and optional final verification take additional time.
- Optional goals encourage playing a selected card more often or using it for a finisher, within a chosen HP-loss tolerance. Finishers count targets eligible for the game's native Fatal rewards, respecting minion and mod restrictions.
- Inspect progress, explored routes and timing, or continue optimizing an existing candidate. Final independent verification is skipped by default and can be enabled.
- Execution starts only after clicking the button, in singleplayer combat. Each step checks the native state, action history and mod environment and stops on divergence. AI text is never executed automatically.

A winning plan, the best discovered candidate, and a proven minimum-loss result are distinct claims. Reaching an attempt, time or turn limit does not mean the search was exhaustive. Incomplete routes are not returned as winning plans.

## AI and data

AI advice uses a snapshot of your hand, draw/discard/exhaust piles, energy, HP, powers, relics, potions and enemy attributes and visible intents. Replies stream into the panel. Stable rules and battle context appear early in requests to support provider prefix caching; actual cache hits depend on the provider.

Battle information is sent to the configured provider only when you click AI analysis and may incur API charges. Local search does not send combat data to an AI provider. Keys are kept in the current session by default; optional persistence uses DPAPI encryption tied to the current Windows account. Do not post API keys, configuration files, full saves or unredacted diagnostics in issues.

## Help improve the algorithms

Issues and pull requests are welcome. The goal is to **find complete winning routes with lower damage, approaching or proving optimal play, in the least actual computation time**. State reuse, shared prefixes, concurrency, native simulation overhead, action ordering and justified pruning are all useful areas.

Compare identical inputs, budgets and objectives. Report preparation/search/verification time, valid candidates and route quality. Reducing budgets, omitting legal moves or ignoring mod effects is not a valid speed improvement. See [CONTRIBUTING.md](CONTRIBUTING.md) for entry points and expectations.

## Development

Use the .NET 9 SDK. Building the mod requires your own legitimate game installation. Building does not install anything or modify game files.

```powershell
# Core checks: no game files or API key required
dotnet run --project tests/SpireAiCoach.Tests -c Release

# Build and package
./scripts/Build.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'

# Install with the game closed; existing mod files are backed up
./scripts/Install.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
```

`STS2_GAME_DIR` can also specify the installation. Game assemblies, assets and player saves are not redistributed.

| Directory | Contents |
| --- | --- |
| `src/SpireAiCoach.Core` | Search coordination, contracts, AI client and formatting |
| `src/SpireAiCoach.Mod` | Native state capture, background simulation, UI and executor |
| `tests/SpireAiCoach.Tests` | Checks without game dependencies |
| `experiments/LocalIntegration` | Native checks in an owned isolated environment |
| `docs` | Architecture, search policy, performance and validation evidence, mostly in Chinese |
| `workshop` | Bilingual publishing text and original cover source |

## Credits and license

**CombatSolver, autoSpire, SpireVibePlaying and ModTemplate-StS2** informed research on search organization, action/selection interfaces and mod initialization. Their solvers were not imported. See [ACKNOWLEDGEMENTS.md](ACKNOWLEDGEMENTS.md) for the exact relationships, other research and the official Workshop uploader. Thanks to their authors for sharing their work.

Original project code is available under the [MIT License](LICENSE). Game code, assets and trademarks remain the property of their owners. This is an independent community mod, not an official Mega Crit product.
