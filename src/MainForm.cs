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
        const int HotkeyBothOn = 3;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        static readonly Color[] OutputColors = { Color.FromArgb(37, 99, 235), Color.FromArgb(234, 88, 12) };
        static readonly Color MutedColor = Color.FromArgb(120, 120, 120);

        ComboBox cboIn;
        readonly ComboBox[] cboOut = new ComboBox[AudioRouter.OutputCount];
        readonly Button[] btnMute = new Button[AudioRouter.OutputCount];
        readonly LevelMeter[] meterOut = new LevelMeter[AudioRouter.OutputCount];
        readonly bool[] muted = new bool[AudioRouter.OutputCount];
        Button btnRefresh, btnStart, btnBothOn;
        LevelMeter meterIn;
        Label lblStatus, lblCable;
        CheckBox chkTop;
        Timer timer;

        AudioRouter router;
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
            AddLabel("出力1", 16, 80);
            cboOut[0] = AddCombo(70, 76);
            AddLabel("出力2", 16, 114);
            cboOut[1] = AddCombo(70, 110);
            for (int i = 0; i < cboOut.Length; i++) cboOut[i].Width = 376;

            lblCable = AddLabel("", 16, 142);
            lblCable.ForeColor = Color.Firebrick;
            lblCable.AutoSize = false;
            lblCable.Size = new Size(430, 20);

            btnRefresh = new Button { Text = "デバイス再読込", Location = new Point(16, 166), Size = new Size(140, 34) };
            btnRefresh.Click += delegate { LoadDevices(cboIn.Text, cboOut[0].Text, cboOut[1].Text); };
            Controls.Add(btnRefresh);

            btnStart = new Button { Text = "開始", Location = new Point(306, 166), Size = new Size(140, 34) };
            btnStart.Click += delegate { if (router == null) StartRouting(); else StopRouting(null); };
            Controls.Add(btnStart);

            AddLabel("出力のON／ミュート（クリックで切り替え）", 16, 216);
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                int n = i;
                Button b = new Button { Location = new Point(16 + i * 222, 240), Size = new Size(208, 76), FlatStyle = FlatStyle.Flat };
                b.Font = new Font("Meiryo UI", 11F, FontStyle.Bold);
                b.Click += delegate { SetMuted(n, !muted[n]); };
                Controls.Add(b);
                btnMute[i] = b;
            }
            btnBothOn = new Button { Text = "両方ON（Ctrl+Alt+3）", Location = new Point(16, 322), Size = new Size(430, 32) };
            btnBothOn.Click += delegate { SetMuted(0, false); SetMuted(1, false); };
            Controls.Add(btnBothOn);

            AddLabel("入力", 16, 370);
            meterIn = AddMeter(370);
            AddLabel("出力1", 16, 398);
            meterOut[0] = AddMeter(398);
            AddLabel("出力2", 16, 426);
            meterOut[1] = AddMeter(426);

            chkTop = new CheckBox { Text = "常に手前に表示", Location = new Point(16, 458), AutoSize = true };
            chkTop.CheckedChanged += delegate { TopMost = chkTop.Checked; };
            Controls.Add(chkTop);

            lblStatus = AddLabel("停止中", 16, 488);
            lblStatus.AutoSize = false;
            lblStatus.Size = new Size(430, 44);

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(462, 540);
            ResumeLayout(false);

            timer = new Timer { Interval = 50 };
            timer.Tick += OnTick;
            timer.Start();

            LoadSettings();
            UpdateMuteButtons();
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

        LevelMeter AddMeter(int y)
        {
            LevelMeter m = new LevelMeter { Location = new Point(70, y), Size = new Size(376, 20) };
            Controls.Add(m);
            return m;
        }

        void SetMuted(int output, bool value)
        {
            muted[output] = value;
            if (router != null) router.SetMuted(output, value);
            UpdateMuteButtons();
        }

        void UpdateMuteButtons()
        {
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                Button b = btnMute[i];
                Color c = OutputColors[i];
                string head = "出力" + (i + 1) + "（Ctrl+Alt+" + (i + 1) + "）";
                b.Text = head + "\n" + (muted[i] ? "ミュート中" : "ON");
                b.BackColor = muted[i] ? SystemColors.Control : c;
                b.ForeColor = muted[i] ? MutedColor : Color.White;
                b.FlatAppearance.BorderColor = muted[i] ? MutedColor : c;
                b.FlatAppearance.BorderSize = muted[i] ? 1 : 3;
            }
            UpdateStatus();
        }

        void LoadDevices(string preferIn, string preferOut1, string preferOut2)
        {
            string[] ins = AudioRouter.GetInputDevices();
            string[] outs = AudioRouter.GetOutputDevices();
            cboIn.Items.Clear();
            cboIn.Items.AddRange(ins);

            cboIn.SelectedIndex = FindExact(ins, preferIn);
            if (cboIn.SelectedIndex < 0) cboIn.SelectedIndex = FindContains(ins, "マイク", 0);
            if (cboIn.SelectedIndex < 0) cboIn.SelectedIndex = FindContains(ins, "Mic", 0);
            if (cboIn.SelectedIndex < 0 && ins.Length > 0) cboIn.SelectedIndex = 0;

            string[] prefer = { preferOut1, preferOut2 };
            int cables = 0;
            for (int i = 0; i < outs.Length; i++) if (IsCable(outs[i])) cables++;
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                cboOut[i].Items.Clear();
                cboOut[i].Items.AddRange(outs);
                int idx = FindExact(outs, prefer[i]);
                if (idx < 0) idx = FindContains(outs, "CABLE", i);   // 見つかった仮想ケーブルを順に割り当てる
                cboOut[i].SelectedIndex = idx;
            }

            lblCable.Text = cables < 2 ? "※ 仮想ケーブルが2本見つかりません（README参照）" : "";
        }

        static bool IsCable(string name)
        {
            return name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int FindExact(string[] names, string exact)
        {
            if (string.IsNullOrEmpty(exact)) return -1;
            return Array.IndexOf(names, exact);
        }

        static int FindContains(string[] names, string part, int nth)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (nth-- == 0) return i;
            }
            return -1;
        }

        void StartRouting()
        {
            if (cboIn.SelectedIndex < 0 || cboOut[0].SelectedIndex < 0 || cboOut[1].SelectedIndex < 0)
            {
                MessageBox.Show(this, "入力と出力1・出力2のデバイスを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (cboOut[0].SelectedIndex == cboOut[1].SelectedIndex)
            {
                MessageBox.Show(this, "出力1と出力2には別々のデバイスを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!IsCable(cboOut[0].Text) || !IsCable(cboOut[1].Text))
            {
                DialogResult ans = MessageBox.Show(this,
                    "出力先が仮想ケーブルではないようです。\nスピーカーに出すとハウリングすることがあります。続けますか？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return;
            }

            AudioRouter r = new AudioRouter(cboIn.SelectedIndex, new int[] { cboOut[0].SelectedIndex, cboOut[1].SelectedIndex });
            for (int i = 0; i < AudioRouter.OutputCount; i++) r.SetMuted(i, muted[i]);
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
            SetDeviceControlsEnabled(false);
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
            SetDeviceControlsEnabled(true);
            meterIn.Level = 0;
            foreach (LevelMeter m in meterOut) m.Level = 0;
            UpdateStatus();
            if (error != null)
                MessageBox.Show(this, "停止しました。\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void SetDeviceControlsEnabled(bool enabled)
        {
            cboIn.Enabled = btnRefresh.Enabled = enabled;
            foreach (ComboBox c in cboOut) c.Enabled = enabled;
        }

        void UpdateStatus()
        {
            if (lblStatus == null) return;
            string s = (router != null ? "動作中" : "停止中") + "　／　"
                + "出力1: " + (muted[0] ? "ミュート" : "ON") + "　出力2: " + (muted[1] ? "ミュート" : "ON");
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
            meterIn.Level = router.InputPeak / 32768.0;
            for (int i = 0; i < AudioRouter.OutputCount; i++)
                meterOut[i].Level = router.GetOutputPeak(i) / 32768.0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hotkeysOk = true;
            for (int id = 1; id <= 3; id++)
                hotkeysOk &= RegisterHotKey(Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x30 + id);
            UpdateStatus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyBothOn) { SetMuted(0, false); SetMuted(1, false); }
                else if (id >= 1 && id <= AudioRouter.OutputCount) SetMuted(id - 1, !muted[id - 1]);
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            timer.Stop();
            StopRouting(null);
            for (int id = 1; id <= 3; id++) UnregisterHotKey(Handle, id);
            base.OnFormClosing(e);
        }

        void LoadSettings()
        {
            string inName = null, out1 = null, out2 = null;
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
                        else if (key == "output1") out1 = val;
                        else if (key == "output2") out2 = val;
                        else if (key == "mute1") muted[0] = val == "1";
                        else if (key == "mute2") muted[1] = val == "1";
                        else if (key == "topmost") chkTop.Checked = val == "1";
                    }
                }
            }
            catch (Exception) { }
            LoadDevices(inName, out1, out2);
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllLines(settingsPath, new string[] {
                    "input=" + cboIn.Text,
                    "output1=" + cboOut[0].Text,
                    "output2=" + cboOut[1].Text,
                    "mute1=" + (muted[0] ? "1" : "0"),
                    "mute2=" + (muted[1] ? "1" : "0"),
                    "topmost=" + (chkTop.Checked ? "1" : "0"),
                });
            }
            catch (Exception) { }
        }
    }

    // 音量レベルを dB 表示するシンプルなメーター
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
