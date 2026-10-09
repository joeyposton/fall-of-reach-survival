using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using ReachSurvival.Sheets;

namespace ReachSurvival.Convert
{
    /// <summary>
    /// Reads every rust_rules row from the player's Rust bundles (type trees are present) and writes rust/rules.json.
    /// Only numbers and identifiers are read: nothing visible from Rust is used.
    /// </summary>
    static class RustExport
    {
        // Script class names we need, by bundle ("*Door" = any class ending in Door, e.g. DudTimedExplosive for C4). ItemDefinition/ItemBlueprint first: other rows refer to items.
        static readonly (string bundle, string[] classes)[] Plan =
        {
            ("items.preload.bundle", new[] { "ItemDefinition", "ItemBlueprint", "ItemModCookable", "ItemModBurnable" }),
            ("content.bundle", new[] { "BuildingGrade", "Construction", "ProtectionProperties", "Upkeep", "*Door", "*Oven" }),
            ("assetscenes.bundle", new[] { "*Door", "*TimedExplosive", "ResourceDispenser", "BuildingPrivlidge", "*Oven" }),
        };

        public static int Run(string rustDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var am = new AssetsManager();
            var found = new Dictionary<string, List<(AssetsFileInstance file, AssetFileInfo info, string goName)>>();
            var fields = new Dictionary<long, AssetTypeValueField>();

            foreach (var (bundleName, classes) in Plan)
            {
                var path = Path.Combine(rustDir, "Bundles", "shared", bundleName);
                Log.Info($"Reading Rust {bundleName}");
                var bun = am.LoadBundleFile(path, false);
                if (bun.file.DataIsCompressed)
                {
                    Log.Info($"  unpacking {bundleName} (first launch only)");
                    bun = am.LoadBundleFile(path, true);
                }
                int n = bun.file.BlockAndDirInfo.DirectoryInfos.Count;
                for (int i = 0; i < n; i++)
                {
                    if (!bun.file.IsAssetsFile(i)) continue;
                    var af = am.LoadAssetsFileFromBundle(bun, i, false);
                    // Script class per script-type index, so we only read the MonoBehaviours we need.
                    var scriptNames = new Dictionary<ushort, string>();
                    foreach (var info in af.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                    {
                        ushort si = info.GetScriptIndex(af.file);
                        if (!scriptNames.TryGetValue(si, out var cls))
                        {
                            cls = "";
                            try
                            {
                                var pptr = af.file.Metadata.ScriptTypes[si];
                                var ms = am.GetExtAsset(af, pptr.FileId, pptr.PathId);
                                cls = ms.baseField?["m_ClassName"].AsString ?? "";
                            }
                            catch { }
                            scriptNames[si] = cls;
                        }
                        var key = classes.FirstOrDefault(c => c == cls || (c[0] == '*' && cls.EndsWith(c[1..], StringComparison.Ordinal)));
                        if (key == null) continue;
                        var bf = am.GetBaseField(af, info);
                        string go = "";
                        var gop = bf["m_GameObject"];
                        if (!gop.IsDummy && gop["m_PathID"].AsLong != 0)
                        {
                            try { go = am.GetExtAsset(af, gop["m_FileID"].AsInt, gop["m_PathID"].AsLong).baseField["m_Name"].AsString; } catch { }
                        }
                        if (go == "") go = bf["m_Name"].IsDummy ? "" : bf["m_Name"].AsString;
                        if (!found.TryGetValue(key, out var list)) found[key] = list = new();
                        list.Add((af, info, go));
                        fields[info.PathId] = bf;
                    }
                }
                Log.Info($"  {string.Join(", ", classes.Select(c => $"{c} {(found.TryGetValue(c, out var l) ? l.Count : 0)}"))}");
            }

            var rules = Build(found, fields);
            File.WriteAllText(Path.Combine(outDir, "rules.json"), JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true }));
            am.UnloadAll();
            return rules.Missing.Count == 0 ? 0 : 1;
        }

        // ---- what the game reads ---------------------------------------------------------------------------

