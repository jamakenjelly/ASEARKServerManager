using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ServerManagerTool.Lib
{
    /// <summary>
    /// Maps the user added by hand to the map list. Each map has its own game data file, CustomMap_&lt;class name&gt;.gamedata, in the data folder's
    /// GameData folder, which the manager reads at start-up like every other game data file, so a map added here is still in the list after a restart.
    /// Earlier versions kept all of them together in CustomMaps.gamedata; that file is split into one file per map the first time it is found.
    /// </summary>
    public static class AseCustomMaps
    {
        public const string FilePrefix = "CustomMap_";
        private const string LegacyFileName = "CustomMaps.gamedata";

        private static string LegacyFilePath => Path.Combine(GameData.UserDataFolder, LegacyFileName);

        private static string FileFor(string className)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var safe = new string((className ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return Path.Combine(GameData.UserDataFolder, FilePrefix + safe + Config.Default.GameDataExtension);
        }

        private static IEnumerable<string> MapFiles()
            => Directory.Exists(GameData.UserDataFolder)
                ? Directory.GetFiles(GameData.UserDataFolder, FilePrefix + "*" + Config.Default.GameDataExtension)
                : Enumerable.Empty<string>();

        private static void WriteMap(string className, string description)
        {
            Directory.CreateDirectory(GameData.UserDataFolder);

            var root = new JObject
            {
                ["Application"] = Config.Default.GameDataApplication,
                ["Version"] = "1.0.0",
                ["GameMaps"] = new JArray(new JObject { ["IsSotF"] = false, ["ClassName"] = className, ["Description"] = description, ["Mod"] = "Custom" }),
            };
            File.WriteAllText(FileFor(className), root.ToString(Newtonsoft.Json.Formatting.Indented));
        }

        /// <summary>Splits the old all-in-one CustomMaps.gamedata into one file per map. Safe to call any number of times.</summary>
        public static void Migrate()
        {
            try
            {
                var legacy = LegacyFilePath;
                if (!File.Exists(legacy))
                    return;

                var root = JObject.Parse(File.ReadAllText(legacy));
                if (root["GameMaps"] is JArray array)
                {
                    foreach (var entry in array)
                    {
                        var className = (string)entry["ClassName"];
                        if (!string.IsNullOrWhiteSpace(className) && !File.Exists(FileFor(className)))
                            WriteMap(className, (string)entry["Description"] ?? className);
                    }
                }

                File.Delete(legacy);
            }
            catch
            {
            }
        }

        public static List<KeyValuePair<string, string>> Load()
        {
            var maps = new List<KeyValuePair<string, string>>();
            try
            {
                Migrate();

                foreach (var file in MapFiles())
                {
                    var root = JObject.Parse(File.ReadAllText(file));
                    if (root["GameMaps"] is JArray array)
                        foreach (var entry in array)
                            maps.Add(new KeyValuePair<string, string>((string)entry["ClassName"], (string)entry["Description"]));
                }
            }
            catch
            {
            }
            return maps;
        }

        public static bool IsCustom(string className)
            => Load().Any(m => string.Equals(m.Key, className, StringComparison.OrdinalIgnoreCase));

        public static void Add(string className, string description)
            => WriteMap(className, string.IsNullOrWhiteSpace(description) ? className : description.Trim());

        public static bool Remove(string className)
        {
            Migrate();

            var file = FileFor(className);
            if (!File.Exists(file))
                return false;

            File.Delete(file);
            return true;
        }
    }
}
