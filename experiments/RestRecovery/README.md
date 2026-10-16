# Rest-phase recovery classification

Build/run this metadata check against the exact package prepared by `Update.ps1 -BuildOnly`:

```powershell
dotnet run --project experiments/RestRecovery -c Release -p:NativeCoachDll=<absolute-prepared-dll> -p:GameDir=<installed-game> -- <report.json>
```

The check reads actual game method bodies without executing model callbacks. It verifies that native `StoneHumidifier.AfterRestSiteHeal` does not create a combat return target, while strict recovery analysis still retains it. Fixed victory healing, actual combat healing, external rest overrides and an externally reachable helper remain recognized. The fixtures initialize the held-source types as the real estimator does.

This is metadata evidence, not native combat or search performance evidence. Frozen combat comparisons use the independent RouteStudyNative launcher, exact package hashes, owned workers and ordinary final replay. A complete supplied winning prefix verifies return behavior only; it cannot establish discovery speed. Keep raw requests, routes, game metadata and Mod copies in ignored `work` directories.
