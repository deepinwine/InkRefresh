using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace InkRefresh
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, "InkRefresh_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("刷新助手已经在运行了, 请查看任务栏右下角的托盘图标。",
                    "大上墨水屏刷新助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            GC.KeepAlive(mutex);
        }
    }

    /// <summary>SendInput 全局模拟按键。</summary>
    internal static class NativeInput
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static INPUT MakeKey(ushort vk, bool keyUp)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
            return i;
        }

        public static void SendCombo(ushort[] vks, int holdMs)
        {
            var downs = new List<INPUT>();
            var ups = new List<INPUT>();
            foreach (ushort vk in vks) downs.Add(MakeKey(vk, false));
            for (int i = vks.Length - 1; i >= 0; i--) ups.Add(MakeKey(vks[i], true));

            int size = Marshal.SizeOf(typeof(INPUT));
            uint n1 = SendInput((uint)downs.Count, downs.ToArray(), size);
            Thread.Sleep(holdMs);
            uint n2 = SendInput((uint)ups.Count, ups.ToArray(), size);
            if (n1 != (uint)downs.Count || n2 != (uint)ups.Count)
                throw new InvalidOperationException("SendInput 失败, Win32Error=" + Marshal.GetLastWin32Error());
        }
    }

    /// <summary>接收 WM_HOTKEY 的隐藏消息窗口(用于"立即刷新"全局热键)。</summary>
    internal sealed class HotkeyWindow : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modFlags, ushort vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public event Action Triggered;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(int id, uint modFlags, ushort vk)
        {
            Unregister(id);
            return RegisterHotKey(Handle, id, modFlags, vk);
        }

        public void Unregister(int id)
        {
            try { UnregisterHotKey(Handle, id); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                Action handler = Triggered;
                if (id == 1 && handler != null) handler();
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>全屏黑屏重绘(备用刷新方式, 覆盖所有显示器)。</summary>
    internal static class RefreshEngine
    {
        private static Form _flashForm;

        public static void Flash(int ms)
        {
            if (_flashForm != null) return;
            Rectangle vs = SystemInformation.VirtualScreen;

            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.Manual;
            f.Bounds = vs;
            f.ShowInTaskbar = false;
            f.TopMost = true;
            f.BackColor = Color.Black;

            Form captured = f;
            var t = new System.Windows.Forms.Timer { Interval = ms };
            t.Tick += delegate
            {
                t.Dispose();
                if (_flashForm == captured)
                {
                    _flashForm = null;
                    captured.Close();
                    captured.Dispose();
                }
            };
            _flashForm = f;
            f.Show();
            t.Start();
        }
    }

    internal static class Log
    {
        private static readonly object LockObj = new object();
        private static string _path;

        public static void Init(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length > 400)
                    {
                        var keep = new string[200];
                        Array.Copy(lines, lines.Length - 200, keep, 0, 200);
                        File.WriteAllLines(path, keep);
                    }
                }
            }
            catch { }
        }

        public static void Write(string msg)
        {
            try
            {
                lock (LockObj)
                {
                    if (_path == null) return;
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>开机自启: 在"启动"文件夹放/删快捷方式(不写注册表)。</summary>
    internal static class AutostartHelper
    {
        private static string LnkPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "InkRefresh.lnk");
        }

        /// <summary>读取现有快捷方式指向的目标 exe 路径(失败返回 null)。</summary>
        private static string GetLnkTarget(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                return sc.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
            }
            catch { return null; }
        }

        /// <summary>自启有效 = 快捷方式存在且指向当前 exe。旧版本残留的失效快捷方式不算数。</summary>
        public static bool IsEnabled()
        {
            try
            {
                string lnk = LnkPath();
                if (!File.Exists(lnk)) return false;
                string target = GetLnkTarget(lnk);
                if (string.IsNullOrEmpty(target)) return false;
                return string.Equals(target, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Set(bool enable)
        {
            string lnk = LnkPath();
            if (!enable)
            {
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
                return;
            }
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) throw new InvalidOperationException("WScript.Shell 不可用");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type sct = sc.GetType();
                sct.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc,
                    new object[] { Application.ExecutablePath });
                sct.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc,
                    new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                sct.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
            }
            catch (Exception ex)
            {
                Log.Write("autostart set failed: " + ex.Message);
            }
            EnsureStartupApprovedEnabled();
        }

        /// <summary>任务管理器"启动应用"按文件名记忆禁用状态, 重建同名快捷方式不会自动恢复;
        /// 勾选自启时强制写回"已启用"。</summary>
        private static void EnsureStartupApprovedEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Startup", true))
                {
                    key.SetValue("InkRefresh.lnk", new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0 },
                        Microsoft.Win32.RegistryValueKind.Binary);
                }
            }
            catch (Exception ex)
            {
                Log.Write("startupapproved write failed: " + ex.Message);
            }
        }
    }

    /// <summary>主程序: 托盘常驻 + 定时刷新。</summary>
    internal sealed class App : ApplicationContext
    {
        private readonly string _exeIni;
        private readonly string _appDataIni;
        private readonly string _logPath;
        private string _iniPath;
        private AppSettings _settings;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _timer;
        private SettingsForm _form;
        private HotkeyWindow _hotkeyWindow;
        private MenuItem _miPause;
        private MenuItem _miAutostart;
        private bool _paused;
        private int _nextIn;
        private DateTime? _lastRefresh;

        public AppSettings Settings { get { return _settings; } }

        public App()
        {
            string baseDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
            _exeIni = Path.Combine(baseDir, "settings.ini");
            _appDataIni = Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkRefresh"),
                "settings.ini");
            _logPath = Path.Combine(baseDir, "InkRefresh.log");

            Log.Init(_logPath);

            // 写入目标: exe 旁优先, 目录不可写时改用 %APPDATA%\InkRefresh
            _iniPath = IsDirWritable(baseDir) ? _exeIni : _appDataIni;
            // 读取来源: 优先 exe 旁的现有配置(即使现在不可写, 里面的旧值也要继承)
            string loadFrom = File.Exists(_exeIni) ? _exeIni
                : (File.Exists(_appDataIni) ? _appDataIni : _iniPath);
            _settings = AppSettings.Load(loadFrom);
            Log.Write("config: load=" + loadFrom + ", save=" + _iniPath);
            if (!File.Exists(_iniPath)) SaveSettings();

            BuildTray();

            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.Triggered += ManualRefresh;
            ApplyManualHotkey();

            _nextIn = _settings.IntervalSec;
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += TimerTick;
            _timer.Start();

            _form = new SettingsForm(this);

            Log.Write("started, interval=" + _settings.IntervalSec + "s, hotkey=" + _settings.Hotkey
                + ", method=" + _settings.Method);
            Log.Write("autostart enabled=" + AutostartHelper.IsEnabled());

            if (!_settings.StartMinimized) ShowForm();
            if (_settings.RefreshOnStart) DoRefresh("start");
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = LoadAppIcon();
            _tray.Text = "大上墨水屏刷新助手";
            _tray.Visible = true;

            var menu = new ContextMenu();
            menu.MenuItems.Add(new MenuItem("立即刷新", delegate { ManualRefresh(); }));

            _miPause = new MenuItem("暂停自动刷新", delegate { TogglePause(); });
            menu.MenuItems.Add(_miPause);

            menu.MenuItems.Add(new MenuItem("-"));

            _miAutostart = new MenuItem("开机自启动", delegate { ToggleAutostart(); });
            _miAutostart.Checked = AutostartHelper.IsEnabled();
            menu.MenuItems.Add(_miAutostart);

            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(new MenuItem("设置", delegate { ShowForm(); }));
            menu.MenuItems.Add(new MenuItem("退出", delegate { ExitApp(); }));

            _tray.ContextMenu = menu;
            _tray.DoubleClick += delegate { ShowForm(); };
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) return ic;
            }
            catch { }
            return SystemIcons.Application;
        }

        private void TimerTick(object sender, EventArgs e)
        {
            if (!_paused)
            {
                _nextIn--;
                if (_nextIn <= 0) DoRefresh("timer");
            }
            UpdateStatusTexts();
        }

        public void ManualRefresh()
        {
            DoRefresh("manual");
        }

        public void DoRefresh(string source)
        {
            string err = null;
            try
            {
                bool doHotkey = _settings.Method != AppSettings.MethodFlash;
                bool doFlash = _settings.Method != AppSettings.MethodHotkey;

                if (doHotkey)
                {
                    ushort[] keys;
                    if (!HotkeyParser.TryParse(_settings.Hotkey, out keys))
                    {
                        err = "刷新快捷键无法解析: " + _settings.Hotkey;
                    }
                    else
                    {
                        NativeInput.SendCombo(keys, 80);
                    }
                }
                if (err == null && doFlash) RefreshEngine.Flash(_settings.FlashMs);
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            _lastRefresh = DateTime.Now;
            _nextIn = _settings.IntervalSec;

            if (err != null) Log.Write("refresh FAILED (" + source + "): " + err);
            else Log.Write("refresh ok (" + source + ", method=" + _settings.Method + ")");
            UpdateStatusTexts();
        }

        public void TogglePause()
        {
            _paused = !_paused;
            _miPause.Text = _paused ? "继续自动刷新" : "暂停自动刷新";
            if (_form != null) _form.OnPauseChanged(_paused);
            UpdateStatusTexts();
        }

        private void ToggleAutostart()
        {
            bool now = !AutostartHelper.IsEnabled();
            AutostartHelper.Set(now);
            _miAutostart.Checked = AutostartHelper.IsEnabled();
            Log.Write("autostart -> " + (_miAutostart.Checked ? "ON" : "OFF")
                + ", exe=" + Application.ExecutablePath);
        }

        public void ShowForm()
        {
            _form.Show();
            _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        private void ExitApp()
        {
            try { _timer.Stop(); } catch { }
            try { _hotkeyWindow.Unregister(1); } catch { }
            try { _form.SaveAll(false); } catch { }
            try { _form.AllowClose = true; _form.Close(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            ExitThread();
        }

        public void SettingsChanged()
        {
            SaveSettings();
            ApplyManualHotkey();
            UpdateStatusTexts();
        }

        public void SetInterval(int sec)
        {
            _settings.IntervalSec = sec;
            _nextIn = sec;
            SaveSettings();
            UpdateStatusTexts();
        }

        public string IniPathUsed { get { return _iniPath; } }

        /// <summary>集中保存配置: exe 旁写失败自动回退 %APPDATA%, 结果写日志。</summary>
        private bool SaveSettings()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(_iniPath)); } catch { }
            bool ok = _settings.Save(_iniPath);
            if (!ok)
            {
                Log.Write("settings save FAILED at " + _iniPath + ": " + _settings.LastError);
                if (string.Equals(_iniPath, _exeIni, StringComparison.OrdinalIgnoreCase))
                {
                    _iniPath = _appDataIni;
                    try { Directory.CreateDirectory(Path.GetDirectoryName(_iniPath)); } catch { }
                    ok = _settings.Save(_iniPath);
                    Log.Write("fallback save to " + _iniPath + ": " + (ok ? "ok" : "FAILED: " + _settings.LastError));
                }
            }
            return ok;
        }

        /// <summary>保存后重读磁盘, 校验配置是否真正落盘(供[保存配置]按钮反馈)。</summary>
        public bool VerifyPersisted()
        {
            AppSettings disk = AppSettings.Load(_iniPath);
            AppSettings cur = _settings;
            return disk.IntervalSec == cur.IntervalSec
                && string.Equals(disk.Hotkey, cur.Hotkey, StringComparison.OrdinalIgnoreCase)
                && disk.Method == cur.Method
                && disk.RefreshOnStart == cur.RefreshOnStart
                && disk.StartMinimized == cur.StartMinimized
                && disk.ManualHotkeyEnabled == cur.ManualHotkeyEnabled
                && string.Equals(disk.ManualHotkey, cur.ManualHotkey, StringComparison.OrdinalIgnoreCase)
                && disk.FlashMs == cur.FlashMs;
        }

        private static bool IsDirWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".inkrefresh-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private void ApplyManualHotkey()
        {
            _hotkeyWindow.Unregister(1);
            if (!_settings.ManualHotkeyEnabled) return;
            uint mods;
            ushort vk;
            if (HotkeyParser.ToRegisterHotkey(_settings.ManualHotkey, out mods, out vk))
            {
                if (!_hotkeyWindow.Register(1, mods, vk))
                    Log.Write("manual hotkey register failed (组合键可能被占用): " + _settings.ManualHotkey);
            }
        }

        private void UpdateStatusTexts()
        {
            if (_paused)
            {
                _tray.Text = "大上刷新助手 - 已暂停";
            }
            else
            {
                DateTime next = DateTime.Now.AddSeconds(_nextIn);
                _tray.Text = "下次刷新 " + next.ToString("HH:mm:ss") + " (还有" + _nextIn + "秒)";
            }
            if (_form != null) _form.UpdateStatus(BuildStatusText());
        }

        private string BuildStatusText()
        {
            if (_paused) return "已暂停 (托盘菜单或[继续]恢复)";
            string last = _lastRefresh.HasValue ? _lastRefresh.Value.ToString("HH:mm:ss") : "--:--:--";
            DateTime next = DateTime.Now.AddSeconds(_nextIn);
            return "上次刷新: " + last + "\n下次刷新: " + next.ToString("HH:mm:ss") + "  (还有 " + _nextIn + " 秒)";
        }
    }

    /// <summary>设置窗口。</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly App _app;
        private NumericUpDown _numInterval;
        private ComboBox _cmbMethod;
        private TextBox _txtHotkey;
        private CheckBox _chkManualHk;
        private TextBox _txtManualHk;
        private CheckBox _chkRefreshOnStart;
        private CheckBox _chkStartMinimized;
        private Button _btnRefresh;
        private Button _btnPause;
        private Button _btnSave;
        private System.Windows.Forms.Timer _saveFeedbackTimer;
        private Label _lblStatus;
        private string _lastGoodHotkey;
        private string _lastGoodManual;

        public bool AllowClose;

        private static readonly string[] MethodNames =
        {
            "模拟快捷键 (推荐, 需官方驱动)",
            "全屏黑屏重绘 (无需驱动)",
            "两者都执行"
        };
        private static readonly string[] MethodValues =
        {
            AppSettings.MethodHotkey, AppSettings.MethodFlash, AppSettings.MethodBoth
        };

        public SettingsForm(App app)
        {
            _app = app;
            BuildUi();
            LoadFromSettings();
        }

        private void BuildUi()
        {
            Font = SystemFonts.MessageBoxFont;
            Text = "大上墨水屏 · 刷新助手 InkRefresh";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 372);

            var lbl1 = new Label { Text = "刷新间隔(秒):", Location = new Point(20, 20), AutoSize = true };
            _numInterval = new NumericUpDown
            {
                Location = new Point(160, 16),
                Size = new Size(90, 23),
                Minimum = 1,
                Maximum = 86400
            };
            _numInterval.ValueChanged += delegate
            {
                _app.SetInterval((int)_numInterval.Value);
            };

            var lbl2 = new Label { Text = "刷新方式:", Location = new Point(20, 54), AutoSize = true };
            _cmbMethod = new ComboBox
            {
                Location = new Point(160, 50),
                Size = new Size(230, 23),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (string n in MethodNames) _cmbMethod.Items.Add(n);
            _cmbMethod.SelectedIndexChanged += delegate { SaveFromUi(); };

            var lbl3 = new Label { Text = "驱动刷新快捷键:", Location = new Point(20, 88), AutoSize = true };
            _txtHotkey = new TextBox { Location = new Point(160, 84), Size = new Size(110, 23) };
            var hint3 = new Label
            {
                Text = "需与大上驱动一致, 如 Alt+E",
                Location = new Point(278, 88),
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };
            _txtHotkey.Leave += delegate
            {
                ushort[] k;
                if (!HotkeyParser.TryParse(_txtHotkey.Text, out k))
                {
                    MessageBox.Show("快捷键格式无法识别。示例: Alt+C / Ctrl+Alt+R / F5 / Ctrl+Shift+F9",
                        "大上墨水屏刷新助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _txtHotkey.Text = _lastGoodHotkey;
                    return;
                }
                _lastGoodHotkey = _txtHotkey.Text.Trim();
                SaveFromUi();
            };

            _chkManualHk = new CheckBox
            {
                Text = "立即刷新热键:",
                Location = new Point(20, 122),
                AutoSize = true
            };
            _txtManualHk = new TextBox { Location = new Point(160, 118), Size = new Size(110, 23) };
            var hint4 = new Label
            {
                Text = "全局有效, 如 Ctrl+Alt+R",
                Location = new Point(278, 122),
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };
            _chkManualHk.CheckedChanged += delegate { SaveFromUi(); };
            _txtManualHk.Leave += delegate
            {
                uint mods;
                ushort vk;
                if (!HotkeyParser.ToRegisterHotkey(_txtManualHk.Text, out mods, out vk))
                {
                    MessageBox.Show("热键格式无法识别(不能只有修饰键)。示例: Ctrl+Alt+R",
                        "大上墨水屏刷新助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _txtManualHk.Text = _lastGoodManual;
                    return;
                }
                _lastGoodManual = _txtManualHk.Text.Trim();
                SaveFromUi();
            };

            _chkRefreshOnStart = new CheckBox
            {
                Text = "启动时立即刷新一次",
                Location = new Point(20, 156),
                AutoSize = true
            };
            _chkRefreshOnStart.CheckedChanged += delegate { SaveFromUi(); };

            _chkStartMinimized = new CheckBox
            {
                Text = "启动时最小化到托盘",
                Location = new Point(20, 182),
                AutoSize = true
            };
            _chkStartMinimized.CheckedChanged += delegate { SaveFromUi(); };

            _btnRefresh = new Button
            {
                Text = "立即刷新",
                Location = new Point(20, 214),
                Size = new Size(95, 32)
            };
            _btnRefresh.Click += delegate { _app.ManualRefresh(); };

            _btnPause = new Button
            {
                Text = "暂停",
                Location = new Point(126, 214),
                Size = new Size(95, 32)
            };
            _btnPause.Click += delegate { _app.TogglePause(); };

            _btnSave = new Button
            {
                Text = "保存配置",
                Location = new Point(232, 214),
                Size = new Size(95, 32)
            };
            _btnSave.Click += delegate { SaveAll(true); };

            _saveFeedbackTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _saveFeedbackTimer.Tick += delegate
            {
                _saveFeedbackTimer.Stop();
                _btnSave.Text = "保存配置";
            };

            _lblStatus = new Label
            {
                Location = new Point(18, 258),
                Size = new Size(392, 46),
                BackColor = SystemColors.ControlLightLight,
                BorderStyle = BorderStyle.Fixed3D,
                Text = ""
            };

            var tip = new Label
            {
                Text = "点窗口 X = 最小化到托盘; 退出请用托盘图标右键菜单",
                Location = new Point(20, 314),
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };

            Controls.AddRange(new Control[]
            {
                lbl1, _numInterval, lbl2, _cmbMethod, lbl3, _txtHotkey, hint3,
                _chkManualHk, _txtManualHk, hint4,
                _chkRefreshOnStart, _chkStartMinimized,
                _btnRefresh, _btnPause, _btnSave, _lblStatus, tip
            });
        }

        private void LoadFromSettings()
        {
            AppSettings s = _app.Settings;
            _numInterval.Value = Math.Min(Math.Max(s.IntervalSec, 1), 86400);
            int idx = Array.IndexOf(MethodValues, s.Method);
            if (idx < 0) idx = 0;
            _cmbMethod.SelectedIndex = idx;
            _txtHotkey.Text = s.Hotkey;
            _txtManualHk.Text = s.ManualHotkey;
            _chkManualHk.Checked = s.ManualHotkeyEnabled;
            _chkRefreshOnStart.Checked = s.RefreshOnStart;
            _chkStartMinimized.Checked = s.StartMinimized;
            _lastGoodHotkey = s.Hotkey;
            _lastGoodManual = s.ManualHotkey;
        }

        private void SaveFromUi()
        {
            AppSettings s = _app.Settings;
            int idx = _cmbMethod.SelectedIndex;
            if (idx >= 0 && idx < MethodValues.Length) s.Method = MethodValues[idx];
            s.Hotkey = string.IsNullOrEmpty(_txtHotkey.Text) ? s.Hotkey : _txtHotkey.Text.Trim();
            s.ManualHotkey = string.IsNullOrEmpty(_txtManualHk.Text) ? s.ManualHotkey : _txtManualHk.Text.Trim();
            s.ManualHotkeyEnabled = _chkManualHk.Checked;
            s.RefreshOnStart = _chkRefreshOnStart.Checked;
            s.StartMinimized = _chkStartMinimized.Checked;
            _app.SettingsChanged();
        }

        /// <summary>校验输入并保存配置。interactive=true 时弹提示并给按钮反馈; false 时静默(隐藏/退出前兜底保存)。</summary>
        public void SaveAll(bool interactive)
        {
            ushort[] keys;
            if (!HotkeyParser.TryParse(_txtHotkey.Text, out keys))
            {
                if (interactive)
                    MessageBox.Show("驱动刷新快捷键格式无法识别, 已还原为: " + _lastGoodHotkey,
                        "大上墨水屏刷新助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtHotkey.Text = _lastGoodHotkey;
            }
            else
            {
                _lastGoodHotkey = _txtHotkey.Text.Trim();
            }

            uint mods;
            ushort vk;
            if (!HotkeyParser.ToRegisterHotkey(_txtManualHk.Text, out mods, out vk))
            {
                if (interactive)
                    MessageBox.Show("立即刷新热键格式无法识别(不能只有修饰键), 已还原为: " + _lastGoodManual,
                        "大上墨水屏刷新助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtManualHk.Text = _lastGoodManual;
            }
            else
            {
                _lastGoodManual = _txtManualHk.Text.Trim();
            }

            SaveFromUi();

            if (interactive)
            {
                if (_app.VerifyPersisted())
                {
                    _btnSave.Text = "已保存 ✓";
                    _saveFeedbackTimer.Stop();
                    _saveFeedbackTimer.Start();
                }
                else
                {
                    MessageBox.Show(
                        "配置保存失败, 无法写入:\n" + _app.IniPathUsed +
                        "\n\n请把软件移动到可写目录(如桌面或 D 盘)后重试, 或右键以管理员身份运行。\n" +
                        "详细信息见程序目录下的 InkRefresh.log。",
                        "大上墨水屏刷新助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        public void OnPauseChanged(bool paused)
        {
            _btnPause.Text = paused ? "继续" : "暂停";
        }

        public void UpdateStatus(string text)
        {
            _lblStatus.Text = text;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SaveAll(false);
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
