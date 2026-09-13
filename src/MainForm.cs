using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChannelSwitcher
{
    public class MainForm : Form
    {
        const int WM_HOTKEY = 0x0312;
        const int MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        static readonly Color LeftColor = Color.FromArgb(37, 99, 235);
        static readonly Color BothColor = Color.FromArgb(22, 163, 74);
        static readonly Color RightColor = Color.FromArgb(234, 88, 12);

        ComboBox cboIn, cboOut;
        Button btnRefresh, btnStart, btnLeft, btnBoth, btnRight;
        LevelMeter meterL, meterR;
        Label lblStatus, lblCable;
        CheckBox chkTop;
        Timer timer;

        AudioRouter router;
        ChannelMode mode = ChannelMode.Both;
        bool hotkeysOk;

        readonly string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChannelSwitcher", "settings.txt");

        public MainForm()
        {
            SuspendLayout();
            Text = "Channel Switcher";
            Font = new Font("Meiryo UI", 10F);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            AddLabel("入力（マイク）", 16, 16);
            cboIn = AddCombo(16, 40);
            AddLabel("出力（仮想ケーブル：CABLE Input）", 16, 76);
            cboOut = AddCombo(16, 100);
            lblCable = AddLabel("", 16, 130);
            lblCable.ForeColor = Color.Firebrick;
            lblCable.AutoSize = false;
            lblCable.Size = new Size(430, 20);

            btnRefresh = new Button { Text = "デバイス再読込", Location = new Point(16, 156), Size = new Size(140, 34) };
            btnRefresh.Click += delegate { LoadDevices(cboIn.Text, cboOut.Text); };
            Controls.Add(btnRefresh);

            btnStart = new Button { Text = "開始", Location = new Point(306, 156), Size = new Size(140, 34) };
            btnStart.Click += delegate { if (router == null) StartRouting(); else StopRouting(null); };
            Controls.Add(btnStart);

            AddLabel("マイクを挿す位置", 16, 208);
            btnLeft = AddModeButton("左のみ\nCtrl+Alt+1", 16, ChannelMode.Left);
            btnBoth = AddModeButton("両方\nCtrl+Alt+2", 164, ChannelMode.Both);
            btnRight = AddModeButton("右のみ\nCtrl+Alt+3", 312, ChannelMode.Right);

            AddLabel("L", 16, 318);
            meterL = new LevelMeter { Location = new Point(36, 318), Size = new Size(410, 20) };
            Controls.Add(meterL);
            AddLabel("R", 16, 346);
            meterR = new LevelMeter { Location = new Point(36, 346), Size = new Size(410, 20) };
            Controls.Add(meterR);

            chkTop = new CheckBox { Text = "常に手前に表示", Location = new Point(16, 380), AutoSize = true };
            chkTop.CheckedChanged += delegate { TopMost = chkTop.Checked; };
            Controls.Add(chkTop);

            lblStatus = AddLabel("停止中", 16, 410);
            lblStatus.AutoSize = false;
            lblStatus.Size = new Size(430, 44);

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(462, 464);
            ResumeLayout(false);

            timer = new Timer { Interval = 50 };
            timer.Tick += OnTick;
            timer.Start();

            LoadSettings();
            UpdateModeButtons();
        }

        Label AddLabel(string text, int x, int y)
        {
            Label l = new Label { Text = text, Location = new Point(x, y), AutoSize = true };
            Controls.Add(l);
            return l;
        }

        ComboBox AddCombo(int x, int y)
        {
            ComboBox c = new ComboBox { Location = new Point(x, y), Size = new Size(430, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(c);
            return c;
        }

        Button AddModeButton(string text, int x, ChannelMode m)
        {
            Button b = new Button { Text = text, Location = new Point(x, 232), Size = new Size(134, 70), FlatStyle = FlatStyle.Flat };
            b.Font = new Font("Meiryo UI", 11F, FontStyle.Bold);
            b.Click += delegate { SetMode(m); };
            Controls.Add(b);
            return b;
        }

        void SetMode(ChannelMode m)
        {
            mode = m;
            if (router != null) router.Mode = m;
            UpdateModeButtons();
        }

        void UpdateModeButtons()
        {
            StyleModeButton(btnLeft, mode == ChannelMode.Left, LeftColor);
            StyleModeButton(btnBoth, mode == ChannelMode.Both, BothColor);
            StyleModeButton(btnRight, mode == ChannelMode.Right, RightColor);
            UpdateStatus();
        }

        static void StyleModeButton(Button b, bool selected, Color color)
        {
            b.BackColor = selected ? color : SystemColors.Control;
            b.ForeColor = selected ? Color.White : SystemColors.ControlText;
            b.FlatAppearance.BorderColor = color;
            b.FlatAppearance.BorderSize = selected ? 3 : 1;
        }

        void LoadDevices(string preferIn, string preferOut)
        {
            string[] ins = AudioRouter.GetInputDevices();
            string[] outs = AudioRouter.GetOutputDevices();
            cboIn.Items.Clear();
            cboIn.Items.AddRange(ins);
            cboOut.Items.Clear();
            cboOut.Items.AddRange(outs);

            cboIn.SelectedIndex = FindIndex(ins, preferIn, null);
            if (cboIn.SelectedIndex < 0) cboIn.SelectedIndex = FindIndex(ins, null, "マイク");
            if (cboIn.SelectedIndex < 0) cboIn.SelectedIndex = FindIndex(ins, null, "Mic");
            if (cboIn.SelectedIndex < 0 && ins.Length > 0) cboIn.SelectedIndex = 0;

            int cable = FindIndex(outs, null, "CABLE Input");
            cboOut.SelectedIndex = FindIndex(outs, preferOut, null);
            if (cboOut.SelectedIndex < 0) cboOut.SelectedIndex = cable;

            lblCable.Text = cable < 0 ? "※ VB-CABLE が見つかりません（README参照）" : "";
        }

        static int FindIndex(string[] names, string exact, string contains)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(exact) && names[i] == exact) return i;
                if (!string.IsNullOrEmpty(contains) && names[i].IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return -1;
        }

        void StartRouting()
        {
            if (cboIn.SelectedIndex < 0 || cboOut.SelectedIndex < 0)
            {
                MessageBox.Show(this, "入力と出力のデバイスを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string outName = cboOut.Text;
            if (outName.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) < 0)
            {
                DialogResult ans = MessageBox.Show(this,
                    "出力先が仮想ケーブルではないようです。\nスピーカーに出すとハウリングすることがあります。続けますか？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return;
            }

            AudioRouter r = new AudioRouter(cboIn.SelectedIndex, cboOut.SelectedIndex);
            r.Mode = mode;
            try
            {
                r.Start();
            }
            catch (Exception ex)
            {
                r.Dispose();
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            router = r;
            btnStart.Text = "停止";
            cboIn.Enabled = cboOut.Enabled = btnRefresh.Enabled = false;
            UpdateStatus();
        }

        void StopRouting(string error)
        {
            if (router != null)
            {
                router.Dispose();
                router = null;
            }
            btnStart.Text = "開始";
            cboIn.Enabled = cboOut.Enabled = btnRefresh.Enabled = true;
            meterL.Level = 0;
            meterR.Level = 0;
            UpdateStatus();
            if (error != null)
                MessageBox.Show(this, "停止しました。\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void UpdateStatus()
        {
            if (lblStatus == null) return;
            string modeText = mode == ChannelMode.Left ? "左のみ" : mode == ChannelMode.Right ? "右のみ" : "両方";
            string s = (router != null ? "動作中" : "停止中") + "　／　モード: " + modeText;
            if (!hotkeysOk) s += "\n※ ショートカットは他のアプリと重複して使えません（ボタンで操作してください）";
            lblStatus.Text = s;
        }

        void OnTick(object sender, EventArgs e)
        {
            if (router == null) return;
            if (router.LastError != null)
            {
                StopRouting(router.LastError);
                return;
            }
            meterL.Level = router.PeakLeft / 32768.0;
            meterR.Level = router.PeakRight / 32768.0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hotkeysOk = true;
            for (int i = 0; i < 3; i++)
                hotkeysOk &= RegisterHotKey(Handle, i + 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x31 + i);
            UpdateStatus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) SetMode(ChannelMode.Left);
                else if (id == 2) SetMode(ChannelMode.Both);
                else if (id == 3) SetMode(ChannelMode.Right);
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            timer.Stop();
            StopRouting(null);
            for (int i = 1; i <= 3; i++) UnregisterHotKey(Handle, i);
            base.OnFormClosing(e);
        }

        void LoadSettings()
        {
            string inName = null, outName = null;
            try
            {
                if (File.Exists(settingsPath))
                {
                    foreach (string line in File.ReadAllLines(settingsPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                        if (key == "input") inName = val;
                        else if (key == "output") outName = val;
                        else if (key == "mode") { int v; if (int.TryParse(val, out v) && v >= 0 && v <= 2) mode = (ChannelMode)v; }
                        else if (key == "topmost") chkTop.Checked = val == "1";
                    }
                }
            }
            catch (Exception) { }
            LoadDevices(inName, outName);
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllLines(settingsPath, new string[] {
                    "input=" + cboIn.Text,
                    "output=" + cboOut.Text,
                    "mode=" + (int)mode,
                    "topmost=" + (chkTop.Checked ? "1" : "0"),
                });
            }
            catch (Exception) { }
        }
    }

    // 入力レベルを dB 表示するシンプルなメーター
    public class LevelMeter : Control
    {
        double shown;

        public LevelMeter()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public double Level
        {
            set
            {
                double db = value <= 0 ? -60 : 20 * Math.Log10(value);
                double pos = Math.Max(0, Math.Min(1, (db + 60) / 60));
                shown = pos >= shown ? pos : Math.Max(pos, shown - 0.04);
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(40, 40, 40));
            int w = (int)(Width * shown);
            int yellow = (int)(Width * 0.8), red = (int)(Width * 0.95);
            using (Brush green = new SolidBrush(Color.FromArgb(34, 197, 94)))
            using (Brush yel = new SolidBrush(Color.FromArgb(234, 179, 8)))
            using (Brush rd = new SolidBrush(Color.FromArgb(239, 68, 68)))
            {
                g.FillRectangle(green, 0, 0, Math.Min(w, yellow), Height);
                if (w > yellow) g.FillRectangle(yel, yellow, 0, Math.Min(w, red) - yellow, Height);
                if (w > red) g.FillRectangle(rd, red, 0, w - red, Height);
            }
        }
    }
}
