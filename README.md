# Fall of Reach: Survival

Rust's survival, building and raiding on Halo: Reach's Forge World, for up to 8 Spartans. A mashup for [Melty](https://melty.gg).

- **Halo: Reach is the world.** Forge World's terrain, Spartans, Grunts, Elites, the MA37 and the Forge blocks you build with are read from the player's own Halo: The Master Chief Collection, along with Reach's shields, health and weapon stats.
- **Rust is the rules.** Recipes, building-grade health and costs, construction multipliers, damage protection, doors, the tool cupboard, upkeep, smelting and Satchel Charges (fuse, dud chance, damage) are read from the player's own Rust. Nothing from Rust is shown.
- Nothing from either game is in this repository or the release.

## How it works
- `converter/` (.NET 9, GPL-3.0): reads Reach `.map` caches with [Reclaimer](https://github.com/Gravemind2401/Reclaimer) and Rust's asset bundles with [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) into `%LOCALAPPDATA%\ReachSurvival\cache`.
- `game/` (Unity 6000.4): host-authoritative game; solo play is a local host, online play uses Unity Relay (`--host`, `--join CODE`).
- `sheets/`: the design as JSON sheets (one row per thing); `preflight.py` lists unfinished cells, `gen.py` generates `Sheets.g.cs` for both projects.
- `melty.json`: the install recipe Melty uses.

## Building
1. `powershell setup/setup.ps1` (fetches Reclaimer and the .NET 9 SDK into `tools/`).
2. `python sheets/preflight.py && python sheets/gen.py`
3. `tools/dotnet9/dotnet publish converter -c Release -r win-x64 --self-contained -o build/ReachSurvival/Converter`
4. Unity 6000.4.10f1: `-batchmode -projectPath game -executeMethod ReachSurvival.EditorTools.BuildGame.Build -quit`
5. Test: `build/ReachSurvival/ReachSurvival.exe --autotest all` (results in `%LOCALAPPDATA%\ReachSurvival\logs\latest.log`).

## Credits
Postman Games, built with Claude (AI). Reclaimer by Gravemind2401 (GPL-3.0); AssetsTools.NET by nesrak1 (MIT);
tag layouts from Assembly by XboxChaos (GPL-3.0). Halo is Microsoft's, Rust is Facepunch Studios'; this is an
unofficial, free fan mashup under Microsoft's Game Content Usage Rules.

Licence: GPL-3.0 (see LICENSE).