        public sealed class Cost { public string Item { get; set; } public float Amount { get; set; } }
        public sealed class Grade { public float Health { get; set; } public List<Cost> Cost { get; set; } = new(); public float[] Protection { get; set; } }
        public sealed class Piece { public float CostMultiplier { get; set; } public float HealthMultiplier { get; set; } }
        public sealed class Explosive { public float Radius { get; set; } public float MinRadius { get; set; } public float TimerMin { get; set; } public float TimerMax { get; set; } public float DudChance { get; set; } public Dictionary<string, float> DamageByType { get; set; } = new(); }
        public sealed class Blueprint { public List<Cost> Ingredients { get; set; } = new(); public float Time { get; set; } public int Workbench { get; set; } public int Amount { get; set; } }
        public sealed class Item { public int Stack { get; set; } public int Category { get; set; } }
        public sealed class Cook { public string Become { get; set; } public float Time { get; set; } public float Amount { get; set; } }
        public sealed class Burn { public float Fuel { get; set; } public string Byproduct { get; set; } public int ByproductAmount { get; set; } public float ByproductChance { get; set; } }
        public sealed class Oven { public float Health { get; set; } public int Slots { get; set; } }
        public sealed class Rules
        {
            public Dictionary<string, Item> Items { get; set; } = new();
            public Dictionary<string, Blueprint> Blueprints { get; set; } = new();
            public Dictionary<string, Grade> Grades { get; set; } = new();
            public Dictionary<string, Piece> Pieces { get; set; } = new();
            public Dictionary<string, float> Doors { get; set; } = new();
            public Dictionary<string, Explosive> Explosives { get; set; } = new();
            public Dictionary<string, float> TreeWood { get; set; } = new();
            public float CupboardHealth { get; set; }
            public Dictionary<string, Cook> Cooking { get; set; } = new();
            public Dictionary<string, Burn> Burning { get; set; } = new();
            public Oven Furnace { get; set; }
            public Dictionary<string, float> Upkeep { get; set; } = new();
            public List<string> Missing { get; set; } = new();
        }

