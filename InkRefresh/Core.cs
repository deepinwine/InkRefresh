using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InkRefresh
{
    /// <summary>解析 "Ctrl+Alt+F9" / "Alt+C" / "F5" 形式的快捷键字符串。</summary>
    public static class HotkeyParser
    {
        public static bool TryParse(string hotkey, out ushort[] keys)
        {
            keys = null;
            if (string.IsNullOrEmpty(hotkey)) return false;
            string[] parts = hotkey.Split('+');
            var list = new List<ushort>();
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                ushort? vk = ParseKey(p);
                if (vk == null) return false;
                list.Add(vk.Value);
            }
            if (list.Count == 0) return false;
            keys = list.ToArray();
            return true;
        }

        /// <summary>单个键名 → 虚拟键码; 不认识返回 null。</summary>
        public static ushort? ParseKey(string name)
        {
            string n = (name ?? "").Trim().ToUpperInvariant();
            if (n.Length == 0) return null;
            if (n == "CTRL" || n == "CONTROL") return 0x11;
            if (n == "ALT" || n == "MENU") return 0x12;
            if (n == "SHIFT") return 0x10;
            if (n == "WIN" || n == "WINDOWS") return 0x5B;
            if (n.Length >= 2 && n.Length <= 3 && n[0] == 'F')
            {
                int k;
                if (int.TryParse(n.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out k)
                    && k >= 1 && k <= 24)
                    return (ushort)(0x6F + k);
                return null;
            }
            if (n.Length == 1)
            {
                char c = n[0];
                if (c >= 'A' && c <= 'Z') return (ushort)c;
                if (c >= '0' && c <= '9') return (ushort)c;
            }
            return null;
        }

        /// <summary>拆成 RegisterHotKey 需要的 修饰键标记 + 主键。不能全是修饰键。</summary>
        public static bool ToRegisterHotkey(string hotkey, out uint modifiers, out ushort vk)
        {
            modifiers = 0;
            vk = 0;
            ushort[] keys;
            if (!TryParse(hotkey, out keys) || keys.Length == 0) return false;
            ushort main = 0;
            foreach (ushort k in keys)
            {
                if (k == 0x11) modifiers |= 0x0002;      // MOD_CONTROL
                else if (k == 0x12) modifiers |= 0x0001; // MOD_ALT
                else if (k == 0x10) modifiers |= 0x0004; // MOD_SHIFT
                else if (k == 0x5B) modifiers |= 0x0008; // MOD_WIN
                else
                {
                    if (main != 0) return false;
                    main = k;
                }
            }
            if (main == 0) return false;
            modifiers |= 0x4000; // MOD_NOREPEAT
            vk = main;
            return true;
        }
    }

    /// <summary>极简 key=value 配置文件读写。</summary>
    public sealed class IniFile
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IniFile(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        _map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
        }

        public string Get(string key, string def)
        {
            string v;
            return _map.TryGetValue(key, out v) ? v : def;
        }

        public int GetInt(string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(Get(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return def;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public bool GetBool(string key, bool def)
        {
            string v = Get(key, null);
            if (v == null) return def;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        public void Set(string key, string value) { _map[key] = value; }

        public bool Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# InkRefresh settings (auto-saved)");
                foreach (KeyValuePair<string, string> kv in _map)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>应用设置(带默认值与容错)。</summary>
    public sealed class AppSettings
    {
        public const string MethodHotkey = "hotkey";
        public const string MethodFlash = "flash";
        public const string MethodBoth = "both";

        public int IntervalSec = 300;
        public string Hotkey = "Alt+C";
        public string Method = MethodHotkey;
        public bool RefreshOnStart = true;
        public bool StartMinimized = false;
        public bool ManualHotkeyEnabled = true;
        public string ManualHotkey = "Ctrl+Alt+R";
        public int FlashMs = 400;

        public static AppSettings Load(string iniPath)
        {
            var s = new AppSettings();
            var ini = new IniFile(iniPath);

            s.IntervalSec = ini.GetInt("interval", 300, 1, 86400);

            string hk = ini.Get("hotkey", "Alt+C");
            ushort[] tmp;
            if (HotkeyParser.TryParse(hk, out tmp)) s.Hotkey = hk;

            string m = ini.Get("method", MethodHotkey);
            if (m != MethodHotkey && m != MethodFlash && m != MethodBoth) m = MethodHotkey;
            s.Method = m;

            s.RefreshOnStart = ini.GetBool("refresh_on_start", true);
            s.StartMinimized = ini.GetBool("start_minimized", false);
            s.ManualHotkeyEnabled = ini.GetBool("manual_hotkey_enabled", true);

            string mh = ini.Get("manual_hotkey", "Ctrl+Alt+R");
            uint mods;
            ushort vk;
            if (HotkeyParser.ToRegisterHotkey(mh, out mods, out vk)) s.ManualHotkey = mh;

            s.FlashMs = ini.GetInt("flash_ms", 400, 50, 5000);
            return s;
        }

        public void Save(string iniPath)
        {
            var ini = new IniFile(iniPath);
            ini.Set("interval", IntervalSec.ToString(CultureInfo.InvariantCulture));
            ini.Set("hotkey", Hotkey);
            ini.Set("method", Method);
            ini.Set("refresh_on_start", RefreshOnStart ? "1" : "0");
            ini.Set("start_minimized", StartMinimized ? "1" : "0");
            ini.Set("manual_hotkey_enabled", ManualHotkeyEnabled ? "1" : "0");
            ini.Set("manual_hotkey", ManualHotkey);
            ini.Set("flash_ms", FlashMs.ToString(CultureInfo.InvariantCulture));
            ini.Save();
        }
    }
}
