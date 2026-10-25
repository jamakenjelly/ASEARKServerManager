using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ServerManagerTool.Lib
{
    /// <summary>
    /// Builds game data from an ASE server's own files. For every mod installed in a server (ShooterGame\Content\Mods\&lt;id&gt;) the names of the
    /// mod's assets give its items, engrams, creatures, spawn containers and supply crates, written to Mod_&lt;id&gt;_generated.gamedata in the data
    /// folder's GameData folder under the mod's own name. The same is done for the base game's files (ASE_generated.gamedata), for everything the
    /// manager's built-in game data does not have yet. The files are read by the manager at start-up like every other game data file, and made again
    /// only when the mod (or the server build) changes.
    /// </summary>
    internal static class AseGameData
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Raised when the way the generated data is built changes, so files made by an older version are made again.
        private const string GeneratorVersion = "1";

        public const string BaseLabel = "Game files";
        private const string BaseFileName = "ASE_generated.gamedata";
        private const string BaseStampName = "ASE_generated.build";

        // the same folders GameData uses (the data folder's GameData folder, and the program's own)
        private static string UserFolder => Path.Combine(Config.Default.DataDir ?? string.Empty, Config.Default.GameDataDir);
        private static string MainFolder => Path.Combine(Environment.CurrentDirectory, Config.Default.GameDataDir);

        private static string ModsRelativePath => Config.Default.ServerModsRelativePath;

        // ---------- entry point ----------

        public static void GenerateFromAnyInstalledServer()
        {
            try
            {
                Directory.CreateDirectory(UserFolder);
                AseCustomMaps.Migrate();

                var gotBase = false;
                foreach (var dir in InstalledServerDirectories())
                {
                    if (!gotBase && GenerateFromServerFiles(dir) >= 0)
                        gotBase = true;
                    GenerateForInstalledMods(dir);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(GenerateFromAnyInstalledServer)} - {ex.Message}");
            }
        }

        private static List<string> InstalledServerDirectories()
        {
            var dirs = new List<string>();
            try
            {
                var servers = Path.Combine(Config.Default.DataDir ?? string.Empty, Config.Default.ServersInstallDir ?? "Servers");
                if (Directory.Exists(servers))
                    dirs.AddRange(Directory.GetDirectories(servers));

                var profiles = Config.Default.ConfigDirectory;
                if (!string.IsNullOrWhiteSpace(profiles) && Directory.Exists(profiles))
                {
                    foreach (var profile in Directory.GetFiles(profiles, "*" + Config.Default.ProfileExtension))
                    {
                        var match = Regex.Match(File.ReadAllText(profile), "\"InstallDirectory\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (match.Success)
                            dirs.Add(Regex.Unescape(match.Groups[1].Value));
                    }
                }
            }
            catch
            {
            }

            return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---------- the base game ----------

        /// <summary>
        /// Writes ASE_generated.gamedata: the base game's items (the numbered master item list, AseItemIds.json), engrams, creatures, spawn
        /// containers and supply crates the built-in data does not have. Returns the number of entries written, or -1 if nothing was done.
        /// </summary>
        public static int GenerateFromServerFiles(string installDirectory)
        {
            try
            {
                var content = Path.Combine(installDirectory ?? string.Empty, "ShooterGame", "Content");
                if (!Directory.Exists(content))
                    return -1;

                var exe = Path.Combine(installDirectory, Config.Default.ServerBinaryRelativePath, Config.Default.ServerExe ?? "ShooterGameServer.exe");
                var build = File.Exists(exe) ? new FileInfo(exe).Length + "-" + new FileInfo(exe).LastWriteTimeUtc.Ticks : "unknown";
                var itemFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AseItemIds.json");
                var itemStamp = File.Exists(itemFile) ? new FileInfo(itemFile).LastWriteTimeUtc.Ticks.ToString() : "0";

                var file = Path.Combine(UserFolder, BaseFileName);
                var stamp = Path.Combine(UserFolder, BaseStampName);
                var stampText = build + "|" + itemStamp + "|" + GeneratorVersion;
                if (File.Exists(file) && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == stampText)
                    return -1;

                // the base game's asset names (everything below Content except the mods)
                var modsRoot = Path.Combine(content, "Mods") + Path.DirectorySeparatorChar;
                var assets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFiles(content, "*.uasset", SearchOption.AllDirectories))
                {
                    if (path.StartsWith(modsRoot, StringComparison.OrdinalIgnoreCase) || IsDevelopmentPath(path))
                        continue;
                    assets.Add(Path.GetFileNameWithoutExtension(path));
                }

                if (assets.Count == 0)
                    return -1;

                var known = KnownClasses();
                var root = BuildGameData(assets, BaseLabel, known, out var total, masterItems: LoadMasterItemClasses());

                File.WriteAllText(file, root.ToString(Formatting.Indented));
                File.WriteAllText(stamp, stampText);
                Logger.Info($"Generated game data from the server's game files: {total} new entries -> {file} (used the next time the manager starts).");
                return total;
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(GenerateFromServerFiles)} - {ex.Message}");
                return -1;
            }
        }

        // test, development and example content of the game is not part of the lists
        private static bool IsDevelopmentPath(string path)
        {
            var p = path.Replace('\\', '/');
            return p.IndexOf("/Test/", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("/Dev/", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("/Debug", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("/Cheat", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("/ExampleContent/", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("/Cinematic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // the classes of the game's master item list (read from a real server): these are exactly the items the game knows
        private static HashSet<string> LoadMasterItemClasses()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var item in AseItemIds.Load())
                    set.Add(item.ClassName);
            }
            catch
            {
            }
            return set;
        }

        // ---------- mods ----------

        /// <summary>Builds a game data file for every mod installed in the server. Returns the number of files written.</summary>
        public static int GenerateForInstalledMods(string installDirectory)
        {
            var written = 0;
            try
            {
                var modsRoot = Path.Combine(installDirectory ?? string.Empty, ModsRelativePath);
                if (!Directory.Exists(modsRoot))
                    return 0;

                HashSet<string> known = null;
                foreach (var modDir in Directory.GetDirectories(modsRoot))
                {
                    var modId = Path.GetFileName(modDir);
                    if (!Regex.IsMatch(modId, @"^\d+$"))
                        continue;

                    var modFile = modDir + ".mod";
                    var file = Path.Combine(UserFolder, $"Mod_{modId}_generated{Config.Default.GameDataExtension}");
                    var stampFile = Path.Combine(UserFolder, $"Mod_{modId}_generated.build");
                    var stampText = ModStamp(modFile, modDir) + "|" + GeneratorVersion;
                    if (File.Exists(file) && File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stampText)
                        continue;

                    var assets = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var path in Directory.EnumerateFiles(modDir, "*.uasset", SearchOption.AllDirectories))
                        assets.Add(Path.GetFileNameWithoutExtension(path));
                    if (assets.Count == 0)
                        continue;

                    var friendly = ReadModName(modFile) ?? modId;
                    known = known ?? KnownClasses(includeGenerated: true);
                    var root = BuildGameData(assets, friendly, known, out var total, masterItems: null);

                    File.WriteAllText(file, root.ToString(Formatting.Indented));
                    File.WriteAllText(stampFile, stampText);
                    Logger.Info($"Generated game data for mod '{friendly}' ({modId}): {total} entries -> {file} (used the next time the manager starts).");
                    written++;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(GenerateForInstalledMods)} - {ex.Message}");
            }
            return written;
        }

        // the mod changed when its .mod file or the newest file in its folder did
        private static string ModStamp(string modFile, string modDir)
        {
            long newest = 0;
            try
            {
                if (File.Exists(modFile))
                    newest = Math.Max(newest, new FileInfo(modFile).LastWriteTimeUtc.Ticks);
                newest = Math.Max(newest, new DirectoryInfo(modDir).LastWriteTimeUtc.Ticks);
            }
            catch
            {
            }
            return newest.ToString();
        }

        /// <summary>The name inside a mod's .mod file: an int64 id, then the name as an Unreal string (int32 length, characters, 0).</summary>
        public static string ReadModName(string modFile)
        {
            try
            {
                if (!File.Exists(modFile))
                    return null;

                using (var reader = new BinaryReader(File.OpenRead(modFile)))
                {
                    reader.ReadInt64();
                    var length = reader.ReadInt32();
                    if (length <= 1 || length > 512)
                        return null;
                    var bytes = reader.ReadBytes(length);
                    var name = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
                    return name.Length == 0 ? null : name;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------- building the file ----------

        private static JObject BuildGameData(IEnumerable<string> assetNames, string label, HashSet<string> known, out int totalEntries, HashSet<string> masterItems)
        {
            var assets = assetNames as ICollection<string> ?? assetNames.ToList();
            var root = new JObject
            {
                ["Application"] = Config.Default.GameDataApplication,
                ["Version"] = "1.0.0",
                ["Created"] = DateTime.UtcNow.ToString("o"),
            };

            var total = 0;
            void Section(string name, IEnumerable<string> classNames, Func<string, JObject> make)
            {
                var array = new JArray();
                foreach (var className in classNames.Distinct().OrderBy(c => c, StringComparer.Ordinal))
                {
                    if (known.Contains(className))
                        continue;
                    var entry = make(className);
                    entry["ClassName"] = className;
                    entry["Mod"] = label;
                    array.Add(entry);
                }
                total += array.Count;
                root[name] = array;
            }

            IEnumerable<string> Classes(Func<string, bool> include) => assets.Where(include).Select(a => a + "_C");

            // the base game's items are exactly the master item list; a mod's are the item assets in its folder
            var itemClasses = masterItems != null && masterItems.Count > 0 ? masterItems.Where(c => IsItemAsset(c.EndsWith("_C", StringComparison.Ordinal) ? c.Substring(0, c.Length - 2) : c)) : Classes(IsItemAsset);

            Section("Items", itemClasses, c => new JObject { ["Category"] = ItemCategory(c), ["IsHarvestable"] = c.StartsWith("PrimalItemResource", StringComparison.Ordinal), ["Description"] = Describe(c, "PrimalItem") });
            Section("Engrams", Classes(IsEngramAsset), c => new JObject { ["Level"] = 0, ["Points"] = 0, ["IsTekGram"] = c.IndexOf("Tek", StringComparison.Ordinal) >= 0, ["Description"] = Describe(c, "EngramEntry") });
            Section("Creatures", Classes(x => IsCreatureAsset(x, strict: masterItems != null)), c => new JObject { ["NameTag"] = NameTag(c), ["IsSpawnable"] = true, ["IsTameable"] = "True", ["IsBreedingable"] = "False", ["Description"] = Describe(c, null) });
            Section("MapSpawners", Classes(a => a.StartsWith("DinoSpawnEntries", StringComparison.Ordinal) || a.StartsWith("NPCZoneManager", StringComparison.Ordinal)), c => new JObject { ["Description"] = Describe(c, "DinoSpawnEntries") });
            Section("SupplyCrates", Classes(IsSupplyCrateAsset), c => new JObject { ["Description"] = Describe(c, null) });

            totalEntries = total;
            return root;
        }

        private static HashSet<string> KnownClasses(bool includeGenerated = false)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var file in Directory.GetFiles(MainFolder, "*" + Config.Default.GameDataExtension))
                    AddClasses(set, file);

                if (includeGenerated)
                {
                    var generated = Path.Combine(UserFolder, BaseFileName);
                    if (File.Exists(generated))
                        AddClasses(set, generated);
                }
            }
            catch
            {
            }
            return set;
        }

        private static void AddClasses(HashSet<string> set, string file)
        {
            try
            {
                var data = JObject.Parse(File.ReadAllText(file));
                foreach (var section in new[] { "Items", "Engrams", "Creatures", "MapSpawners", "SupplyCrates", "Inventories" })
                    if (data[section] is JArray array)
                        foreach (var e in array)
                            set.Add((string)e["ClassName"]);
            }
            catch
            {
            }
        }

        // ---------- what an asset name stands for ----------

        private static bool IsItemAsset(string a)
        {
            if (!a.StartsWith("PrimalItem", StringComparison.Ordinal))
                return false;

            // skins, cosmetics and abstract bases are not things to put in an override list
            if (a.StartsWith("PrimalItemSkin", StringComparison.Ordinal) || a.StartsWith("PrimalItemStructureSkin", StringComparison.Ordinal) || a.StartsWith("PrimalItemCostume", StringComparison.Ordinal))
                return false;
            if (a == "PrimalItem" || a.EndsWith("Generic", StringComparison.Ordinal) || a.EndsWith("Base", StringComparison.Ordinal) || a.IndexOf("Cosmetic", StringComparison.Ordinal) >= 0 || a.IndexOf("Emote", StringComparison.Ordinal) >= 0)
                return false;

            return true;
        }

        private static bool IsEngramAsset(string a)
            => a.StartsWith("EngramEntry", StringComparison.Ordinal) && a.IndexOf("AAA_", StringComparison.Ordinal) < 0 && a.IndexOf("Unlocked", StringComparison.Ordinal) < 0 && a.IndexOf("Cosmetic", StringComparison.Ordinal) < 0;

        // creatures: Name_Character_BP. In the base game only the plain ones (no event, test or tame variants: Name_Character_BP_STA and the like);
        // a mod's creatures often carry a tag after BP, so those are allowed there. No components, skins or alphas.
        private static bool IsCreatureAsset(string a, bool strict)
            => Regex.IsMatch(a, strict ? @"^[A-Za-z0-9]+(_[A-Za-z0-9]+)*_Character_BP(_Aberrant)?$" : @"^[A-Za-z0-9]+(_[A-Za-z0-9]+)*_Character_BP(_[A-Za-z0-9]+)?$")
               && !a.StartsWith("ALPHA_", StringComparison.Ordinal)
               && !a.StartsWith("DinoCharacterStatusComponent", StringComparison.Ordinal)
               && !Regex.IsMatch(a, @"Anim|Rig|Mesh|Mat|Skel|Sound|Cue|Montage|Component|_Skin|Cosmetic");

        private static bool IsSupplyCrateAsset(string a)
            => Regex.IsMatch(a, @"^(SupplyCrate|SupplyCreate)_[A-Za-z0-9_]+$") && !Regex.IsMatch(a, @"Anim|Rig|Idle|_BC$|_N$|_M$|Mesh|Mat|Skel|Sound|Cue|Montage");

        private static string ItemCategory(string c)
        {
            if (c.StartsWith("PrimalItemAmmo", StringComparison.Ordinal)) return "Ammunition";
            if (c.StartsWith("PrimalItemArmor", StringComparison.Ordinal)) return c.IndexOf("Saddle", StringComparison.Ordinal) >= 0 ? "Saddles" : "Armor";
            if (c.StartsWith("PrimalItemConsumable_Egg", StringComparison.Ordinal)) return "Eggs";
            if (c.StartsWith("PrimalItemConsumable_Kibble", StringComparison.Ordinal)) return "Kibble";
            if (c.StartsWith("PrimalItemConsumable_Seed", StringComparison.Ordinal)) return "Seeds";
            if (c.StartsWith("PrimalItemConsumable", StringComparison.Ordinal)) return "Consumables";
            if (c.StartsWith("PrimalItemResource", StringComparison.Ordinal)) return "Resources";
            if (c.StartsWith("PrimalItemStructure", StringComparison.Ordinal)) return "Structures";
            if (c.StartsWith("PrimalItemDye", StringComparison.Ordinal)) return "Dye";
            if (c.StartsWith("PrimalItemArtifact", StringComparison.Ordinal)) return "Artifacts";
            if (c.StartsWith("PrimalItemTrophy", StringComparison.Ordinal)) return "Trophy";
            if (c.StartsWith("PrimalItemWeaponAttachment", StringComparison.Ordinal)) return "Attachments";
            return c.StartsWith("PrimalItem_", StringComparison.Ordinal) ? "Weapons" : "Consumables";
        }

        // PrimalItemConsumable_Berry_Amarberry_C -> "Consumable Berry Amarberry"; keeps the class readable when no display name exists
        private static string Describe(string className, string prefix)
        {
            var text = Regex.Replace(className, @"_C$", string.Empty);
            if (!string.IsNullOrEmpty(prefix) && text.StartsWith(prefix, StringComparison.Ordinal))
                text = text.Substring(prefix.Length);
            text = Regex.Replace(text, @"_Character_BP|_Character|_Char_BP|_BP", " ");
            text = Regex.Replace(text, @"([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ');
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string NameTag(string className)
        {
            var i = className.IndexOf("_Char", StringComparison.Ordinal);
            return i > 0 ? className.Substring(0, i) : className.Replace("_C", string.Empty);
        }

        // ---------- names in the Game Data window ----------

        /// <summary>
        /// The name to show for a game data file in the list: the mod's own name for Mod_&lt;id&gt;_generated, the custom maps' names, and a plain
        /// description for the base game's generated file. Anything else keeps its file name.
        /// </summary>
        public static string DisplayNameForFile(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file ?? string.Empty);
            try
            {
                if (name.StartsWith(AseCustomMaps.FilePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var description = (JObject.Parse(File.ReadAllText(file))["GameMaps"] as JArray)?.Select(e => (string)e["Description"]).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
                    return string.IsNullOrWhiteSpace(description) ? name.Substring(AseCustomMaps.FilePrefix.Length) + " (custom map)" : description + " (custom map)";
                }

                if (string.Equals(name, "CustomMaps", StringComparison.OrdinalIgnoreCase))
                {
                    var maps = AseCustomMaps.Load().Select(m => m.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                    return maps.Count == 0 ? "Custom maps" : "Custom maps: " + string.Join(", ", maps.Take(4)) + (maps.Count > 4 ? ", ..." : string.Empty);
                }

                if (string.Equals(name, Path.GetFileNameWithoutExtension(BaseFileName), StringComparison.OrdinalIgnoreCase))
                    return "Game files (new in this server build, generated)";

                var mod = Regex.Match(name, @"^Mod_(\d+)_generated$", RegexOptions.IgnoreCase);
                if (mod.Success)
                {
                    var friendly = ModNameFromFile(file);
                    return string.IsNullOrWhiteSpace(friendly) || friendly == mod.Groups[1].Value ? name : friendly + " (mod, generated)";
                }
            }
            catch
            {
            }
            return name;
        }

        // the mod's name is written into every entry of its generated file ("Mod")
        private static string ModNameFromFile(string file)
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(file));
                foreach (var section in new[] { "Items", "Engrams", "Creatures", "MapSpawners", "SupplyCrates" })
                {
                    if (root[section] is JArray array)
                    {
                        var label = array.Select(e => (string)e["Mod"]).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                        if (label != null)
                            return label;
                    }
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
