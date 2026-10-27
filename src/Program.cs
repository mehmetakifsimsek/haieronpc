using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HaierHonDesktop
{
    public class NativeLoginDialog : Form
    {
        public string Email { get { return _txtEmail.Text.Trim(); } }
        public string Password { get { return _txtPassword.Text; } }

        private TextBox _txtEmail;
        private TextBox _txtPassword;

        public NativeLoginDialog(bool isDark, string lang)
        {
            bool tr = lang != "en";
            Text = tr ? "Haier hOn Hesap Bağlantısı" : "Haier hOn Account Sign-In";
            Size = new Size(410, 265);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            BackColor = isDark ? Color.FromArgb(13, 24, 48) : Color.FromArgb(236, 244, 255);
            ForeColor = isDark ? Color.FromArgb(241, 246, 255) : Color.FromArgb(14, 30, 62);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            Label lblEmail = new Label
            {
                Text = tr ? "hOn E-posta Adresi" : "hOn Email Address",
                Location = new Point(24, 22),
                Size = new Size(345, 20),
                Font = new Font("Segoe UI Semibold", 9.25f, FontStyle.Bold)
            };

            _txtEmail = new TextBox
            {
                Location = new Point(24, 46),
                Size = new Size(345, 28),
                BackColor = isDark ? Color.FromArgb(24, 42, 78) : Color.White,
                ForeColor = isDark ? Color.White : Color.FromArgb(14, 30, 62),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label lblPass = new Label
            {
                Text = tr ? "hOn Şifresi" : "hOn Password",
                Location = new Point(24, 88),
                Size = new Size(345, 20),
                Font = new Font("Segoe UI Semibold", 9.25f, FontStyle.Bold)
            };

            _txtPassword = new TextBox
            {
                Location = new Point(24, 112),
                Size = new Size(345, 28),
                PasswordChar = '●',
                BackColor = isDark ? Color.FromArgb(24, 42, 78) : Color.White,
                ForeColor = isDark ? Color.White : Color.FromArgb(14, 30, 62),
                BorderStyle = BorderStyle.FixedSingle
            };

            Button btnCancel = new Button
            {
                Text = tr ? "İptal" : "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(175, 166),
                Size = new Size(92, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = isDark ? Color.FromArgb(35, 55, 95) : Color.FromArgb(214, 228, 248),
                ForeColor = isDark ? Color.White : Color.FromArgb(14, 30, 62)
            };
            btnCancel.FlatAppearance.BorderSize = 0;

            Button btnOk = new Button
            {
                Text = tr ? "Giriş Yap" : "Sign In",
                DialogResult = DialogResult.OK,
                Location = new Point(277, 166),
                Size = new Size(92, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold)
            };
            btnOk.FlatAppearance.BorderSize = 0;

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[] { lblEmail, _txtEmail, lblPass, _txtPassword, btnCancel, btnOk });
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool headless = false;
            int port = 5890;

            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase))
                        headless = true;
                    else if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(a.Substring(7), out port);
                }
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string uiPath = Path.Combine(baseDir, "ui", "index.html");
            if (!File.Exists(uiPath))
            {
                string parentUi = Path.GetFullPath(Path.Combine(baseDir, "..", "ui", "index.html"));
                if (File.Exists(parentUi))
                    uiPath = parentUi;
            }

            HonCloudClient client = new HonCloudClient();
            LocalWebServer server = new LocalWebServer(client, uiPath, port);
            server.Start();

            if (headless)
            {
                Console.WriteLine("HAIER_HON_DESKTOP_READY " + server.BaseUrl);
                Console.Out.Flush();
                ManualResetEvent waitForever = new ManualResetEvent(false);
                waitForever.WaitOne();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainControlForm(client, server));
            server.Stop();
        }
    }
}
