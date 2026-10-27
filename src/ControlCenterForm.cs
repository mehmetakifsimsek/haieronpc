using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HaierHonDesktop
{
    /// <summary>
    /// Fully custom-painted, animated "glass" Control Center window (first screen).
    /// Borderless rounded window, animated blurred blue background, glass cards,
    /// animated thermostat dial, appliance tabs and quick actions.
    /// </summary>
    public class MainControlForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private const int FORM_W = 620;
        private const int FORM_H = 668;

        private readonly HonCloudClient _client;
        private readonly LocalWebServer _server;
        private NotifyIcon _trayIcon;
        private Icon _appIcon;

        private bool _isDark;
        private string _lang = "tr";

        // ---- Snapshot cache (refreshed every second, no network on UI thread) ----
        private List<Dictionary<string, object>> _appliances = new List<Dictionary<string, object>>();
        private string _selectedMac = "";
        private Dictionary<string, object> _params = new Dictionary<string, object>();
        private Dictionary<string, object> _profile = new Dictionary<string, object>();
        private Dictionary<string, bool> _caps = new Dictionary<string, bool>();
        private Dictionary<string, object> _activeApp = new Dictionary<string, object>();
        private bool _cloud;
        private string _email = "";
        private DateTime _lastUpdate = DateTime.MinValue;
        private string _lastComboSignature = "";
        private bool _suppressComboEvent = false;
        private ComboBox _cmbAppliances;

        private class ApplianceComboItem
        {
            public string Mac { get; set; }
            public string Display { get; set; }
            public override string ToString() { return Display; }
        }

        // ---- Animation state ----
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Windows.Forms.Timer _dataTimer;
        private readonly System.Windows.Forms.Timer _tempDebounce;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private float _t;
        private float _intro = 1f;
        private float _fanAngle;
        private float _dialFrac = -1f;
        private int _frame;
        private Bitmap _bgBitmap;
        private bool _bgBitmapDark;
        private readonly Dictionary<string, float> _hover = new Dictionary<string, float>();
        private string _hoverId;
        private string _pressId;
        private volatile string _busyId;

        private int _optimisticTemp = -1;
        private DateTime _optimisticUntil = DateTime.MinValue;

        private readonly List<HitItem> _hits = new List<HitItem>();

        private class HitItem
        {
            public string Id;
            public RectangleF Rect;
        }

        private class Palette
        {
            public Color BgA, BgB, Blob1, Blob2, Blob3;
            public Color Card, CardHover, CardBorder;
            public Color Text, TextMuted;
            public Color Accent, AccentB, HeroA, HeroB;
            public Color WindowBorder, Shadow;
        }

        // ---- Fonts ----
        private Font _fTitle, _fSub, _fGreeting, _fBody, _fBodyBold, _fSmall, _fSmallBold, _fCaps;
        private Font _fHeroName, _fDialNum, _fDialUnit, _fTile, _fTileSub, _fCta, _fMetric, _fLogo;

        private static readonly StringFormat SfLeft;
        private static readonly StringFormat SfCenter;
        private static readonly StringFormat SfVCenterLeft;
        private static readonly CultureInfo CiTr = new CultureInfo("tr-TR");
        private static readonly CultureInfo CiEn = new CultureInfo("en-US");

        static MainControlForm()
        {
            SfLeft = (StringFormat)StringFormat.GenericTypographic.Clone();
            SfLeft.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            SfLeft.Trimming = StringTrimming.EllipsisCharacter;
            SfLeft.LineAlignment = StringAlignment.Near;

            SfCenter = (StringFormat)StringFormat.GenericTypographic.Clone();
            SfCenter.FormatFlags |= StringFormatFlags.NoWrap;
            SfCenter.Trimming = StringTrimming.EllipsisCharacter;
            SfCenter.Alignment = StringAlignment.Center;
            SfCenter.LineAlignment = StringAlignment.Center;

            SfVCenterLeft = (StringFormat)SfLeft.Clone();
            SfVCenterLeft.LineAlignment = StringAlignment.Center;
        }

        public MainControlForm(HonCloudClient client, LocalWebServer server)
        {
            _client = client;
            _server = server;

            _lang = !string.IsNullOrEmpty(_client.UiLang) ? _client.UiLang : "tr";
            _isDark = _client.UiTheme != "light";

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(FORM_W, FORM_H);
            MaximizeBox = false;
            ShowInTaskbar = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            CreateFonts();
            BuildApplianceComboBox();
            _appIcon = BuildAppIcon();
            Icon = _appIcon;
            UpdateWindowTitle();
            SetupTrayIcon();

            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += OnAnimTick;

            _dataTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _dataTimer.Tick += (s, e) => RefreshSnapshot();

            _tempDebounce = new System.Windows.Forms.Timer { Interval = 400 };
            _tempDebounce.Tick += (s, e) =>
            {
                _tempDebounce.Stop();
                if (_optimisticTemp >= 0)
                {
                    int val = _optimisticTemp;
                    ExecuteCommand(null, "settings", new Dictionary<string, object> { { "tempSel", val.ToString() } });
                }
            };

            Shown += (s, e) =>
            {
                RefreshSnapshot();
                _animTimer.Start();
                _dataTimer.Start();
            };
        }

        private void BuildApplianceComboBox()
        {
            _cmbAppliances = new ComboBox
            {
                Location = new Point(214, 136),
                Size = new Size(368, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed,
                FlatStyle = FlatStyle.Flat,
                ItemHeight = 22,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold)
            };
            UpdateComboTheme();

            _cmbAppliances.DrawItem += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= _cmbAppliances.Items.Count) return;
                e.DrawBackground();
                bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                Color bg = selected
                    ? Color.FromArgb(37, 99, 235)
                    : (_isDark ? Color.FromArgb(20, 34, 68) : Color.White);
                Color fg = selected
                    ? Color.White
                    : (_isDark ? Color.FromArgb(240, 245, 255) : Color.FromArgb(15, 30, 68));
                using (SolidBrush b = new SolidBrush(bg))
                    e.Graphics.FillRectangle(b, e.Bounds);
                string txt = _cmbAppliances.Items[e.Index].ToString();
                RectangleF rf = new RectangleF(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                DrawText(e.Graphics, txt, _cmbAppliances.Font, fg, rf, SfVCenterLeft);
            };

            _cmbAppliances.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressComboEvent) return;
                ApplianceComboItem item = _cmbAppliances.SelectedItem as ApplianceComboItem;
                if (item != null && !string.IsNullOrEmpty(item.Mac) && item.Mac != _selectedMac)
                {
                    _client.SelectedMac = item.Mac;
                    _selectedMac = item.Mac;
                    _optimisticTemp = -1;
                    RefreshSnapshot();
                    RunBackground(null, () => _client.GetStateSnapshot(true));
                }
            };

            Controls.Add(_cmbAppliances);
        }

        private void UpdateComboTheme()
        {
            if (_cmbAppliances == null) return;
            _cmbAppliances.BackColor = _isDark ? Color.FromArgb(20, 34, 68) : Color.White;
            _cmbAppliances.ForeColor = _isDark ? Color.FromArgb(240, 245, 255) : Color.FromArgb(15, 30, 68);
        }

        // =====================================================================
        //  Window chrome (borderless, rounded, shadow, taskbar minimize)
        // =====================================================================

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;            // CS_DROPSHADOW
                cp.Style |= 0x00020000 | 0x00080000;    // WS_MINIMIZEBOX | WS_SYSMENU
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int hr = -1;
            try
            {
                int pref = 2; // DWMWCP_ROUND (Windows 11)
                hr = DwmSetWindowAttribute(Handle, 33, ref pref, 4);
            }
            catch { }

            if (hr != 0)
            {
                using (GraphicsPath path = RoundRect(new RectangleF(0, 0, FORM_W, FORM_H), 16f))
                {
                    Region = new Region(path);
                }
            }
        }

        private void UpdateWindowTitle()
        {
            Text = _lang == "tr" ? "Haier hOn — Klima Kontrol Merkezi" : "Haier hOn — AC Control Center";
        }

        // =====================================================================
        //  Fonts, palette, icon
        // =====================================================================

        private static FontFamily PickFamily(params string[] names)
        {
            foreach (string n in names)
            {
                try { return new FontFamily(n); }
                catch { }
            }
            return FontFamily.GenericSansSerif;
        }

        private void CreateFonts()
        {
            FontFamily disp = PickFamily("Segoe UI Variable Display", "Segoe UI");
            FontFamily text = PickFamily("Segoe UI Variable Text", "Segoe UI");

            _fTitle = new Font(disp, 11.5f, FontStyle.Bold);
            _fSub = new Font(text, 8.25f, FontStyle.Regular);
            _fGreeting = new Font(disp, 17f, FontStyle.Bold);
            _fBody = new Font(text, 9f, FontStyle.Regular);
            _fBodyBold = new Font(text, 9f, FontStyle.Bold);
            _fSmall = new Font(text, 8.25f, FontStyle.Regular);
            _fSmallBold = new Font(text, 8.25f, FontStyle.Bold);
            _fCaps = new Font(text, 7.5f, FontStyle.Bold);
            _fHeroName = new Font(disp, 18f, FontStyle.Bold);
            _fDialNum = new Font(disp, 30f, FontStyle.Bold);
            _fDialUnit = new Font(disp, 12f, FontStyle.Bold);
            _fTile = new Font(disp, 10f, FontStyle.Bold);
            _fTileSub = new Font(text, 8f, FontStyle.Regular);
            _fCta = new Font(disp, 10.5f, FontStyle.Bold);
            _fMetric = new Font(disp, 12.5f, FontStyle.Bold);
            _fLogo = new Font(disp, 8f, FontStyle.Bold);
        }

        private Palette Pal()
        {
            Palette p = new Palette();
            if (_isDark)
            {
                p.BgA = Color.FromArgb(9, 16, 44);
                p.BgB = Color.FromArgb(16, 30, 74);
                p.Blob1 = Color.FromArgb(150, 37, 99, 235);
                p.Blob2 = Color.FromArgb(115, 6, 182, 212);
                p.Blob3 = Color.FromArgb(105, 124, 58, 237);
                p.Card = Color.FromArgb(18, 255, 255, 255);
                p.CardHover = Color.FromArgb(32, 255, 255, 255);
                p.CardBorder = Color.FromArgb(34, 255, 255, 255);
                p.Text = Color.FromArgb(240, 245, 255);
                p.TextMuted = Color.FromArgb(150, 166, 206);
                p.Accent = Color.FromArgb(37, 99, 235);
                p.AccentB = Color.FromArgb(14, 165, 233);
                p.HeroA = Color.FromArgb(29, 78, 216);
                p.HeroB = Color.FromArgb(6, 182, 212);
                p.WindowBorder = Color.FromArgb(45, 255, 255, 255);
                p.Shadow = Color.FromArgb(70, 0, 0, 0);
            }
            else
            {
                p.BgA = Color.FromArgb(232, 241, 255);
                p.BgB = Color.FromArgb(246, 249, 255);
                p.Blob1 = Color.FromArgb(120, 96, 165, 250);
                p.Blob2 = Color.FromArgb(95, 34, 211, 238);
                p.Blob3 = Color.FromArgb(80, 167, 139, 250);
                p.Card = Color.FromArgb(190, 255, 255, 255);
                p.CardHover = Color.FromArgb(240, 255, 255, 255);
                p.CardBorder = Color.FromArgb(210, 224, 250);
                p.Text = Color.FromArgb(15, 30, 68);
                p.TextMuted = Color.FromArgb(92, 108, 146);
                p.Accent = Color.FromArgb(37, 99, 235);
                p.AccentB = Color.FromArgb(14, 165, 233);
                p.HeroA = Color.FromArgb(59, 130, 246);
                p.HeroB = Color.FromArgb(34, 211, 238);
                p.WindowBorder = Color.FromArgb(190, 205, 235);
                p.Shadow = Color.FromArgb(40, 37, 78, 160);
            }
            return p;
        }

        private Icon BuildAppIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        RectangleF r = new RectangleF(1, 1, 30, 30);
                        using (GraphicsPath path = RoundRect(r, 8))
                        using (LinearGradientBrush br = new LinearGradientBrush(r, Color.FromArgb(37, 99, 235), Color.FromArgb(14, 165, 233), 45f))
                        {
                            g.FillPath(br, path);
                        }
                        using (Font f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                        {
                            g.DrawString("h", f, Brushes.White, new RectangleF(0, 0, 32, 31), SfCenter);
                        }
                    }
                    IntPtr h = bmp.GetHicon();
                    Icon ico = (Icon)Icon.FromHandle(h).Clone();
                    DestroyIcon(h);
                    return ico;
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        // =====================================================================
        //  Data
        // =====================================================================

        private void RefreshSnapshot()
        {
            try
            {
                string backendLang = !string.IsNullOrEmpty(_client.UiLang) ? _client.UiLang : "tr";
                bool backendDark = _client.UiTheme != "light";
                if (backendLang != _lang || backendDark != _isDark)
                {
                    _lang = backendLang;
                    _isDark = backendDark;
                    _lastComboSignature = "";
                    UpdateWindowTitle();
                    UpdateComboTheme();
                }

                var snap = _client.GetStateSnapshot(false);
                _appliances = (snap.ContainsKey("appliances") ? snap["appliances"] as List<Dictionary<string, object>> : null)
                              ?? new List<Dictionary<string, object>>();
                _selectedMac = snap.ContainsKey("selectedMac") && snap["selectedMac"] != null ? snap["selectedMac"].ToString() : "";
                _params = (snap.ContainsKey("parameters") ? snap["parameters"] as Dictionary<string, object> : null)
                          ?? new Dictionary<string, object>();
                _profile = (snap.ContainsKey("modelProfile") ? snap["modelProfile"] as Dictionary<string, object> : null)
                           ?? new Dictionary<string, object>();
                _caps = (snap.ContainsKey("capabilities") ? snap["capabilities"] as Dictionary<string, bool> : null)
                        ?? new Dictionary<string, bool>();
                _activeApp = (snap.ContainsKey("appliance") ? snap["appliance"] as Dictionary<string, object> : null)
                             ?? new Dictionary<string, object>();
                _cloud = _client.IsCloudConnected;
                _email = _client.CurrentEmail ?? "";
                _lastUpdate = DateTime.Now;

                if (_optimisticTemp >= 0)
                {
                    int real = (int)Math.Round(GetDouble(_params, "tempSel", -100));
                    if (real == _optimisticTemp || DateTime.Now > _optimisticUntil)
                        _optimisticTemp = -1;
                }

                SyncApplianceComboBox();
                Invalidate();
            }
            catch { }
        }

        private void SyncApplianceComboBox()
        {
            if (_cmbAppliances == null) return;
            bool tr = _lang == "tr";
            string sig = _lang + "|" + _selectedMac + "|" + _appliances.Count;
            for (int i = 0; i < _appliances.Count; i++)
            {
                sig += "|" + GetStr(_appliances[i], "macAddress", "") + ":" + GetStr(_appliances[i], "nickName", "");
            }
            if (sig == _lastComboSignature) return;
            _lastComboSignature = sig;

            _suppressComboEvent = true;
            try
            {
                _cmbAppliances.BeginUpdate();
                _cmbAppliances.Items.Clear();
                int selIdx = 0;
                for (int i = 0; i < _appliances.Count; i++)
                {
                    var a = _appliances[i];
                    string mac = GetStr(a, "macAddress", "");
                    string nick = GetStr(a, "nickName", "Klima");
                    string model = GetStr(a, "modelName", "AC");
                    string capBadge = "";
                    if (a.ContainsKey("supportedCount") && a.ContainsKey("totalFeatureCount"))
                    {
                        capBadge = string.Format(tr ? " [{0}/{1} Özellik]" : " [{0}/{1} Features]", a["supportedCount"], a["totalFeatureCount"]);
                    }
                    string label = string.Format("{0} — {1}{2}", nick, model, capBadge);
                    _cmbAppliances.Items.Add(new ApplianceComboItem { Mac = mac, Display = label });
                    if (mac == _selectedMac) selIdx = i;
                }
                if (_cmbAppliances.Items.Count > 0)
                {
                    _cmbAppliances.SelectedIndex = selIdx;
                }
                _cmbAppliances.EndUpdate();
            }
            finally
            {
                _suppressComboEvent = false;
            }
        }

        private static string GetStr(Dictionary<string, object> d, string key, string def)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            string s = d[key].ToString();
            return string.IsNullOrEmpty(s) ? def : s;
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            string s = GetStr(d, key, null);
            if (s == null) return def;
            double v;
            if (double.TryParse(s.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        private bool IsOn
        {
            get { return GetStr(_params, "onOffStatus", "0") == "1"; }
        }

        private bool IsCapSupported(string key)
        {
            if (_caps == null || !_caps.ContainsKey(key)) return true;
            return _caps[key];
        }

        private void GetModeLimits(out double min, out double max)
        {
            min = 16;
            max = 30;
            try
            {
                if (_profile.ContainsKey("tempMin")) min = Convert.ToDouble(_profile["tempMin"], CultureInfo.InvariantCulture);
                if (_profile.ContainsKey("tempMax")) max = Convert.ToDouble(_profile["tempMax"], CultureInfo.InvariantCulture);
                string mode = GetStr(_params, "machMode", "1");
                if (_profile.ContainsKey("modeTempLimits"))
                {
                    var lim = _profile["modeTempLimits"] as Dictionary<string, object>;
                    if (lim != null && lim.ContainsKey(mode))
                    {
                        var r = lim[mode] as Dictionary<string, object>;
                        if (r != null)
                        {
                            if (r.ContainsKey("min")) min = Convert.ToDouble(r["min"], CultureInfo.InvariantCulture);
                            if (r.ContainsKey("max")) max = Convert.ToDouble(r["max"], CultureInfo.InvariantCulture);
                        }
                    }
                }
            }
            catch { }
            if (max <= min) max = min + 1;
        }

        private int DisplayTemp()
        {
            if (_optimisticTemp >= 0 && DateTime.Now < _optimisticUntil) return _optimisticTemp;
            return (int)Math.Round(GetDouble(_params, "tempSel", 23));
        }

        private string ModeName(string code)
        {
            bool tr = _lang == "tr";
            switch (code)
            {
                case "1": return tr ? "Soğutma" : "Cooling";
                case "4": return tr ? "Isıtma" : "Heating";
                case "2": return tr ? "Nem Alma" : "Dry";
                case "6": return tr ? "Fan" : "Fan";
                default: return tr ? "Otomatik" : "Auto";
            }
        }

        // =====================================================================
        //  Actions
        // =====================================================================

        private void RunBackground(string busyId, Action work)
        {
            if (busyId != null)
            {
                if (_busyId != null) return;
                _busyId = busyId;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                catch { }
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (busyId != null && _busyId == busyId) _busyId = null;
                        RefreshSnapshot();
                    }));
                }
                catch { }
            });
        }

        private void ExecuteCommand(string busyId, string commandName, Dictionary<string, object> parameters)
        {
            RunBackground(busyId, () =>
            {
                string msg;
                _client.ExecuteAcAction(new Dictionary<string, object>
                {
                    { "commandName", commandName },
                    { "parameters", parameters }
                }, out msg);
            });
        }

        private void ToggleParam(string busyId, string key)
        {
            bool on = GetStr(_params, key, "0") == "1";
            ExecuteCommand(busyId, "settings", new Dictionary<string, object> { { key, on ? "0" : "1" } });
        }

        private void StepTemp(int delta)
        {
            double min, max;
            GetModeLimits(out min, out max);
            int next = (int)Math.Max(min, Math.Min(max, DisplayTemp() + delta));
            _optimisticTemp = next;
            _optimisticUntil = DateTime.Now.AddSeconds(6);
            _tempDebounce.Stop();
            _tempDebounce.Start();
            Invalidate();
        }

        private void HandleAction(string id)
        {
            if (id == null) return;

            if (id.StartsWith("tab:"))
            {
                string mac = id.Substring(4);
                if (mac != _selectedMac)
                {
                    _client.SelectedMac = mac;
                    _selectedMac = mac;
                    _optimisticTemp = -1;
                    RefreshSnapshot();
                    RunBackground(null, () => _client.GetStateSnapshot(true));
                }
                return;
            }

            switch (id)
            {
                case "close":
                    Close();
                    break;
                case "min":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "lang_tr":
                case "lang_en":
                    _lang = id == "lang_tr" ? "tr" : "en";
                    _client.SetUiPreferences(_lang, _isDark ? "dark" : "light");
                    _lastComboSignature = "";
                    UpdateWindowTitle();
                    SyncApplianceComboBox();
                    Invalidate();
                    break;
                case "theme":
                    _isDark = !_isDark;
                    _client.SetUiPreferences(_lang, _isDark ? "dark" : "light");
                    UpdateComboTheme();
                    Invalidate();
                    break;
                case "account":
                    OnCloudAccountClick();
                    break;
                case "temp_minus":
                    StepTemp(-1);
                    break;
                case "temp_plus":
                    StepTemp(1);
                    break;
                case "power_on":
                    ExecuteCommand(id, "startProgram", new Dictionary<string, object> { { "onOffStatus", "1" } });
                    break;
                case "power_off":
                    ExecuteCommand(id, "stopProgram", new Dictionary<string, object> { { "onOffStatus", "0" } });
                    break;
                case "cool22":
                    ExecuteCommand(id, "settings", new Dictionary<string, object>
                    {
                        { "onOffStatus", "1" }, { "machMode", "1" }, { "tempSel", "22" }
                    });
                    break;
                case "heat25":
                    ExecuteCommand(id, "settings", new Dictionary<string, object>
                    {
                        { "onOffStatus", "1" }, { "machMode", "4" }, { "tempSel", "25" }
                    });
                    break;
                case "silent":
                    ToggleParam(id, "muteStatus");
                    break;
                case "turbo":
                    ToggleParam(id, "rapidMode");
                    break;
                case "open_panel":
                    LaunchAppWindow(_server.BaseUrl + "?lang=" + _lang + "&theme=" + (_isDark ? "dark" : "light"));
                    break;
            }
        }

        private void OnCloudAccountClick()
        {
            if (_client.IsCloudConnected)
            {
                _client.LogoutAndUseDemo();
                RefreshSnapshot();
                return;
            }

            using (NativeLoginDialog dlg = new NativeLoginDialog(_isDark, _lang))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrEmpty(dlg.Email) || string.IsNullOrEmpty(dlg.Password)) return;

                string email = dlg.Email;
                string pass = dlg.Password;
                if (_busyId != null) return;
                _busyId = "account";

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string msg = "";
                    bool ok = false;
                    try { ok = _client.LoginWithCredentials(email, pass, out msg); }
                    catch (Exception ex) { msg = ex.Message; }
                    try
                    {
                        BeginInvoke((MethodInvoker)(() =>
                        {
                            _busyId = null;
                            RefreshSnapshot();
                            MessageBox.Show(this, msg, "Haier hOn", MessageBoxButtons.OK,
                                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                        }));
                    }
                    catch { }
                });
            }
        }

        // =====================================================================
        //  Animation loop
        // =====================================================================

        private static float Ease(float x)
        {
            x = Math.Max(0f, Math.Min(1f, x));
            return 1f - (float)Math.Pow(1f - x, 3);
        }

        private float HoverOf(string id)
        {
            float v;
            return _hover.TryGetValue(id, out v) ? v : 0f;
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            float now = (float)_clock.Elapsed.TotalSeconds;
            float dt = Math.Min(0.1f, Math.Max(0.001f, now - _t));
            _t = now;

            if (WindowState == FormWindowState.Minimized || !Visible) return;

            bool needFullInvalidate = false;

            if (_intro < 1f)
            {
                _intro = Math.Min(1f, _intro + dt / 0.35f);
                try { Opacity = _intro >= 1f ? 1.0 : Ease(_intro); }
                catch { }
                needFullInvalidate = true;
            }

            if (_hoverId != null && !_hover.ContainsKey(_hoverId)) _hover[_hoverId] = 0f;
            List<string> keys = new List<string>(_hover.Keys);
            float k = Math.Min(1f, dt * 18f);
            foreach (string key in keys)
            {
                float cur = _hover[key];
                float target = key == _hoverId ? 1f : 0f;
                if (Math.Abs(cur - target) > 0.002f)
                {
                    float v = cur + (target - cur) * k;
                    if (Math.Abs(v - target) < 0.004f) v = target;
                    _hover[key] = v;
                    needFullInvalidate = true;
                }
            }

            double min, max;
            GetModeLimits(out min, out max);
            float targetFrac = (float)Math.Max(0, Math.Min(1, (DisplayTemp() - min) / (max - min)));
            if (_dialFrac < 0)
            {
                _dialFrac = targetFrac;
                needFullInvalidate = true;
            }
            else if (Math.Abs(targetFrac - _dialFrac) > 0.002f)
            {
                _dialFrac += (targetFrac - _dialFrac) * Math.Min(1f, dt * 12f);
                if (Math.Abs(targetFrac - _dialFrac) < 0.003f) _dialFrac = targetFrac;
                needFullInvalidate = true;
            }

            if (_busyId != null)
            {
                needFullInvalidate = true;
            }

            _frame++;
            if (IsOn)
            {
                _fanAngle = (_fanAngle + dt * 360f) % 360f;
            }

            if (needFullInvalidate)
            {
                Invalidate();
            }
            else if (IsOn && (_frame % 2 == 0))
            {
                Invalidate(new Rectangle(44, 280, 240, 34));
            }
        }

        // =====================================================================
        //  Mouse
        // =====================================================================

        private string HitTest(Point pt)
        {
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (_hits[i].Rect.Contains(pt)) return _hits[i].Id;
            }
            return null;
        }

        private void AddHit(string id, RectangleF r)
        {
            _hits.Add(new HitItem { Id = id, Rect = r });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string id = HitTest(e.Location);
            if (id != _hoverId)
            {
                _hoverId = id;
                Cursor = id != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverId != null || _pressId != null)
            {
                _hoverId = null;
                _pressId = null;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            string id = HitTest(e.Location);
            if (id == null && e.Y < 60)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
                return;
            }
            _pressId = id;
            if (id != null) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            string id = HitTest(e.Location);
            string pressed = _pressId;
            _pressId = null;
            Invalidate();
            if (id != null && id == pressed) HandleAction(id);
        }

        // =====================================================================
        //  Painting
        // =====================================================================

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Everything is painted in OnPaint (prevents flicker)
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.CompositingQuality = CompositingQuality.HighSpeed;

            Palette p = Pal();
            _hits.Clear();

            DrawBackground(g, p);

            DrawTitleBar(g, p);
            DrawGreeting(g, p);
            DrawTabs(g, p);
            DrawHero(g, p);
            DrawTiles(g, p);
            DrawCta(g, p);
            DrawFooter(g, p);

            using (GraphicsPath border = RoundRect(new RectangleF(0.5f, 0.5f, FORM_W - 1f, FORM_H - 1f), 16f))
            using (Pen pen = new Pen(p.WindowBorder, 1f))
            {
                g.DrawPath(pen, border);
            }
        }

        private void DrawBackground(Graphics g, Palette p)
        {
            if (_bgBitmap == null || _bgBitmapDark != _isDark)
            {
                if (_bgBitmap != null) _bgBitmap.Dispose();
                _bgBitmap = new Bitmap(FORM_W, FORM_H, PixelFormat.Format32bppPArgb);
                _bgBitmapDark = _isDark;
                Rectangle full = new Rectangle(0, 0, FORM_W, FORM_H);
                using (Graphics bg = Graphics.FromImage(_bgBitmap))
                {
                    bg.SmoothingMode = SmoothingMode.AntiAlias;
                    using (LinearGradientBrush lg = new LinearGradientBrush(full, p.BgA, p.BgB, 55f))
                    {
                        bg.FillRectangle(lg, full);
                    }
                    DrawBlob(bg, 110f, 150f, 270f, p.Blob1);
                    DrawBlob(bg, 520f, 320f, 250f, p.Blob2);
                    DrawBlob(bg, 250f, 610f, 290f, p.Blob3);
                }
            }
            g.DrawImageUnscaled(_bgBitmap, 0, 0);
        }

        private static void DrawBlob(Graphics g, float cx, float cy, float r, Color c)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                using (PathGradientBrush pgb = new PathGradientBrush(path))
                {
                    pgb.CenterColor = c;
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, c) };
                    g.FillPath(pgb, path);
                }
            }
        }

        // ------------------------------------------------------------------
        //  Title bar
        // ------------------------------------------------------------------
        private void DrawTitleBar(Graphics g, Palette p)
        {
            bool tr = _lang == "tr";

            RectangleF logo = new RectangleF(24, 15, 32, 32);
            DrawSoftShadow(g, logo, 10, Color.FromArgb(70, p.Accent), 4, 1.5f);
            using (GraphicsPath lp = RoundRect(logo, 10))
            using (LinearGradientBrush lb = new LinearGradientBrush(logo, p.Accent, p.AccentB, 45f))
            {
                g.FillPath(lb, lp);
            }
            DrawText(g, "hOn", _fLogo, Color.White, logo, SfCenter);

            DrawText(g, "Haier hOn", _fTitle, p.Text, new RectangleF(66, 13, 260, 22), SfLeft);
            DrawText(g, tr ? "Klima Kontrol Merkezi" : "AC Control Center", _fSub, p.TextMuted, new RectangleF(66, 34, 260, 16), SfLeft);

            // Language segmented control
            RectangleF seg = new RectangleF(334, 17, 88, 30);
            FillGlass(g, seg, 15, p, 0f);
            DrawSegment(g, p, "lang_tr", "TR", new RectangleF(337, 20, 40, 24), _lang == "tr");
            DrawSegment(g, p, "lang_en", "EN", new RectangleF(379, 20, 40, 24), _lang == "en");

            // Theme toggle (Gündüz Modu / Gece Modu)
            RectangleF th = new RectangleF(430, 17, 106, 30);
            float hv = HoverOf("theme");
            FillGlass(g, th, 15, p, hv);
            PointF ic = new PointF(th.X + 16, th.Y + th.Height / 2);
            if (_isDark) DrawSunIcon(g, ic, 14, Color.FromArgb(251, 191, 36));
            else DrawMoonIcon(g, ic, 14, p.Accent);
            string thText = _isDark ? (tr ? "Gündüz Modu" : "Light Mode") : (tr ? "Gece Modu" : "Dark Mode");
            DrawText(g, thText, _fSmallBold, p.Text, new RectangleF(th.X + 28, th.Y, th.Width - 32, th.Height), VCenterLeft());
            AddHit("theme", th);

            // Minimize & close
            RectangleF mn = new RectangleF(544, 18, 28, 28);
            RectangleF cl = new RectangleF(576, 18, 28, 28);
            float hm = HoverOf("min");
            float hc = HoverOf("close");
            if (hm > 0.01f)
                using (GraphicsPath pp = RoundRect(mn, 8))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(hm * (_isDark ? 40 : 200)), _isDark ? Color.White : Color.White)))
                    g.FillPath(b, pp);
            if (hc > 0.01f)
                using (GraphicsPath pp = RoundRect(cl, 8))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(hc * 255), 239, 68, 68)))
                    g.FillPath(b, pp);

            using (Pen pen = new Pen(p.TextMuted, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, mn.X + 9, mn.Y + 14, mn.Right - 9, mn.Y + 14);
            }
            Color xc = Blend(p.TextMuted, Color.White, hc);
            using (Pen pen = new Pen(xc, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cl.X + 9.5f, cl.Y + 9.5f, cl.Right - 9.5f, cl.Bottom - 9.5f);
                g.DrawLine(pen, cl.Right - 9.5f, cl.Y + 9.5f, cl.X + 9.5f, cl.Bottom - 9.5f);
            }
            AddHit("min", mn);
            AddHit("close", cl);
        }

        private void DrawSegment(Graphics g, Palette p, string id, string text, RectangleF r, bool active)
        {
            float hv = HoverOf(id);
            if (active)
            {
                DrawSoftShadow(g, r, 12, Color.FromArgb(60, p.Accent), 3, 1f);
                using (GraphicsPath pp = RoundRect(r, 12))
                using (LinearGradientBrush b = new LinearGradientBrush(r, p.Accent, p.AccentB, 0f))
                    g.FillPath(b, pp);
                DrawText(g, text, _fSmallBold, Color.White, r, SfCenter);
            }
            else
            {
                if (hv > 0.01f)
                    using (GraphicsPath pp = RoundRect(r, 12))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(hv * 60), p.Accent)))
                        g.FillPath(b, pp);
                DrawText(g, text, _fSmallBold, p.TextMuted, r, SfCenter);
            }
            AddHit(id, r);
        }

        // ------------------------------------------------------------------
        //  Greeting + status + account
        // ------------------------------------------------------------------
        private void DrawGreeting(Graphics g, Palette p)
        {
            bool tr = _lang == "tr";
            int h = DateTime.Now.Hour;
            string greet;
            if (h >= 5 && h < 12) greet = tr ? "Günaydın" : "Good morning";
            else if (h >= 12 && h < 18) greet = tr ? "İyi günler" : "Good afternoon";
            else if (h >= 18 && h < 22) greet = tr ? "İyi akşamlar" : "Good evening";
            else greet = tr ? "İyi geceler" : "Good night";

            DrawText(g, greet, _fGreeting, p.Text, new RectangleF(24, 62, 400, 34), SfLeft);

            // status dot
            PointF dot = new PointF(30, 108);
            Color dc = _cloud ? Color.FromArgb(52, 211, 153) : Color.FromArgb(251, 191, 36);
            using (SolidBrush pb = new SolidBrush(Color.FromArgb(55, dc)))
            {
                g.FillEllipse(pb, dot.X - 7, dot.Y - 7, 14, 14);
            }
            using (SolidBrush db = new SolidBrush(dc))
                g.FillEllipse(db, dot.X - 4, dot.Y - 4, 8, 8);

            CultureInfo ci = tr ? CiTr : CiEn;
            string conn = _cloud
                ? ((tr ? "Bulut bağlı · " : "Cloud connected · ") + _email)
                : (tr ? "Çevrimdışı önizleme" : "Offline preview");
            string line = conn + "   ·   " + DateTime.Now.ToString(tr ? "d MMMM dddd, HH:mm" : "dddd, MMMM d, HH:mm", ci);
            DrawText(g, line, _fSmall, p.TextMuted, new RectangleF(42, 100, 400, 18), SfLeft);

            // Account button
            RectangleF ab = new RectangleF(FORM_W - 24 - 140, 70, 140, 36);
            float hv = HoverOf("account");
            string atext = _cloud ? (tr ? "Oturumu Kapat" : "Sign Out") : (tr ? "Hesap Bağla" : "Sign In");
            if (_cloud)
            {
                FillGlass(g, ab, 18, p, hv);
                DrawUserIcon(g, new PointF(ab.X + 20, ab.Y + 18), 15, p.Accent);
                DrawText(g, atext, _fSmallBold, p.Text, new RectangleF(ab.X + 34, ab.Y, ab.Width - 40, ab.Height), VCenterLeft());
            }
            else
            {
                DrawSoftShadow(g, ab, 18, Color.FromArgb((int)(50 + 50 * hv), p.Accent), 4, 2f);
                using (GraphicsPath pp = RoundRect(ab, 18))
                using (LinearGradientBrush b = new LinearGradientBrush(ab, Blend(p.Accent, Color.White, hv * 0.12f), p.AccentB, 0f))
                    g.FillPath(b, pp);
                DrawUserIcon(g, new PointF(ab.X + 20, ab.Y + 18), 15, Color.White);
                DrawText(g, atext, _fSmallBold, Color.White, new RectangleF(ab.X + 34, ab.Y, ab.Width - 40, ab.Height), VCenterLeft());
            }
            if (_busyId == "account") DrawSpinner(g, new PointF(ab.Right - 16, ab.Y + 18), 7, _cloud ? p.Accent : Color.White);
            AddHit("account", ab);
        }

        // ------------------------------------------------------------------
        //  Appliance selector bar (ComboBox / DropDownList)
        // ------------------------------------------------------------------
        private void DrawTabs(Graphics g, Palette p)
        {
            RectangleF bar = new RectangleF(24, 128, FORM_W - 48, 44);
            FillGlass(g, bar, 16, p, 0f);

            DrawAcGlyph(g, new PointF(bar.X + 22, bar.Y + bar.Height / 2), 18, p.Accent);
            string lbl = _lang == "tr" ? "Bağlı Klima Seçimi:" : "Select Connected AC:";
            DrawText(g, lbl, _fBodyBold, p.Text, new RectangleF(bar.X + 38, bar.Y, 152, bar.Height), SfVCenterLeft);
        }

        // ------------------------------------------------------------------
        //  Hero card with animated thermostat dial
        // ------------------------------------------------------------------
        private void DrawHero(Graphics g, Palette p)
        {
            bool tr = _lang == "tr";
            bool on = IsOn;
            RectangleF hero = new RectangleF(24, 186, FORM_W - 48, 214);

            DrawSoftShadow(g, hero, 22, Color.FromArgb(_isDark ? 90 : 70, p.HeroA), 7, 4f);
            using (GraphicsPath hp = RoundRect(hero, 22))
            {
                using (LinearGradientBrush hb = new LinearGradientBrush(hero, p.HeroA, p.HeroB, 25f))
                    g.FillPath(hb, hp);

                GraphicsState st = g.Save();
                g.SetClip(hp);
                // Decorative floating translucent circles
                float c1x = hero.Right - 70 + (float)Math.Sin(_t * 0.5f) * 12;
                float c1y = hero.Y + 10 + (float)Math.Cos(_t * 0.4f) * 8;
                using (SolidBrush b1 = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
                    g.FillEllipse(b1, c1x - 140, c1y - 140, 280, 280);
                float c2x = hero.X + 210 + (float)Math.Cos(_t * 0.35f) * 18;
                using (SolidBrush b2 = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
                    g.FillEllipse(b2, c2x - 160, hero.Bottom - 50, 320, 320);
                // top sheen
                RectangleF sheen = new RectangleF(hero.X, hero.Y, hero.Width, hero.Height * 0.55f);
                using (LinearGradientBrush sb = new LinearGradientBrush(sheen, Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(sb, sheen);
                g.Restore(st);
            }

            float lx = hero.X + 24;
            DrawText(g, tr ? "AKTİF KLİMA" : "ACTIVE UNIT", _fCaps, Color.FromArgb(200, 255, 255, 255), new RectangleF(lx, hero.Y + 20, 300, 14), SfLeft);
            string nick = GetStr(_activeApp, "nickName", GetStr(_activeApp, "modelName", "Haier Klima"));
            DrawText(g, nick, _fHeroName, Color.White, new RectangleF(lx, hero.Y + 34, 320, 34), SfLeft);

            string model = GetStr(_profile, "modelName", GetStr(_activeApp, "modelName", "AC"));
            string series = GetStr(_profile, "series", GetStr(_activeApp, "series", ""));
            DrawText(g, model + (string.IsNullOrEmpty(series) ? "" : "  ·  " + series), _fSmall, Color.FromArgb(215, 255, 255, 255),
                new RectangleF(lx, hero.Y + 70, 320, 16), SfLeft);

            // Status pill
            string status = on ? (tr ? "ÇALIŞIYOR" : "RUNNING") : (tr ? "KAPALI" : "OFF");
            string modeTxt = on ? ("  ·  " + ModeName(GetStr(_params, "machMode", "1"))) : "";
            SizeF ssz = g.MeasureString(status + modeTxt, _fSmallBold, PointF.Empty, SfLeft);
            RectangleF pill = new RectangleF(lx, hero.Y + 96, ssz.Width + 62, 30);
            using (GraphicsPath pp = RoundRect(pill, 15))
            using (SolidBrush pb = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                g.FillPath(pb, pp);
            PointF sd = new PointF(pill.X + 15, pill.Y + 15);
            Color sdc = on ? Color.FromArgb(74, 222, 128) : Color.FromArgb(203, 213, 225);
            if (on)
            {
                float ph = (_t % 1.4f) / 1.4f;
                using (SolidBrush ring = new SolidBrush(Color.FromArgb((int)(150 * (1 - ph)), sdc)))
                {
                    float pr = 4 + ph * 7;
                    g.FillEllipse(ring, sd.X - pr, sd.Y - pr, pr * 2, pr * 2);
                }
            }
            using (SolidBrush sdb = new SolidBrush(sdc))
                g.FillEllipse(sdb, sd.X - 4, sd.Y - 4, 8, 8);
            DrawText(g, status + modeTxt, _fSmallBold, Color.White, new RectangleF(pill.X + 26, pill.Y, ssz.Width + 4, pill.Height), VCenterLeft());
            DrawFanIcon(g, new PointF(pill.Right - 16, pill.Y + 15), 16, Color.FromArgb(on ? 255 : 150, 255, 255, 255), on ? _fanAngle : 0f);

            // Metrics
            double tin = GetDouble(_params, "tempIndoor", double.NaN);
            double tout = GetDouble(_params, "tempOutdoor", double.NaN);
            DrawMetric(g, lx, hero.Y + 138, tr ? "İÇ ORTAM" : "INDOOR", double.IsNaN(tin) ? "--" : tin.ToString("0.0", CultureInfo.InvariantCulture) + "°C");
            DrawMetric(g, lx + 112, hero.Y + 138, tr ? "DIŞ ORTAM" : "OUTDOOR", double.IsNaN(tout) ? "--" : tout.ToString("0.0", CultureInfo.InvariantCulture) + "°C");

            int sup = 0, unsup = 0;
            try
            {
                if (_profile.ContainsKey("supportedCount")) sup = Convert.ToInt32(_profile["supportedCount"]);
                if (_profile.ContainsKey("unsupportedCount")) unsup = Convert.ToInt32(_profile["unsupportedCount"]);
            }
            catch { }
            string capLine = tr
                ? string.Format("{0} özellik destekleniyor  ·  {1} bu modelde yok", sup, unsup)
                : string.Format("{0} features supported  ·  {1} not on this model", sup, unsup);
            DrawText(g, capLine, _fSmall, Color.FromArgb(200, 255, 255, 255), new RectangleF(lx, hero.Y + 186, 330, 16), SfLeft);

            // ---- Dial ----
            PointF c = new PointF(hero.Right - 110, hero.Y + 100);
            float r = 72f;
            RectangleF dr = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
            int alphaMul = on ? 255 : 150;

            using (Pen track = new Pen(Color.FromArgb(55, 255, 255, 255), 10f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, dr, 135f, 270f);

            // tick marks
            using (Pen tick = new Pen(Color.FromArgb(60, 255, 255, 255), 1.2f))
            {
                for (int i = 0; i <= 27; i++)
                {
                    double ang = (135 + i * 10) * Math.PI / 180.0;
                    float r1 = r - 14, r2 = r - (i % 3 == 0 ? 20 : 17);
                    g.DrawLine(tick, c.X + (float)Math.Cos(ang) * r1, c.Y + (float)Math.Sin(ang) * r1,
                                     c.X + (float)Math.Cos(ang) * r2, c.Y + (float)Math.Sin(ang) * r2);
                }
            }

            float frac = Math.Max(0f, Math.Min(1f, _dialFrac));
            if (frac > 0.002f)
            {
                using (Pen glow = new Pen(Color.FromArgb(on ? 60 : 25, 255, 255, 255), 18f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(glow, dr, 135f, 270f * frac);
                using (Pen prog = new Pen(Color.FromArgb(alphaMul, 255, 255, 255), 10f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(prog, dr, 135f, 270f * frac);
            }
            double ka = (135 + 270 * frac) * Math.PI / 180.0;
            PointF knob = new PointF(c.X + (float)Math.Cos(ka) * r, c.Y + (float)Math.Sin(ka) * r);
            using (SolidBrush ks = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                g.FillEllipse(ks, knob.X - 9, knob.Y - 7.5f, 18, 18);
            g.FillEllipse(Brushes.White, knob.X - 9, knob.Y - 9, 18, 18);
            using (SolidBrush kd = new SolidBrush(p.HeroA))
                g.FillEllipse(kd, knob.X - 3.5f, knob.Y - 3.5f, 7, 7);

            DrawText(g, tr ? "HEDEF" : "TARGET", _fCaps, Color.FromArgb(200, 255, 255, 255), new RectangleF(c.X - 50, c.Y - 40, 100, 14), SfCenter);
            string tnum = DisplayTemp().ToString();
            SizeF nsz = g.MeasureString(tnum, _fDialNum, PointF.Empty, SfLeft);
            SizeF usz = g.MeasureString("°C", _fDialUnit, PointF.Empty, SfLeft);
            float total = nsz.Width + usz.Width + 2;
            float nx = c.X - total / 2;
            DrawText(g, tnum, _fDialNum, Color.FromArgb(alphaMul, 255, 255, 255), new RectangleF(nx, c.Y - 28, nsz.Width + 4, 50), SfLeft);
            DrawText(g, "°C", _fDialUnit, Color.FromArgb(alphaMul, 255, 255, 255), new RectangleF(nx + nsz.Width + 2, c.Y - 22, usz.Width + 4, 24), SfLeft);
            DrawText(g, ModeName(GetStr(_params, "machMode", "1")), _fSmall, Color.FromArgb(205, 255, 255, 255), new RectangleF(c.X - 60, c.Y + 20, 120, 16), SfCenter);

            DrawRoundIconButton(g, "temp_minus", new PointF(c.X - 34, c.Y + 74), 15, false);
            DrawRoundIconButton(g, "temp_plus", new PointF(c.X + 34, c.Y + 74), 15, true);
        }

        private void DrawMetric(Graphics g, float x, float y, string label, string value)
        {
            DrawText(g, label, _fCaps, Color.FromArgb(190, 255, 255, 255), new RectangleF(x, y, 110, 14), SfLeft);
            DrawText(g, value, _fMetric, Color.White, new RectangleF(x, y + 14, 110, 26), SfLeft);
        }

        private void DrawRoundIconButton(Graphics g, string id, PointF c, float r, bool plus)
        {
            float hv = HoverOf(id);
            float pr = _pressId == id ? 1.5f : 0f;
            float rr = r - pr;
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(45 + 50 * hv), 255, 255, 255)))
                g.FillEllipse(b, c.X - rr, c.Y - rr, rr * 2, rr * 2);
            using (Pen pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                g.DrawEllipse(pen, c.X - rr, c.Y - rr, rr * 2, rr * 2);
            using (Pen pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, c.X - 5.5f, c.Y, c.X + 5.5f, c.Y);
                if (plus) g.DrawLine(pen, c.X, c.Y - 5.5f, c.X, c.Y + 5.5f);
            }
            AddHit(id, new RectangleF(c.X - r - 3, c.Y - r - 3, r * 2 + 6, r * 2 + 6));
        }

        // ------------------------------------------------------------------
        //  Quick action tiles
        // ------------------------------------------------------------------
        private void DrawTiles(Graphics g, Palette p)
        {
            bool tr = _lang == "tr";
            bool on = IsOn;
            string mode = GetStr(_params, "machMode", "1");
            int temp = DisplayTemp();
            bool mute = GetStr(_params, "muteStatus", "0") == "1";
            bool rapid = GetStr(_params, "rapidMode", "0") == "1";
            bool muteSup = IsCapSupported("muteStatus");
            bool rapidSup = IsCapSupported("rapidMode");

            string notOnModel = tr ? "Bu modelde yok" : "Not on this model";

            string[] ids = { "power_on", "power_off", "cool22", "heat25", "silent", "turbo" };
            string[] titles =
            {
                tr ? "Klimayı Aç" : "Turn On",
                tr ? "Klimayı Kapat" : "Turn Off",
                tr ? "22°C Soğutma" : "22°C Cooling",
                tr ? "25°C Isıtma" : "25°C Heating",
                tr ? "Sessiz Mod" : "Quiet Mode",
                tr ? "Turbo Mod" : "Turbo Mode"
            };
            string[] subs =
            {
                on ? (tr ? "Şu an çalışıyor" : "Currently running") : (tr ? "Gücü başlat" : "Start the unit"),
                !on ? (tr ? "Şu an kapalı" : "Currently off") : (tr ? "Bekleme moduna al" : "Switch to standby"),
                tr ? "Hızlı ön ayar" : "Quick preset",
                tr ? "Hızlı ön ayar" : "Quick preset",
                !muteSup ? notOnModel : (mute ? (tr ? "Aktif" : "Active") : (tr ? "Fan sesini azalt" : "Lower fan noise")),
                !rapidSup ? notOnModel : (rapid ? (tr ? "Aktif" : "Active") : (tr ? "Maksimum güç" : "Maximum power"))
            };
            bool[] active =
            {
                on, !on,
                on && mode == "1" && temp == 22,
                on && mode == "4" && temp == 25,
                mute && muteSup,
                rapid && rapidSup
            };
            bool[] enabled = { true, true, true, true, muteSup, rapidSup };
            Color[,] grads =
            {
                { Color.FromArgb(16, 185, 129), Color.FromArgb(52, 211, 153) },
                { Color.FromArgb(239, 68, 68), Color.FromArgb(248, 113, 113) },
                { Color.FromArgb(37, 99, 235), Color.FromArgb(56, 189, 248) },
                { Color.FromArgb(249, 115, 22), Color.FromArgb(251, 191, 36) },
                { Color.FromArgb(99, 102, 241), Color.FromArgb(165, 180, 252) },
                { Color.FromArgb(124, 58, 237), Color.FromArgb(192, 132, 252) }
            };

            float gap = 12f;
            float tileW = (FORM_W - 48 - gap * 2) / 3f;
            float tileH = 66f;
            float top = 416f;

            for (int i = 0; i < ids.Length; i++)
            {
                int col = i % 3, row = i / 3;
                RectangleF baseRect = new RectangleF(24 + col * (tileW + gap), top + row * (tileH + gap), tileW, tileH);
                float hv = enabled[i] ? HoverOf(ids[i]) : 0f;
                RectangleF r = baseRect;
                r.Y -= 2.5f * hv;
                if (_pressId == ids[i]) r.Inflate(-2f, -2f);

                Color ga = grads[i, 0], gb = grads[i, 1];

                if (hv > 0.01f || active[i])
                    DrawSoftShadow(g, r, 16, Color.FromArgb((int)(active[i] ? 55 : 40 * hv), ga), 5, 2.5f);

                FillGlass(g, r, 16, p, hv);

                if (active[i])
                {
                    using (GraphicsPath pp = RoundRect(r, 16))
                    {
                        using (LinearGradientBrush ob = new LinearGradientBrush(r, Color.FromArgb(_isDark ? 50 : 34, ga), Color.FromArgb(_isDark ? 18 : 10, gb), 0f))
                            g.FillPath(ob, pp);
                        using (LinearGradientBrush pb = new LinearGradientBrush(r, ga, gb, 0f))
                        using (Pen pen = new Pen(pb, 1.6f))
                            g.DrawPath(pen, pp);
                    }
                }

                RectangleF ic = new RectangleF(r.X + 14, r.Y + 15, 36, 36);
                int ia = enabled[i] ? 255 : 90;
                using (GraphicsPath ip = RoundRect(ic, 12))
                using (LinearGradientBrush ib = new LinearGradientBrush(ic, Color.FromArgb(ia, ga), Color.FromArgb(ia, gb), 45f))
                    g.FillPath(ib, ip);
                PointF icc = new PointF(ic.X + 18, ic.Y + 18);
                Color iconCol = Color.FromArgb(enabled[i] ? 255 : 170, 255, 255, 255);
                switch (ids[i])
                {
                    case "power_on":
                    case "power_off": DrawPowerIcon(g, icc, 18, iconCol); break;
                    case "cool22": DrawSnowIcon(g, icc, 18, iconCol); break;
                    case "heat25": DrawFlameIcon(g, icc, 18, iconCol); break;
                    case "silent": DrawQuietIcon(g, icc, 18, iconCol); break;
                    case "turbo": DrawBoltIcon(g, icc, 18, iconCol); break;
                }

                Color tc = enabled[i] ? p.Text : p.TextMuted;
                DrawText(g, titles[i], _fTile, tc, new RectangleF(r.X + 60, r.Y + 15, r.Width - 66, 20), SfLeft);
                DrawText(g, subs[i], _fTileSub, active[i] ? Blend(ga, p.Text, _isDark ? 0.15f : 0.25f) : p.TextMuted,
                    new RectangleF(r.X + 60, r.Y + 37, r.Width - 66, 16), SfLeft);

                if (_busyId == ids[i])
                    DrawSpinner(g, new PointF(r.Right - 18, r.Y + 18), 7, ga);

                if (enabled[i]) AddHit(ids[i], baseRect);
            }
        }

        // ------------------------------------------------------------------
        //  Call-to-action: open detailed panel (with shimmer)
        // ------------------------------------------------------------------
        private void DrawCta(Graphics g, Palette p)
        {
            bool tr = _lang == "tr";
            RectangleF cta = new RectangleF(24, 576, FORM_W - 48, 52);
            float hv = HoverOf("open_panel");
            RectangleF r = cta;
            r.Y -= 2f * hv;
            if (_pressId == "open_panel") r.Inflate(-2f, -2f);

            DrawSoftShadow(g, r, 18, Color.FromArgb((int)(60 + 60 * hv), p.Accent), 6, 3f);
            using (GraphicsPath pp = RoundRect(r, 18))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(r, Blend(p.Accent, Color.White, 0.08f * hv), p.AccentB, 0f))
                    g.FillPath(b, pp);

                GraphicsState st = g.Save();
                g.SetClip(pp);
                float cycle = 3.4f;
                float ph = (_t % cycle) / cycle;
                float bandW = 120f;
                float bx = r.X - bandW * 2 + ph * (r.Width + bandW * 4);
                RectangleF band = new RectangleF(bx, r.Y, bandW, r.Height);
                using (LinearGradientBrush sb = new LinearGradientBrush(band, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 20f))
                {
                    ColorBlend cb = new ColorBlend
                    {
                        Colors = new Color[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                        Positions = new float[] { 0f, 0.5f, 1f }
                    };
                    sb.InterpolationColors = cb;
                    g.FillRectangle(sb, band);
                }
                g.Restore(st);
            }

            string text = tr ? "Detaylı Masaüstü Kontrol Panelini Aç" : "Open Detailed Desktop Control Panel";
            SizeF sz = g.MeasureString(text, _fCta, PointF.Empty, SfLeft);
            float totalW = 26 + sz.Width + 24;
            float sx = r.X + (r.Width - totalW) / 2;
            DrawPanelIcon(g, new PointF(sx + 10, r.Y + r.Height / 2), 18, Color.White);
            DrawText(g, text, _fCta, Color.White, new RectangleF(sx + 26, r.Y, sz.Width + 6, r.Height), VCenterLeft());
            DrawArrowIcon(g, new PointF(sx + 26 + sz.Width + 14 + 4 * hv, r.Y + r.Height / 2), 14, Color.White);
            AddHit("open_panel", cta);
        }

        private void DrawFooter(Graphics g, Palette p)
        {
            bool tr = _lang == "tr";
            string sync = _lastUpdate == DateTime.MinValue ? "--:--:--" : _lastUpdate.ToString("HH:mm:ss");
            string text = (tr ? "Yerel sunucu  " : "Local server  ") + _server.BaseUrl + "     ·     " +
                          (tr ? "Canlı senkronizasyon  " : "Live sync  ") + sync;
            DrawText(g, text, _fSmall, p.TextMuted, new RectangleF(24, 640, FORM_W - 48, 16), SfCenter);
        }

        // =====================================================================
        //  Drawing helpers
        // =====================================================================

        private static StringFormat VCenterLeft()
        {
            return SfVCenterLeft;
        }

        private static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, StringFormat sf)
        {
            if (string.IsNullOrEmpty(s)) return;
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, r, sf);
        }

        private static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath path = new GraphicsPath();
            float d = rad * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawSoftShadow(Graphics g, RectangleF r, float rad, Color c, int layers, float offsetY)
        {
            if (c.A <= 0) return;
            RectangleF sr1 = r;
            sr1.Inflate(3.5f, 3.5f);
            sr1.Offset(0, offsetY + 1.5f);
            using (GraphicsPath sp1 = RoundRect(sr1, rad + 3.5f))
            using (SolidBrush b1 = new SolidBrush(Color.FromArgb(Math.Max(1, c.A / 3), c.R, c.G, c.B)))
                g.FillPath(b1, sp1);

            RectangleF sr2 = r;
            sr2.Inflate(1.5f, 1.5f);
            sr2.Offset(0, offsetY);
            using (GraphicsPath sp2 = RoundRect(sr2, rad + 1.5f))
            using (SolidBrush b2 = new SolidBrush(Color.FromArgb(Math.Max(1, c.A / 2), c.R, c.G, c.B)))
                g.FillPath(b2, sp2);
        }

        private void FillGlass(Graphics g, RectangleF r, float rad, Palette p, float hover)
        {
            using (GraphicsPath pp = RoundRect(r, rad))
            {
                using (SolidBrush b = new SolidBrush(Blend(p.Card, p.CardHover, hover)))
                    g.FillPath(b, pp);
                // inner top highlight
                RectangleF hl = new RectangleF(r.X, r.Y, r.Width, Math.Min(r.Height, 24));
                GraphicsState st = g.Save();
                g.SetClip(pp);
                using (LinearGradientBrush hb = new LinearGradientBrush(hl, Color.FromArgb(_isDark ? 14 : 70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(hb, hl);
                g.Restore(st);
                using (Pen pen = new Pen(Blend(p.CardBorder, Color.FromArgb(_isDark ? 70 : 255, p.Accent.R, p.Accent.G, p.Accent.B), hover * 0.5f), 1f))
                    g.DrawPath(pen, pp);
            }
        }

        private void DrawSpinner(Graphics g, PointF c, float r, Color col)
        {
            using (Pen bg = new Pen(Color.FromArgb(50, col), 2.2f))
                g.DrawEllipse(bg, c.X - r, c.Y - r, r * 2, r * 2);
            using (Pen pen = new Pen(col, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, c.X - r, c.Y - r, r * 2, r * 2, (_t * 360f) % 360f, 100f);
        }

        // ---- Icons (monoline vector) ----
        private static Pen IconPen(Color c, float w)
        {
            return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        }

        private static void DrawPowerIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 2f))
            {
                float r = s * 0.40f;
                g.DrawArc(pen, c.X - r, c.Y - r + s * 0.05f, r * 2, r * 2, 300, 300);
                g.DrawLine(pen, c.X, c.Y - r - s * 0.06f, c.X, c.Y - s * 0.02f);
            }
        }

        private static void DrawSnowIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 1.8f))
            {
                float r = s * 0.46f;
                for (int k = 0; k < 6; k++)
                {
                    double a = k * Math.PI / 3.0 - Math.PI / 2.0;
                    float ex = c.X + (float)Math.Cos(a) * r, ey = c.Y + (float)Math.Sin(a) * r;
                    if (k < 3) g.DrawLine(pen, c.X - (float)Math.Cos(a) * r, c.Y - (float)Math.Sin(a) * r, ex, ey);
                    float mx = c.X + (float)Math.Cos(a) * r * 0.62f, my = c.Y + (float)Math.Sin(a) * r * 0.62f;
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        double b = a + Math.PI + sgn * 0.75;
                        g.DrawLine(pen, ex - (ex - mx) * 0.0f + 0, ey, ex, ey);
                        g.DrawLine(pen, mx, my, mx + (float)Math.Cos(b) * -r * 0.28f, my + (float)Math.Sin(b) * -r * 0.28f);
                    }
                }
            }
        }

        private static void DrawFlameIcon(Graphics g, PointF c, float s, Color col)
        {
            float h = s * 0.48f;
            using (GraphicsPath path = new GraphicsPath())
            using (Pen pen = IconPen(col, 1.9f))
            {
                path.AddBezier(c.X, c.Y + h, c.X - h * 0.9f, c.Y + h, c.X - h * 0.95f, c.Y - h * 0.05f, c.X - h * 0.25f, c.Y - h * 0.4f);
                path.AddBezier(c.X - h * 0.25f, c.Y - h * 0.4f, c.X - h * 0.1f, c.Y - h * 0.65f, c.X + h * 0.05f, c.Y - h * 0.85f, c.X + h * 0.05f, c.Y - h);
                path.AddBezier(c.X + h * 0.05f, c.Y - h, c.X + h * 0.75f, c.Y - h * 0.5f, c.X + h * 0.95f, c.Y + h * 0.15f, c.X + h * 0.6f, c.Y + h * 0.65f);
                path.AddBezier(c.X + h * 0.6f, c.Y + h * 0.65f, c.X + h * 0.45f, c.Y + h * 0.92f, c.X + h * 0.22f, c.Y + h, c.X, c.Y + h);
                path.CloseFigure();
                g.DrawPath(pen, path);
                g.DrawArc(pen, c.X - h * 0.32f, c.Y + h * 0.1f, h * 0.64f, h * 0.8f, 200, 140);
            }
        }

        private static void DrawQuietIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 1.8f))
            {
                PointF[] pts =
                {
                    new PointF(c.X - s * 0.42f, c.Y - s * 0.13f),
                    new PointF(c.X - s * 0.20f, c.Y - s * 0.13f),
                    new PointF(c.X + s * 0.02f, c.Y - s * 0.34f),
                    new PointF(c.X + s * 0.02f, c.Y + s * 0.34f),
                    new PointF(c.X - s * 0.20f, c.Y + s * 0.13f),
                    new PointF(c.X - s * 0.42f, c.Y + s * 0.13f)
                };
                g.DrawPolygon(pen, pts);
                g.DrawArc(pen, c.X - s * 0.08f, c.Y - s * 0.18f, s * 0.36f, s * 0.36f, -50, 100);
            }
        }

        private static void DrawBoltIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 1.8f))
            {
                PointF[] pts =
                {
                    new PointF(c.X + s * 0.08f, c.Y - s * 0.46f),
                    new PointF(c.X - s * 0.28f, c.Y + s * 0.06f),
                    new PointF(c.X - s * 0.01f, c.Y + s * 0.06f),
                    new PointF(c.X - s * 0.08f, c.Y + s * 0.46f),
                    new PointF(c.X + s * 0.28f, c.Y - s * 0.06f),
                    new PointF(c.X + s * 0.01f, c.Y - s * 0.06f)
                };
                g.DrawPolygon(pen, pts);
            }
        }

        private static void DrawFanIcon(Graphics g, PointF c, float s, Color col, float angle)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform(angle);
            using (SolidBrush b = new SolidBrush(col))
            {
                for (int k = 0; k < 3; k++)
                {
                    g.RotateTransform(120f);
                    g.FillEllipse(b, -s * 0.13f, -s * 0.5f, s * 0.26f, s * 0.42f);
                }
            }
            g.Restore(st);
            using (SolidBrush cb = new SolidBrush(col))
                g.FillEllipse(cb, c.X - s * 0.1f, c.Y - s * 0.1f, s * 0.2f, s * 0.2f);
        }

        private static void DrawSunIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 1.7f))
            {
                float r = s * 0.22f;
                g.DrawEllipse(pen, c.X - r, c.Y - r, r * 2, r * 2);
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4.0;
                    g.DrawLine(pen, c.X + (float)Math.Cos(a) * s * 0.36f, c.Y + (float)Math.Sin(a) * s * 0.36f,
                                    c.X + (float)Math.Cos(a) * s * 0.5f, c.Y + (float)Math.Sin(a) * s * 0.5f);
                }
            }
        }

        private static void DrawMoonIcon(Graphics g, PointF c, float s, Color col)
        {
            float r = s * 0.45f;
            using (GraphicsPath outer = new GraphicsPath())
            using (GraphicsPath inner = new GraphicsPath())
            {
                outer.AddEllipse(c.X - r, c.Y - r, r * 2, r * 2);
                inner.AddEllipse(c.X - r * 0.35f, c.Y - r * 1.25f, r * 2, r * 2);
                using (Region reg = new Region(outer))
                using (SolidBrush b = new SolidBrush(col))
                {
                    reg.Exclude(inner);
                    g.FillRegion(b, reg);
                }
            }
        }

        private static void DrawUserIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 1.7f))
            {
                float hr = s * 0.2f;
                g.DrawEllipse(pen, c.X - hr, c.Y - s * 0.42f, hr * 2, hr * 2);
                g.DrawArc(pen, c.X - s * 0.38f, c.Y + s * 0.02f, s * 0.76f, s * 0.7f, 200, 140);
            }
        }

        private static void DrawAcGlyph(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 1.5f))
            {
                RectangleF body = new RectangleF(c.X - s * 0.5f, c.Y - s * 0.28f, s, s * 0.4f);
                using (GraphicsPath bp = RoundRect(body, 2.5f))
                    g.DrawPath(pen, bp);
                g.DrawLine(pen, body.X + s * 0.15f, body.Bottom - s * 0.11f, body.Right - s * 0.15f, body.Bottom - s * 0.11f);
                g.DrawLine(pen, c.X - s * 0.22f, c.Y + s * 0.26f, c.X - s * 0.28f, c.Y + s * 0.4f);
                g.DrawLine(pen, c.X, c.Y + s * 0.26f, c.X, c.Y + s * 0.42f);
                g.DrawLine(pen, c.X + s * 0.22f, c.Y + s * 0.26f, c.X + s * 0.28f, c.Y + s * 0.4f);
            }
        }

        private static void DrawPanelIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 1.7f))
            {
                RectangleF r = new RectangleF(c.X - s * 0.5f, c.Y - s * 0.4f, s, s * 0.8f);
                using (GraphicsPath rp = RoundRect(r, 3f))
                    g.DrawPath(pen, rp);
                g.DrawLine(pen, r.X, r.Y + s * 0.24f, r.Right, r.Y + s * 0.24f);
                g.DrawLine(pen, r.X + s * 0.32f, r.Y + s * 0.24f, r.X + s * 0.32f, r.Bottom);
            }
        }

        private static void DrawArrowIcon(Graphics g, PointF c, float s, Color col)
        {
            using (Pen pen = IconPen(col, 2f))
            {
                g.DrawLine(pen, c.X - s * 0.45f, c.Y, c.X + s * 0.45f, c.Y);
                g.DrawLine(pen, c.X + s * 0.1f, c.Y - s * 0.35f, c.X + s * 0.45f, c.Y);
                g.DrawLine(pen, c.X + s * 0.1f, c.Y + s * 0.35f, c.X + s * 0.45f, c.Y);
            }
        }

        // =====================================================================
        //  Tray icon & external window
        // =====================================================================

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void SetupTrayIcon()
        {
            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Kontrol Merkezini Göster / Show Control Center", (s, e) => ShowFromTray());
            trayMenu.MenuItems.Add("Detaylı Masaüstü Ekranını Aç / Open Full UI", (s, e) => LaunchAppWindow(_server.BaseUrl + "?lang=" + _lang + "&theme=" + (_isDark ? "dark" : "light")));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Klimayı Aç / Turn On", (s, e) => HandleAction("power_on"));
            trayMenu.MenuItems.Add("Klimayı Kapat / Turn Off", (s, e) => HandleAction("power_off"));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Çıkış / Exit", (s, e) => { _trayIcon.Visible = false; Application.Exit(); });

            _trayIcon = new NotifyIcon
            {
                Text = "Haier hOn Klima Kontrol Merkezi",
                Icon = _appIcon ?? SystemIcons.Application,
                ContextMenu = trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => ShowFromTray();
        }

        public static void LaunchAppWindow(string url)
        {
            try
            {
                // 1. First respect the user's own default browser (Chrome, Edge, Opera, Brave, Vivaldi, Firefox, etc.)
                string defaultExe = ResolveUserDefaultBrowserExe();
                if (!string.IsNullOrEmpty(defaultExe) && File.Exists(defaultExe))
                {
                    if (SupportsChromiumAppFlag(defaultExe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = defaultExe,
                            Arguments = "--app=\"" + url + "\" --window-size=1360,900",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = defaultExe,
                            Arguments = "\"" + url + "\"",
                            UseShellExecute = true
                        });
                    }
                    return;
                }

                // 2. Fallback: scan common installed browsers on the user's PC
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                string[] candidateExes = new string[]
                {
                    Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(localApp, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(pf86, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(localApp, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(localApp, @"Programs\Opera\opera.exe"),
                    Path.Combine(localApp, @"Programs\Opera GX\opera.exe"),
                    Path.Combine(pf, @"Opera\opera.exe"),
                    Path.Combine(localApp, @"Vivaldi\Application\vivaldi.exe"),
                    Path.Combine(pf, @"Vivaldi\Application\vivaldi.exe"),
                    Path.Combine(localApp, @"Yandex\YandexBrowser\Application\browser.exe"),
                    Path.Combine(pf, @"Mozilla Firefox\firefox.exe"),
                    Path.Combine(pf86, @"Mozilla Firefox\firefox.exe"),
                    Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                    Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe")
                };

                foreach (string browserExe in candidateExes)
                {
                    if (!string.IsNullOrEmpty(browserExe) && File.Exists(browserExe))
                    {
                        string args = SupportsChromiumAppFlag(browserExe)
                            ? ("--app=\"" + url + "\" --window-size=1360,900")
                            : ("\"" + url + "\"");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = browserExe,
                            Arguments = args,
                            UseShellExecute = true
                        });
                        return;
                    }
                }

                // 3. Final fallback: ask Windows Shell to open the URL in whatever handler is registered
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(url); } catch { }
            }
        }

        private static bool SupportsChromiumAppFlag(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            string name = Path.GetFileName(exePath).ToLowerInvariant();
            return name == "chrome.exe" ||
                   name == "msedge.exe" ||
                   name == "brave.exe" ||
                   name == "vivaldi.exe" ||
                   name == "chromium.exe" ||
                   name == "thorium.exe";
        }

        private static string ResolveUserDefaultBrowserExe()
        {
            try
            {
                string progId = null;
                using (RegistryKey userChoice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
                {
                    if (userChoice != null)
                    {
                        object val = userChoice.GetValue("ProgId");
                        if (val != null) progId = val.ToString();
                    }
                }

                if (!string.IsNullOrEmpty(progId))
                {
                    using (RegistryKey cmdKey = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                    {
                        if (cmdKey != null)
                        {
                            object cmdVal = cmdKey.GetValue(null);
                            if (cmdVal != null)
                            {
                                string parsed = ExtractExeFromCommandLine(cmdVal.ToString());
                                if (!string.IsNullOrEmpty(parsed) && File.Exists(parsed))
                                    return parsed;
                            }
                        }
                    }
                }

                using (RegistryKey httpCmd = Registry.ClassesRoot.OpenSubKey(@"http\shell\open\command"))
                {
                    if (httpCmd != null)
                    {
                        object cmdVal = httpCmd.GetValue(null);
                        if (cmdVal != null)
                        {
                            string parsed = ExtractExeFromCommandLine(cmdVal.ToString());
                            if (!string.IsNullOrEmpty(parsed) && File.Exists(parsed))
                                return parsed;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string ExtractExeFromCommandLine(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return null;
            string s = commandLine.Trim();
            if (s.StartsWith("\""))
            {
                int endQuote = s.IndexOf('"', 1);
                if (endQuote > 1)
                    return s.Substring(1, endQuote - 1);
            }
            int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0)
            {
                return s.Substring(0, exeIdx + 4).Trim();
            }
            return null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                _animTimer.Stop();
                _dataTimer.Stop();
                _tempDebounce.Stop();
            }
            catch { }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.OnFormClosing(e);
        }
    }
}