        static Rules Build(Dictionary<string, List<(AssetsFileInstance file, AssetFileInfo info, string goName)>> found, Dictionary<long, AssetTypeValueField> f)
        {
            var r = new Rules();
            IEnumerable<(AssetTypeValueField bf, string go)> All(string cls) =>
                found.TryGetValue(cls, out var l) ? l.Select(x => (f[x.info.PathId], x.goName)) : Enumerable.Empty<(AssetTypeValueField, string)>();

            // items: pathId -> shortname (Rust's hashed path IDs are unique across its bundles)
            var shortById = new Dictionary<long, string>();
            foreach (var (bf, _) in All("ItemDefinition"))
            {
                var sn = bf["shortname"].AsString;
                shortById[bf["m_GameObject"]["m_PathID"].AsLong] = sn;
                r.Items[sn] = new Item { Stack = bf["stackable"].AsInt, Category = bf["category"].AsInt };
            }
            // ItemDefinition is referenced by component pathId, not GameObject: index both.
            foreach (var x in found.GetValueOrDefault("ItemDefinition") ?? new())
                shortById[x.info.PathId] = f[x.info.PathId]["shortname"].AsString;

            List<Cost> Costs(AssetTypeValueField arr) => arr.Children
                .Select(c => new Cost { Item = shortById.GetValueOrDefault(c["itemDef"]["m_PathID"].AsLong, "?"), Amount = c["amount"].AsFloat })
                .ToList();

            var wanted = Sheets.Items.All.Select(i => i.RustShortname).ToHashSet();
            foreach (var (bf, _) in All("ItemBlueprint"))
            {
                var owner = shortById.GetValueOrDefault(bf["m_GameObject"]["m_PathID"].AsLong);
                if (owner == null || !wanted.Contains(owner)) continue;
                r.Blueprints[owner] = new Blueprint
                {
                    Ingredients = Costs(bf["ingredients"]["Array"]),
                    Time = bf["time"].AsFloat,
                    Workbench = bf["workbenchLevelRequired"].AsInt,
                    Amount = bf["amountToCreate"].AsInt,
                };
            }

            // Every ingredient of a recipe we use must itself be a row in sheets/items.json.
            var v01 = Sheets.Items.All.Where(i => i.Version == "v0.1").Select(i => i.RustShortname).ToHashSet();
            foreach (var (owner, bp) in r.Blueprints.Where(b => v01.Contains(b.Key)))
                foreach (var ing in bp.Ingredients)
                    if (!wanted.Contains(ing.Item)) r.Missing.Add($"ingredient {ing.Item} of {owner} is not in sheets/items.json");

            foreach (var (bf, _) in All("ItemModCookable"))
            {
                var owner = shortById.GetValueOrDefault(bf["m_GameObject"]["m_PathID"].AsLong);
                if (owner == null || !wanted.Contains(owner)) continue;
                r.Cooking[owner] = new Cook { Become = shortById.GetValueOrDefault(bf["becomeOnCooked"]["m_PathID"].AsLong, "?"), Time = bf["cookTime"].AsFloat, Amount = bf["amountOfBecome"].AsFloat };
            }
            foreach (var (bf, _) in All("ItemModBurnable"))
            {
                var owner = shortById.GetValueOrDefault(bf["m_GameObject"]["m_PathID"].AsLong);
                if (owner == null || !wanted.Contains(owner)) continue;
                r.Burning[owner] = new Burn
                {
                    Fuel = bf["fuelAmount"].AsFloat,
                    Byproduct = shortById.GetValueOrDefault(bf["byproductItem"]["m_PathID"].AsLong, ""),
                    ByproductAmount = bf["byproductAmount"].AsInt,
                    ByproductChance = bf["byproductChance"].AsFloat,
                };
            }
            foreach (var need in new[] { "metal.ore", "sulfur.ore" }) if (!r.Cooking.ContainsKey(need)) r.Missing.Add($"cooking {need}");
            foreach (var need in new[] { "wood", "lowgradefuel" }) if (!r.Burning.ContainsKey(need)) r.Missing.Add($"burning {need}");

            var oven = All("*Oven").FirstOrDefault(x => x.go == "furnace" || x.go.EndsWith("/furnace.prefab"));
            if (oven.bf == null) { r.Missing.Add("furnace"); foreach (var (_, go) in All("*Oven").Take(40)) Log.Info("  oven candidate: " + go); }
            else r.Furnace = new Oven { Health = oven.bf["startHealth"].AsFloat, Slots = oven.bf["inventorySlots"].IsDummy ? 0 : oven.bf["inventorySlots"].AsInt };

            var protById = found.GetValueOrDefault("ProtectionProperties")?.ToDictionary(x => x.info.PathId, x => f[x.info.PathId]) ?? new();
            foreach (var g in Sheets.Grades.All)
            {
                var hit = All("BuildingGrade").FirstOrDefault(x => x.bf["m_Name"].AsString == g.RustGrade);
                if (hit.bf == null) { r.Missing.Add($"grade {g.RustGrade}"); continue; }
                var grade = new Grade { Health = hit.bf["baseHealth"].AsFloat, Cost = Costs(hit.bf["baseCost"]["Array"]) };
                if (protById.TryGetValue(hit.bf["damageProtecton"]["m_PathID"].AsLong, out var prot))
                    grade.Protection = prot["amounts"]["Array"].Children.Select(c => c.AsFloat).ToArray();
                r.Grades[g.RustGrade] = grade;
            }

            foreach (var p in Sheets.Pieces.All)
            {
                var hit = All("Construction").FirstOrDefault(x => x.bf["info"]["name"]["legacyEnglish"].AsString == p.RustConstruction);
                if (hit.bf == null) { r.Missing.Add($"construction {p.RustConstruction}"); continue; }
                r.Pieces[p.RustConstruction] = new Piece { CostMultiplier = hit.bf["costMultiplier"].AsFloat, HealthMultiplier = hit.bf["healthMultiplier"].AsFloat };
            }

            foreach (var name in new[] { "door.hinged.wood", "door.hinged.metal", "door.hinged.toptier" })
            {
                var hit = All("*Door").FirstOrDefault(x => x.go == name);
                if (hit.bf == null) r.Missing.Add($"door {name}"); else r.Doors[name] = hit.bf["startHealth"].AsFloat;
            }

            foreach (var row in Sheets.Explosives.All)
            {
                var hit = All("*TimedExplosive").FirstOrDefault(x => x.go.EndsWith("/" + row.RustPrefab + ".prefab") || x.go == row.RustPrefab);
                if (hit.bf == null) { r.Missing.Add($"explosive {row.RustPrefab}"); continue; }
                float F(string k) => hit.bf[k].IsDummy ? 0f : hit.bf[k].AsFloat;
                var e = new Explosive { Radius = F("explosionRadius"), MinRadius = F("minExplosionRadius"), TimerMin = F("timerAmountMin"), TimerMax = F("timerAmountMax"), DudChance = F("dudChance") };
                foreach (var d in hit.bf["damageTypes"]["Array"].Children)
                    e.DamageByType[d["type"].AsInt.ToString()] = d["amount"].AsFloat;
                r.Explosives[row.RustPrefab] = e;
            }

            foreach (var (bf, go) in All("ResourceDispenser"))
            {
                if (bf["gatherType"].AsInt != 0) continue; // 0 = tree
                var wood = bf["containedItems"]["Array"].Children.Where(c => shortById.GetValueOrDefault(c["itemDef"]["m_PathID"].AsLong) == "wood").Sum(c => c["amount"].AsFloat);
                if (wood > 0 && go != "") r.TreeWood[go] = wood;
            }
            if (r.TreeWood.Count == 0) r.Missing.Add("tree yields");

            var tc = All("BuildingPrivlidge").FirstOrDefault(x => x.go.EndsWith("/cupboard.tool.deployed.prefab") || x.go == "cupboard.tool.deployed");
            if (tc.bf == null) r.Missing.Add("tool cupboard"); else r.CupboardHealth = tc.bf["startHealth"].AsFloat;

            foreach (var (bf, go) in All("Upkeep"))
                if (go != "" && !r.Upkeep.ContainsKey(go)) r.Upkeep[go] = bf["upkeepMultiplier"].AsFloat;

            foreach (var m in r.Missing) Log.Error("Rust: missing " + m);
            return r;
        }
    }
}
