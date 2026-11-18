using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseBatteryTray
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "MouseBatteryTray_quit"))
            using (var mutex = new Mutex(true, "MouseBatteryTray_single", out bool first))
            {
                // A newer launch replaces the running copy instead of silently exiting
                if (!first)
                {
                    quit.Set();
                    try { if (!mutex.WaitOne(5000)) return; }
                    catch (AbandonedMutexException) { }
                }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.Run(new TrayApp());
            }
        }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    }

    class TrayApp : ApplicationContext
    {
        const string FallbackName = "Mouse";
        const int PollMs = 30_000;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "MouseBatteryTray";

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = PollMs };
        readonly System.Windows.Forms.Timer kick = new System.Windows.Forms.Timer { Interval = 1000 };
        ToolStripMenuItem autostartItem;
        readonly HashSet<string> listening = new HashSet<string>();
        readonly SynchronizationContext ui;
        BatteryReading last;
        int lowBattery = Setting("LowBattery", 15);
        bool lowWarned, fullNotified, busy, pending, lastFresh;
        IntPtr iconHandle;

        // -1 = follow Windows UI language, 0 = Ukrainian, 1 = English
        static bool En => Setting("Lang", -1) is int l && l >= 0 ? l == 1 : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "uk";
        static string T(string uk, string en) => En ? en : uk;

        public TrayApp()
        {
            BuildMenu();
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Poll(); };
            tray.Visible = true;
            SetIcon("…", Color.Gray, FallbackName);

            ui = SynchronizationContext.Current;
            ThreadPool.RegisterWaitForSingleObject(EventWaitHandle.OpenExisting("MouseBatteryTray_quit"),
                (st, to) => ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, true);

            timer.Tick += (s, e) => { StartListeners(); Poll(); };
            // Debounced instant refresh on a StatusChanged push from the receiver
            kick.Tick += (s, e) => { kick.Stop(); StartListeners(); Poll(); };
            SystemEvents.UserPreferenceChanged += (s, e) => Render(lastFresh);
            SystemEvents.DisplaySettingsChanged += (s, e) => Render(lastFresh);

            timer.Start();
            StartListeners();
            Poll();
        }

        void BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(T("Оновити", "Refresh"), null, (s, e) => Poll());
            menu.Items.Add(T("Діагностика", "Diagnostics"), null, (s, e) => Diagnostics());
            var lowMenu = new ToolStripMenuItem(T("Попереджати при", "Warn at"));
            foreach (var v in new[] { 10, 15, 20, 0 })
            {
                var item = new ToolStripMenuItem(v == 0 ? T("Не попереджати", "Never") : v + "%") { Tag = v, Checked = v == lowBattery };
                item.Click += (s, e) =>
                {
                    lowBattery = (int)item.Tag;
                    SaveSetting("LowBattery", lowBattery);
                    foreach (ToolStripMenuItem i in lowMenu.DropDownItems) i.Checked = i == item;
                    lowWarned = false;
                    Render(lastFresh);
                };
                lowMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(lowMenu);
            var langMenu = new ToolStripMenuItem("Мова / Language");
            foreach (var code in new[] { 0, 1 })
                langMenu.DropDownItems.Add(new ToolStripMenuItem(code == 1 ? "English" : "Українська", null, (s, e) =>
                {
                    SaveSetting("Lang", code);
                    BuildMenu();
                    Render(lastFresh);
                }) { Checked = En == (code == 1) });
            menu.Items.Add(langMenu);
            autostartItem = new ToolStripMenuItem(T("Запускати з Windows", "Start with Windows"), null, (s, e) => ToggleAutostart()) { Checked = AutostartEnabled() };
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(T("Вихід", "Exit"), null, (s, e) => ExitThread());

            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            old?.Dispose();
        }

        void Kick() { kick.Stop(); kick.Start(); }

        void StartListeners()
        {
            foreach (var d in Hid.Enumerate(Hid.CompxVid).Where(Hid.IsCompxConfig))
            {
                lock (listening) if (!listening.Add(d.Path)) continue;
                var dev = d;
                new Thread(() =>
                {
                    try { Hid.Listen(dev, () => ui.Post(_ => Kick(), null)); }
                    finally { lock (listening) listening.Remove(dev.Path); }
                }) { IsBackground = true }.Start();
            }
        }

        async void Poll()
        {
            if (busy) { pending = true; return; }
            busy = true;
            try
            {
                var r = await Task.Run(() => ReadAny(null));
                if (r != null) { r.Percent = Smooth(r.Percent); last = r; UpdateDrain(r); }
                Render(r != null);
            }
            catch (Exception ex) { SetIcon("!", Color.OrangeRed, Trim(FallbackName + ": " + ex.Message)); }
            finally
            {
                busy = false;
                if (pending) { pending = false; Poll(); }
            }
        }

        static BatteryReading ReadAny(StringBuilder log)
        {
            var devs = Hid.Enumerate(Hid.CompxVid).Where(Hid.IsCompxConfig).ToList();
            BatteryReading best = null;
            foreach (var d in devs)
            {
                log?.AppendLine("Query " + d);
                var r = Hid.ReadBattery(d, log);
                if (r == null) continue;
                if (best == null || (r.Charging && !best.Charging)) best = r;
            }
            return best;
        }

        void Render(bool fresh)
        {
            lastFresh = fresh;
            if (last == null) { SetIcon("–", Color.Gray, FallbackName); return; }

            int red = lowBattery > 0 ? lowBattery : 10;
            Color c = last.Charging ? Color.FromArgb(80, 220, 100)
                    : last.Percent <= red ? Color.FromArgb(255, 70, 60)
                    : last.Percent <= 30 ? Color.FromArgb(255, 170, 0)
                    : LightTaskbar() ? Color.Black : Color.White;
            if (!fresh) c = Color.Gray;

            string name = string.IsNullOrWhiteSpace(last.Device.Product) ? FallbackName : last.Device.Product;
            string tip = $"{name} {last.Percent}%";
            var hours = last.Charging ? null : HoursLeft(last.Percent);
            if (hours != null) tip += hours < 1 ? T(" · <1 год", " · <1 h") : $" · ~{Math.Round(hours.Value):0} " + T("год", "h");
            SetIcon(last.Percent.ToString(), c, tip);

            if (!fresh) return;
            if (lowBattery > 0 && !last.Charging && last.Percent <= lowBattery && !lowWarned)
            {
                lowWarned = true;
                tray.ShowBalloonTip(5000, T("Мишка сідає", "Mouse battery low"), T("Залишилось", "Remaining") + $" {last.Percent}%", ToolTipIcon.Warning);
            }
            if (last.Charging || last.Percent > lowBattery + 5) lowWarned = false;

            // Smoothed value lags while charging, the firmware byte is what reaches 100 first
            if (last.Charging && (last.Firmware >= 100 || last.Percent >= 100) && !fullNotified)
            {
                fullNotified = true;
                tray.ShowBalloonTip(5000, T("Мишка заряджена", "Mouse charged"), T("Можна знімати з кабелю", "You can unplug the cable"), ToolTipIcon.Info);
            }
            if (!last.Charging) fullNotified = false;
        }

        // Drain rate learned between charges: anchor = first reading after unplug, rate kept for next time
        void UpdateDrain(BatteryReading r)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (r.Charging) { SaveSetting("DrainAt", 0); SaveSetting("ChargeEnd", (int)now); return; }
            // Voltage keeps settling for a while after the cable is pulled, that's not real drain
            if (now - Setting("ChargeEnd", 0) < 900) { SaveSetting("DrainAt", 0); return; }
            long at = Setting("DrainAt", 0);
            int pct = Setting("DrainPct", 0);
            if (at == 0 || r.Percent > pct)
            {
                SaveSetting("DrainAt", (int)now);
                SaveSetting("DrainPct", r.Percent);
                return;
            }
            double h = (now - at) / 3600.0;
            int drop = pct - r.Percent;
            if (h >= 0.33 && drop >= 2)
            {
                double rate = drop / h;
                if (rate > 0.2 && rate < 50) SaveSetting("Rate100", (int)(rate * 100));
            }
        }

        static double? HoursLeft(int percent)
        {
            int rate100 = Setting("Rate100", 0);
            return rate100 > 0 ? percent / (rate100 / 100.0) : (double?)null;
        }

        void SetIcon(string text, Color color, string tip)
        {
            using (var bmp = IconArt.Draw(SystemInformation.SmallIconSize.Width, text, color))
            {
                var old = iconHandle;
                iconHandle = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(iconHandle);
                if (old != IntPtr.Zero) DestroyIcon(old);
            }
            tray.Text = Trim(tip);
        }

        // Rate-limit like the official hub: rise ≤0.028 %/s, fall ≤0.014 %/s, reset after 30 min gap
        static int Smooth(int cur)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + AppName))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var prev = k.GetValue("Level") as int?;
                long dt = now - Convert.ToInt64(k.GetValue("At", 0L));
                int shown;
                if (prev == null || dt > 1800) shown = cur;
                else if (dt < 60) return prev.Value;
                else
                {
                    double up = Math.Min(100, prev.Value + 0.028 * dt), down = Math.Max(0, prev.Value - 0.014 * dt);
                    shown = (int)Math.Round(Math.Max(down, Math.Min(up, cur)));
                }
                k.SetValue("Level", shown, RegistryValueKind.DWord);
                k.SetValue("At", now, RegistryValueKind.QWord);
                return shown;
            }
        }

        static int Setting(string name, int def)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + AppName))
                return k.GetValue(name) as int? ?? def;
        }

        static void SaveSetting(string name, int v)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + AppName))
                k.SetValue(name, v, RegistryValueKind.DWord);
        }

        static string Trim(string s) => s.Length > 63 ? s.Substring(0, 63) : s;

        static bool LightTaskbar()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return (k?.GetValue("SystemUsesLightTheme") as int?) == 1;
        }

        void Diagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MouseBatteryTray diag {DateTime.Now}");
            sb.AppendLine("All HID interfaces with VID 3554:");
            foreach (var d in Hid.Enumerate(Hid.CompxVid)) sb.AppendLine(d.ToString());
            sb.AppendLine();
            var r = ReadAny(sb);
            sb.AppendLine();
            sb.AppendLine(r == null ? "RESULT: no reading" : $"RESULT: raw {r.Percent}% charging={r.Charging} {r.MilliVolts} mV via {r.Device.Pid:X4} \"{r.Device.Product}\"");
            sb.AppendLine($"Shown in tray (smoothed): {(last == null ? "-" : last.Percent + "%")}");
            sb.AppendLine();
            sb.AppendLine("Recent push reports:");
            sb.Append(Hid.PushLog());
            var path = Path.Combine(Path.GetTempPath(), "mouse-battery-diag.txt");
            File.WriteAllText(path, sb.ToString());
            Process.Start("notepad.exe", path);
        }

        static bool AutostartEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k?.GetValue(AppName) != null;
        }

        void ToggleAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (AutostartEnabled()) k.DeleteValue(AppName, false);
                else k.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
            }
            autostartItem.Checked = AutostartEnabled();
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            kick.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }
}
