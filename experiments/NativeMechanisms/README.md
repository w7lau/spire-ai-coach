# Installed native mechanism audit

This reads the installed game's metadata and IL without constructing models or executing gameplay hooks. It inventories concrete card/relic/power/potion classes, direct selection-command references and the finite selection adapter contract. Counts include mocks, deprecated and internal models; they are not a count of obtainable cards or a proof that every card/Mod has been played.

```powershell
dotnet run --project experiments/NativeMechanisms -c Release '-p:GameDir=R:\SteamLibrary\steamapps\common\Slay the Spire 2' -- C:\private-output\inventory.json
```

A changed native command set or grid family returns a nonzero exit code. Game assemblies and decompiled source must stay outside Git. The complete runtime matrix and actual card/choice-history tests are in `experiments/LocalIntegration`; the product coverage boundaries are documented in `docs/native-mechanism-coverage.md`.
