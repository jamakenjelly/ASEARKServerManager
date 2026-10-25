using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using ServerManagerTool.Lib.ViewModel;

namespace ServerManagerTool.Lib
{
    /// <summary>
    /// The item ID numbers of ARK: Survival Evolved: the position of an item in the game's master item list, which is the number the
    /// Game.ini setting ExcludeItemIndices takes. The list comes from AseItemIds.json next to the program (written from a real server by
    /// the ItemIdDump plugin). Items from mods have no number here and cannot be excluded this way.
    /// </summary>
    public static class AseItemIds
    {
        private static List<ExcludeItemChoice> _all;

        public static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AseItemIds.json");

        /// <summary>Every item with an ID, in ID order. Empty when the data file is missing. Each call returns fresh objects (tick state is per server).</summary>
        public static List<ExcludeItemChoice> Load()
        {
            if (_all == null)
            {
                var entries = new List<ExcludeItemChoice>();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var root = JToken.Parse(File.ReadAllText(FilePath));
                        var rows = root is JObject obj ? (JArray)obj["items"] : (JArray)root;
                        foreach (var row in rows)
                        {
                            long id;
                            string className;
                            if (row is JObject item)
                            {
                                id = (long)item["id"];
                                className = (string)item["class"];
                            }
                            else
                            {
                                id = (long)row[0];
                                className = (string)row[1];
                            }

                            if (id < 0 || string.IsNullOrWhiteSpace(className))
                                continue;

                            entries.Add(new ExcludeItemChoice(id, className, NameFor(className)));
                        }
                    }
                }
                catch
                {
                }
                _all = entries.OrderBy(e => e.Id).ToList();
            }

            return _all.Select(e => new ExcludeItemChoice(e.Id, e.ClassName, e.DisplayName)).ToList();
        }

        // the manager's own name for the item when it has one, otherwise a readable form of the class name
        private static string NameFor(string className)
        {
            string name = null;
            try { name = GameData.FriendlyItemNameForClass(className, true); } catch { }
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            var text = Regex.Replace(className, @"_C$", string.Empty);
            text = Regex.Replace(text, @"^PrimalItem(Resource|Consumable|Structure|Ammo|Armor|Dye|Artifact|Trophy)?_?", m => m.Groups[1].Success ? m.Groups[1].Value + " " : string.Empty);
            text = Regex.Replace(text, @"([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ');
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length == 0 ? className : text;
        }
    }
}
