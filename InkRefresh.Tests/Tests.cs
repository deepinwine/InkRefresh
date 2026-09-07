using System;
using System.IO;
using InkRefresh;

internal static class Tests
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) Console.WriteLine("PASS  " + name + "  " + detail);
        else { Console.WriteLine("FAIL  " + name + "  " + detail); _fail++; }
    }

    private static string Join(ushort[] a)
    {
        return a == null ? "<null>" : string.Join(",", a);
    }

    private static void TestHotkeyParse()
    {
        ushort[] k;
        Check("Alt+C", HotkeyParser.TryParse("Alt+C", out k) && Join(k) == "18,67", Join(k));
        Check("Ctrl+Alt+R", HotkeyParser.TryParse("Ctrl+Alt+R", out k) && Join(k) == "17,18,82", Join(k));
        Check("F5", HotkeyParser.TryParse("F5", out k) && Join(k) == "116", Join(k));
        Check("lower ctrl+shift+f9", HotkeyParser.TryParse("ctrl + shift + f9", out k) && Join(k) == "17,16,120", Join(k));
        Check("digit 7", HotkeyParser.TryParse("7", out k) && Join(k) == "55", Join(k));
        Check("plain c", HotkeyParser.TryParse("c", out k) && Join(k) == "67", Join(k));
        Check("win+d", HotkeyParser.TryParse("Win+D", out k) && Join(k) == "91,68", Join(k));
        Check("empty", !HotkeyParser.TryParse("", out k), Join(k));
        Check("null", !HotkeyParser.TryParse(null, out k), Join(k));
        Check("garbage Foo+Bar", !HotkeyParser.TryParse("Foo+Bar", out k), Join(k));
        Check("F99 rejected", !HotkeyParser.TryParse("F99", out k), Join(k));
        Check("F24 ok", HotkeyParser.TryParse("F24", out k) && Join(k) == "135", Join(k));
        Check("nonascii rejected", !HotkeyParser.TryParse("Alt+刷", out k), Join(k));
    }

    private static void TestRegisterHotkey()
    {
        uint mods; ushort vk;

        bool ok = HotkeyParser.ToRegisterHotkey("Ctrl+Alt+R", out mods, out vk);
        Check("reg Ctrl+Alt+R", ok && mods == (0x0002 | 0x0001 | 0x4000) && vk == 82,
            "mods=0x" + mods.ToString("X") + " vk=" + vk);

        ok = HotkeyParser.ToRegisterHotkey("Alt+C", out mods, out vk);
        Check("reg Alt+C", ok && mods == (0x0001 | 0x4000) && vk == 67,
            "mods=0x" + mods.ToString("X") + " vk=" + vk);

        ok = HotkeyParser.ToRegisterHotkey("F5", out mods, out vk);
        Check("reg F5 alone", ok && mods == 0x4000 && vk == 116,
            "mods=0x" + mods.ToString("X") + " vk=" + vk);

        ok = HotkeyParser.ToRegisterHotkey("Win+Shift+F9", out mods, out vk);
        Check("reg Win+Shift+F9", ok && mods == (0x0008 | 0x0004 | 0x4000) && vk == 120,
            "mods=0x" + mods.ToString("X") + " vk=" + vk);

        ok = HotkeyParser.ToRegisterHotkey("Ctrl", out mods, out vk);
        Check("reg modifier-only rejected", !ok, "mods=0x" + mods.ToString("X"));

        ok = HotkeyParser.ToRegisterHotkey("A+B", out mods, out vk);
        Check("reg two-mains rejected", !ok, "vk=" + vk);

        ok = HotkeyParser.ToRegisterHotkey("", out mods, out vk);
        Check("reg empty rejected", !ok, "");
    }

    private static void TestIni()
    {
        string dir = Path.Combine(Path.GetTempPath(), "inkrefresh-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "settings.ini");

        File.WriteAllLines(ini, new[]
        {
            "# comment",
            "config_version=2",
            "interval=42",
            "hotkey=Ctrl+Alt+F9",
            "method=both",
            "refresh_on_start=0",
            "manual_hotkey=Win+Shift+F9",
            "flash_ms=900",
            "garbage line without eq",
            "; semicolon comment"
        });
        AppSettings s = AppSettings.Load(ini);
        Check("ini interval", s.IntervalSec == 42, s.IntervalSec.ToString());
        Check("ini hotkey", s.Hotkey == "Ctrl+Alt+F9", s.Hotkey);
        Check("ini method", s.Method == AppSettings.MethodBoth, s.Method);
        Check("ini ros", !s.RefreshOnStart, s.RefreshOnStart.ToString());
        Check("ini manual", s.ManualHotkey == "Win+Shift+F9", s.ManualHotkey);
        Check("ini flash_ms", s.FlashMs == 900, s.FlashMs.ToString());

        // garbage config -> safe defaults
        File.WriteAllLines(ini, new[]
        {
            "interval=abc",
            "hotkey=!!",
            "method=zzz",
            "refresh_on_start=9",
            "flash_ms=1"
        });
        s = AppSettings.Load(ini);
        Check("bad interval", s.IntervalSec == 300, s.IntervalSec.ToString());
        Check("bad hotkey", s.Hotkey == "Alt+C", s.Hotkey);
        Check("bad method", s.Method == AppSettings.MethodHotkey, s.Method);
        Check("bad ros", s.RefreshOnStart, s.RefreshOnStart.ToString());
        Check("low flash_ms clamps to 50", s.FlashMs == 50, s.FlashMs.ToString());

        File.WriteAllLines(ini, new[] { "flash_ms=abc" });
        s = AppSettings.Load(ini);
        Check("non-numeric flash_ms", s.FlashMs == 400, s.FlashMs.ToString());

        // clamping
        File.WriteAllLines(ini, new[] { "interval=999999", "flash_ms=99999" });
        s = AppSettings.Load(ini);
        Check("clamp interval", s.IntervalSec == 86400, s.IntervalSec.ToString());
        Check("clamp flash_ms", s.FlashMs == 5000, s.FlashMs.ToString());

        // missing file -> defaults
        File.Delete(ini);
        s = AppSettings.Load(ini);
        Check("missing file defaults",
            s.IntervalSec == 300 && s.Hotkey == "Alt+C" && s.Method == AppSettings.MethodHotkey
            && s.ManualHotkey == "Alt+E"
            && s.RefreshOnStart && s.StartMinimized && s.ManualHotkeyEnabled,
            s.IntervalSec + "/" + s.Hotkey + "/" + s.ManualHotkey);

        // v1.1.4 旧配置(无 config_version) -> 一次性迁移
        File.WriteAllLines(ini, new[]
        {
            "interval=120",
            "hotkey=Alt+E",
            "method=hotkey",
            "refresh_on_start=0",
            "start_minimized=0",
            "manual_hotkey_enabled=0",
            "manual_hotkey=Ctrl+Alt+R"
        });
        s = AppSettings.Load(ini);
        Check("migrate flags all on",
            s.RefreshOnStart && s.StartMinimized && s.ManualHotkeyEnabled, "");
        Check("migrate hotkey Alt+E -> Alt+C", s.Hotkey == "Alt+C", s.Hotkey);
        Check("migrate manual -> Alt+E", s.ManualHotkey == "Alt+E", s.ManualHotkey);
        Check("migrate keeps interval", s.IntervalSec == 120, s.IntervalSec.ToString());
        Check("migrate marks save", s.NeedsUpgradeSave, s.NeedsUpgradeSave.ToString());

        // 用户自定义值在迁移中保留
        File.WriteAllLines(ini, new[]
        {
            "interval=90",
            "hotkey=Ctrl+Alt+F9",
            "manual_hotkey=Ctrl+Shift+F7",
            "refresh_on_start=0"
        });
        s = AppSettings.Load(ini);
        Check("migrate keeps custom hotkeys",
            s.Hotkey == "Ctrl+Alt+F9" && s.ManualHotkey == "Ctrl+Shift+F7",
            s.Hotkey + "/" + s.ManualHotkey);

        // config_version=2 的配置不做迁移
        File.WriteAllLines(ini, new[]
        {
            "config_version=2",
            "refresh_on_start=0",
            "start_minimized=0",
            "manual_hotkey_enabled=0",
            "manual_hotkey=Ctrl+Alt+R"
        });
        s = AppSettings.Load(ini);
        Check("v2 config not migrated",
            !s.RefreshOnStart && !s.StartMinimized && !s.ManualHotkeyEnabled
            && s.ManualHotkey == "Ctrl+Alt+R" && !s.NeedsUpgradeSave,
            s.RefreshOnStart + "/" + s.ManualHotkey);

        // save round-trip
        s.IntervalSec = 77;
        s.Hotkey = "Ctrl+Alt+R";
        s.Method = AppSettings.MethodFlash;
        s.ManualHotkeyEnabled = false;
        s.StartMinimized = true;
        s.Save(ini);
        AppSettings s2 = AppSettings.Load(ini);
        Check("roundtrip interval", s2.IntervalSec == 77, s2.IntervalSec.ToString());
        Check("roundtrip hotkey", s2.Hotkey == "Ctrl+Alt+R", s2.Hotkey);
        Check("roundtrip method", s2.Method == AppSettings.MethodFlash, s2.Method);
        Check("roundtrip manual_enabled", !s2.ManualHotkeyEnabled, s2.ManualHotkeyEnabled.ToString());
        Check("roundtrip start_min", s2.StartMinimized, s2.StartMinimized.ToString());

        Directory.Delete(dir, true);
    }

    private static int Main()
    {
        TestHotkeyParse();
        TestRegisterHotkey();
        TestIni();
        Console.WriteLine();
        if (_fail > 0)
        {
            Console.WriteLine(_fail + " TEST(S) FAILED");
            return 1;
        }
        Console.WriteLine("ALL TESTS PASSED");
        return 0;
    }
}
