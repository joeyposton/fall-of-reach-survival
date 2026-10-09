# Reach Survival — mod log

Halo Reach (MCC) is the world; Rust's survival/building/crafting **rules** are read from the player's Rust install.
Nothing visible comes from Rust. Halo names on screen (C4 → "Demolition Charge", Tool Cupboard → "Forward Base Beacon").

## Agreed design (2026-10-08)
- Our own Unity program, Melty mode "standalone": halo-mcc primary, rust companion (`{game:rust}`).
- Spartans crash-land on Forge World with one Halo gun; gather, craft, build, raid each other.
- PvP up to 8, player-hosted, Unity Relay join code, Melty join link.
- v0.1: one Reach map + Spartans, Rust building + raiding, Covenant AI raids on a timer.
- No Rust models/icons/names on screen. Free / tips only (Microsoft content rules).

## Paths
- MCC: `C:\Program Files (x86)\Steam\steamapps\common\Halo The Master Chief Collection` — only Halo 3 + Reach installed.
- Reach maps: `haloreach\maps\*.map` (forge_halo = Forge World, ff* = Firefight).
- Rust: `C:\Program Files (x86)\Steam\steamapps\common\Rust` — IL2CPP, Unity 6000.3.15, EAC (never launched/touched).

## Tools (in tools/, not shipped except as noted)
- Reclaimer v2.1.1644 (GPL-3.0), MSI admin-extracted to tools/reclaimer. Reads MCC Reach caches (`MccHaloReachU13`).
- .NET 9 SDK 9.0.318 local in tools/dotnet9 (dotnet-install.ps1).
- UnityPy 1.25.4 (MIT) — reads Rust bundles; type trees are present.
- universal-modder toolkit (tools/universal-modder).

## Feasibility findings
### Halo Reach (works)
- `CacheFactory.ReadCacheFile` opens forge_halo.map (15,238 tags).
- `ContentFactory.TryGetGeometryContent` + walking Scene → OBJ: sbsp needs ALL permutations of each region
  (instances), first-permutation only gives scattered rocks. Forge World: 1.04M tris, 481×672×148 wu.
- Forge blocks (bloc → mode): ff_wall_2x2, ff_wall_2x1_door, ff_wall_2x4, ff_bridge_2x2 (floor), ff_two_way_door_*,
  shield doors. 1 wu = 3.048 m; 2x2 wall = 2 wu.
- Spartan `objects\characters\spartans\spartans` (forge_halo), Grunt/Elite/AR in ff10_prototype.
- Indices may be triangle strips (IndexFormat.TriangleStrip) — flip winding on odd.
### Rust (in progress)
- `Bundles/items/*.json` = basic item metadata (stack sizes, category, rarity) only.
- `Bundles/shared/items.preload.bundle`: 1029 ItemBlueprint (ingredients, amount, time, workbenchLevelRequired)
  + ItemDefinition (stackable, category…) with type trees → readable at runtime.
- `content.bundle` (4.6 GB, ~10 s to index with UnityPy): BuildingGrade ×16 (twigs 10hp/50 wood, wood 250/200 wood,
  stone 500/300 stone, metal 1000/200 frags, toptier 2000/25 HQM; cost itemDefs are FileID 8 = external items file),
  Construction ×113 (costMultiplier/healthMultiplier: Floor 0.5, Window 0.7, Foundation 1…), Door startHealth,
  Upkeep, ProtectionProperties ×159 (per-damage-type resistances), BuildingBlockDecay.
- `assetscenes.bundle` (1.2M MonoBehaviours): explosives with explosionRadius + damageTypes, e.g.
  explosive.timed.deployed (C4) r4.0 [(16 Explosion,550)], satchel r4 [(16,75),(11,200),(15,200)], rocket_basic r3.8.
- RESULT: feasibility PASS — every Rust rule v0.1 needs is readable from the player's install at runtime.
- Runtime plan: converter exe in C# with AssetsTools.NET (MIT) instead of Python; cache results, rescan on Rust update.
- Licence note: Reclaimer is GPL-3.0 → converter (and anything linking it) must be GPL-3.0 with source.
- Doors: Door.startHealth by GO name in assetscenes: door.hinged.wood 200, .metal 250, .toptier 1000.
- Trees: ResourceDispenser wood 250-500. ORE NODES: client prefabs carry no ResourceDispenser (server-only data) ->
  ore yields are our own design (sheets/design.json ore_yield). Tool cupboard: startHealth 100.
- Upkeep: per-piece upkeepMultiplier + decayGrade readable; the upkeep period/brackets are server convars (code) -> design.
- Halo stats (raw fields, Assembly ReachMCC weap/proj/hlmt + Reach jpt!/char layouts): AR 32/160, 10 rps, 5.83-6.79 dmg;
  Spartan hlmt body 45 shield 70 (stun 7 s, recharge 3 s); Grunt char body 40, no shield; Elite char 70/75;
  plasma pistol 16/bolt; plasma rifle 6-10, 6-9 rps. Reader: probe_refl (Reclaimer CreateReader + PointerExpander).
- Sheets in sheets/*.json; preflight.py lists open cells; gen.py writes Sheets.g.cs to converter + game.
- Unity project created: game/ (6000.4.10f1).

## Game build (2026-10-08)
- Unity 6000.4.10f1 project game/, built by Editor/BuildGame.cs; converter published self-contained to build/ReachSurvival/Converter.
- Host-authoritative Server + Client over Net (loopback solo; NGO named messages + Relay online, OnlineNet.cs).
- AutoTest (--autotest all) in the real build, all PASS: gather (Rust tree 300 wood), foundation/walls/window/doorway via
  Ghost snapping, upgrade twig->wood (Rust 250 hp, 200 wood), wooden door 200 hp, beacon 100 hp, raid 4 Covenant
  (return fire 115->90 in 6 s), killed 4/4 with drops, satchel destroys wall (Rust fuse/dud), smelter 10 s/ore + charcoal.
- Gotchas: "doorway".StartsWith("door"); doorway opening has no collider (pick doorway by aim ray distance);
  Forge World's sea is the water_big_foam submesh at y=6.7 (no collision, spawn above it);
  python edits must use encoding="utf-8" (cp1252 default mangled a file); OneDrive EPERM on package unpack -> retry build.
