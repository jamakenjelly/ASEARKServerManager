using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerManagerTool.Common.Serialization
{
    public class IniSection
    {
        public IniSection()
        {
            SectionName = string.Empty;
            Keys = new List<IniKey>();
            _keyIndex = new Dictionary<string, IniKey>(StringComparer.OrdinalIgnoreCase);
        }

        public string SectionName;
        public List<IniKey> Keys;

        // O(1) lookup index kept in sync with Keys.
        private readonly Dictionary<string, IniKey> _keyIndex;

        public IniKey AddKey(string keyName, string keyValue)
        {
            var key = new IniKey() { KeyName = keyName, KeyValue = keyValue };
            Keys.Add(key);
            IndexKey(key);
            return key;
        }

        public IniKey AddKey(string keyValuePair)
        {
            var parts = keyValuePair?.Split(new[] { '=' }, 2) ?? new string[1];

            if (string.IsNullOrWhiteSpace(parts[0]))
                return null;

            var key = new IniKey() { KeyName = parts[0] };
            if (parts.Length > 1)
                key.KeyValue = parts[1];
            Keys.Add(key);
            IndexKey(key);
            return key;
        }

        private void IndexKey(IniKey key)
        {
            // Preserve original semantics: GetKey used to return the FIRST matching
            // key when duplicates existed, so only index the first occurrence.
            if (!_keyIndex.ContainsKey(key.KeyName))
                _keyIndex[key.KeyName] = key;
        }

        public IniKey GetKey(string keyName)
        {
            if (keyName == null)
                return null;

            return _keyIndex.TryGetValue(keyName, out var key) ? key : null;
        }

        public IEnumerable<string> KeysToStringEnumerable()
        {
            return Keys.Select(k => k.ToString());
        }

        public void RemoveKey(string keyName)
        {
            var key = GetKey(keyName);
            RemoveKey(key);
        }

        public void RemoveKey(IniKey key)
        {
            if (key == null)
                return;

            if (Keys.Remove(key))
            {
                if (_keyIndex.TryGetValue(key.KeyName, out var indexed) && ReferenceEquals(indexed, key))
                {
                    _keyIndex.Remove(key.KeyName);
                    var next = Keys.FirstOrDefault(k => k.KeyName.Equals(key.KeyName, StringComparison.OrdinalIgnoreCase));
                    if (next != null)
                        _keyIndex[key.KeyName] = next;
                }
            }
        }

        public override string ToString()
        {
            return $"[{SectionName}]";
        }
    }
}
