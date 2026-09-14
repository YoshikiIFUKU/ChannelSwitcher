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
        const int TrackMax = 1000;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // 音声認識側のUIに合わせた色（出力1: #F7E4D5、出力2: #D7EEC3）
        static readonly Color[] OutputColors = { ColorTranslator.FromHtml("#F7E4D5"), ColorTranslator.FromHtml("#D7EEC3") };
        static readonly Color ActiveTextColor = Color.FromArgb(32, 32, 32);
        static readonly Color MutedColor = Color.FromArgb(120, 120, 120);

        RadioButton rbMic, rbFile;
        Panel pnlMic, pnlFile, pnlLower;

        ComboBox cboIn;

        TextBox txtFile;
        Button btnBrowse, btnPlay, btnStopPlay;
        Label lblFileInfo, lblTime;
        readonly ComboBox[] cboSource = new ComboBox[AudioRouter.OutputCount];
        ComboBox cboMonitor;
        TrackBar trkPos;

        readonly ComboBox[] cboOut = new ComboBox[AudioRouter.OutputCount];
        readonly Button[] btnMute = new Button[AudioRouter.OutputCount];
        readonly LevelMeter[] meterOut = new LevelMeter[AudioRouter.OutputCount];
        readonly bool[] muted = new bool[AudioRouter.OutputCount];
        readonly TrackBar[] trkVolume = new TrackBar[AudioRouter.OutputCount];
        readonly Label[] lblVolume = new Label[AudioRouter.OutputCount];
        const int MaxVolumePercent = 200;
        Button btnRefresh, btnStart, btnBothOn;
        LevelMeter meterIn;
        Label lblMeterIn, lblStatus, lblCable;
        CheckBox chkTop;
        Timer timer;

        AudioRouter router;
        FilePlayer player;
        double fileDuration;
        double filePosition;
        bool seeking;
        string finishedNote;
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
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            AddLabel(this, "音源", 16, 18);
            rbMic = new RadioButton { Text = "マイク", Location = new Point(70, 16), AutoSize = true, Checked = true };
            rbFile = new RadioButton { Text = "音声ファイル（WAV）", Location = new Point(160, 16), AutoSize = true };
            rbMic.CheckedChanged += delegate { UpdateSourceMode(); };
            Controls.Add(rbMic);
            Controls.Add(rbFile);

            // --- マイクモード ---
            pnlMic = new Panel { Location = new Point(0, 48), Size = new Size(462, 64) };
            AddLabel(pnlMic, "入力（マイク）", 16, 0);
            cboIn = AddCombo(pnlMic, 16, 24, 430);
            Controls.Add(pnlMic);

            // --- 音声ファイルモード ---
            pnlFile = new Panel { Location = new Point(0, 48), Size = new Size(462, 214), Visible = false };
            AddLabel(pnlFile, "ファイル", 16, 4);
            txtFile = new TextBox { Location = new Point(86, 0), Size = new Size(274, 28), ReadOnly = true };
            pnlFile.Controls.Add(txtFile);
            btnBrowse = new Button { Text = "参照...", Location = new Point(366, 0), Size = new Size(80, 28) };
            btnBrowse.Click += delegate { BrowseFile(); };
            pnlFile.Controls.Add(btnBrowse);
            lblFileInfo = AddLabel(pnlFile, "WAV ファイルを選ぶか、ウィンドウにドラッグしてください", 86, 32);
            lblFileInfo.ForeColor = MutedColor;

            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                int n = i;
                AddLabel(pnlFile, "出力" + (i + 1) + "に流す", 16 + i * 222, 64);
                ComboBox c = AddCombo(pnlFile, 106 + i * 222, 60, 104);
                c.Items.AddRange(new object[] { "左チャンネル", "右チャンネル" });
                c.SelectedIndex = i;   // 既定は 出力1 = 左、出力2 = 右
                c.SelectedIndexChanged += delegate { if (player != null) player.SetSource(n, (SourceChannel)cboSource[n].SelectedIndex); };
                cboSource[i] = c;
            }
            AddLabel(pnlFile, "モニター", 16, 98);
            cboMonitor = AddCombo(pnlFile, 106, 94, 340);

            btnPlay = new Button { Text = "▶ 再生", Location = new Point(16, 130), Size = new Size(120, 34) };
            btnPlay.Click += delegate { OnPlayClick(); };
            pnlFile.Controls.Add(btnPlay);
            btnStopPlay = new Button { Text = "■ 停止", Location = new Point(142, 130), Size = new Size(96, 34) };
            btnStopPlay.Click += delegate { StopPlayback(null, true); };
            pnlFile.Controls.Add(btnStopPlay);
            lblTime = AddLabel(pnlFile, "00:00 / 00:00", 256, 138);

            trkPos = new TrackBar { Location = new Point(10, 170), Size = new Size(442, 40), Maximum = TrackMax, TickStyle = TickStyle.None };
            trkPos.MouseDown += delegate { seeking = true; };
            trkPos.MouseUp += delegate { seeking = false; ApplySeek(); };
            trkPos.Scroll += delegate { if (seeking) UpdateTimeLabel(TrackToSeconds()); else ApplySeek(); };
            pnlFile.Controls.Add(trkPos);
            Controls.Add(pnlFile);

            // --- 共通（出力・ミュート・メーター） ---
            pnlLower = new Panel { Location = new Point(0, 116), Size = new Size(462, 504) };
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                AddLabel(pnlLower, "出力" + (i + 1), 16, 4 + i * 34);
                cboOut[i] = AddCombo(pnlLower, 70, i * 34, 376);
            }
            lblCable = AddLabel(pnlLower, "", 16, 68);
            lblCable.ForeColor = Color.Firebrick;

            btnRefresh = new Button { Text = "デバイス再読込", Location = new Point(16, 92), Size = new Size(140, 34) };
            btnRefresh.Click += delegate { LoadDevices(cboIn.Text, cboOut[0].Text, cboOut[1].Text, cboMonitor.Text); };
            pnlLower.Controls.Add(btnRefresh);

            btnStart = new Button { Text = "開始", Location = new Point(306, 92), Size = new Size(140, 34) };
            btnStart.Click += delegate { if (router == null) StartRouting(); else StopRouting(null); };
            pnlLower.Controls.Add(btnStart);

            AddLabel(pnlLower, "出力のON／ミュート（クリックで切り替え）", 16, 142);
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                int n = i;
                Button b = new Button { Location = new Point(16 + i * 222, 166), Size = new Size(208, 76), FlatStyle = FlatStyle.Flat };
                b.Font = new Font("Meiryo UI", 11F, FontStyle.Bold);
                b.Click += delegate { SetMuted(n, !muted[n]); };
                pnlLower.Controls.Add(b);
                btnMute[i] = b;
            }
            // 出力ごとの音量（0〜200%）。％表示をダブルクリックで 100% に戻す
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                int n = i;
                AddLabel(pnlLower, "音量", 16 + i * 222, 254);
                TrackBar t = new TrackBar { Location = new Point(52 + i * 222, 248), AutoSize = false, Size = new Size(124, 30),
                    Minimum = 0, Maximum = MaxVolumePercent, Value = 100, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 10 };
                t.ValueChanged += delegate { SetVolume(n, trkVolume[n].Value); };
                pnlLower.Controls.Add(t);
                trkVolume[i] = t;
                Label l = AddLabel(pnlLower, "100%", 176 + i * 222, 254);
                l.DoubleClick += delegate { trkVolume[n].Value = 100; };
                lblVolume[i] = l;
            }

            btnBothOn = new Button { Text = "両方ON（Ctrl+Alt+3）", Location = new Point(16, 288), Size = new Size(430, 32) };
            btnBothOn.Click += delegate { SetMuted(0, false); SetMuted(1, false); };
            pnlLower.Controls.Add(btnBothOn);

            lblMeterIn = AddLabel(pnlLower, "入力", 16, 336);
            meterIn = AddMeter(336);
            AddLabel(pnlLower, "出力1", 16, 364);
            meterOut[0] = AddMeter(364);
            AddLabel(pnlLower, "出力2", 16, 392);
            meterOut[1] = AddMeter(392);

            chkTop = new CheckBox { Text = "常に手前に表示", Location = new Point(16, 424), AutoSize = true };
            chkTop.CheckedChanged += delegate { TopMost = chkTop.Checked; };
            pnlLower.Controls.Add(chkTop);

            lblStatus = AddLabel(pnlLower, "停止中", 16, 452);
            lblStatus.AutoSize = false;
            lblStatus.Size = new Size(430, 44);
            Controls.Add(pnlLower);

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(462, 620);
            ResumeLayout(false);

            timer = new Timer { Interval = 50 };
            timer.Tick += OnTick;
            timer.Start();

            LoadSettings();
            UpdateMuteButtons();
            UpdateSourceMode();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UpdateSourceMode();   // DPI に合わせた拡大後の大きさで配置し直す
        }

        static Label AddLabel(Control parent, string text, int x, int y)
        {
            Label l = new Label { Text = text, Location = new Point(x, y), AutoSize = true };
            parent.Controls.Add(l);
            return l;
        }

        static ComboBox AddCombo(Control parent, int x, int y, int width)
        {
            ComboBox c = new ComboBox { Location = new Point(x, y), Size = new Size(width, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            parent.Controls.Add(c);
            return c;
        }

        LevelMeter AddMeter(int y)
        {
            LevelMeter m = new LevelMeter { Location = new Point(70, y), Size = new Size(376, 20) };
            pnlLower.Controls.Add(m);
            return m;
        }

        bool FileMode { get { return rbFile.Checked; } }

        void UpdateSourceMode()
        {
            pnlMic.Visible = !FileMode;
            pnlFile.Visible = FileMode;
            btnStart.Visible = !FileMode;
            lblMeterIn.Text = FileMode ? "音源" : "入力";
            pnlLower.Top = FileMode ? pnlFile.Bottom : pnlMic.Bottom;
            ClientSize = new Size(ClientSize.Width, pnlLower.Bottom);
            UpdateStatus();
        }

        // ---------- ミュート ----------

        void SetMuted(int output, bool value)
        {
            muted[output] = value;
            if (router != null) router.SetMuted(output, value);
            if (player != null) player.SetMuted(output, value);
            UpdateMuteButtons();
        }

        void SetVolume(int output, int percent)
        {
            float v = percent / 100f;
            if (router != null) router.SetVolume(output, v);
            if (player != null) player.SetVolume(output, v);
            lblVolume[output].Text = percent + "%";
        }

        void UpdateMuteButtons()
        {
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                Button b = btnMute[i];
                Color c = OutputColors[i];
                b.Text = "出力" + (i + 1) + "（Ctrl+Alt+" + (i + 1) + "）\n" + (muted[i] ? "ミュート中" : "ON");
                b.BackColor = muted[i] ? SystemColors.Control : c;
                // 背景が淡い色なので、文字は濃い色、枠は背景より濃い同系色にして見分けやすくする
                b.ForeColor = muted[i] ? MutedColor : ActiveTextColor;
                b.FlatAppearance.BorderColor = muted[i] ? MutedColor : ControlPaint.Dark(c, 0.3f);
                b.FlatAppearance.BorderSize = muted[i] ? 1 : 3;
            }
            UpdateStatus();
        }

        // ---------- デバイス ----------

        void LoadDevices(string preferIn, string preferOut1, string preferOut2, string preferMonitor)
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

            // モニターの先頭は「なし」。項目の番号 - 1 がデバイス番号になる
            cboMonitor.Items.Clear();
            cboMonitor.Items.Add("（なし）");
            cboMonitor.Items.AddRange(outs);
            int mon = FindExact(outs, preferMonitor);
            cboMonitor.SelectedIndex = mon < 0 ? 0 : mon + 1;

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

        bool ValidateOutputs()
        {
            if (cboOut[0].SelectedIndex < 0 || cboOut[1].SelectedIndex < 0)
            {
                MessageBox.Show(this, "出力1・出力2のデバイスを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (cboOut[0].SelectedIndex == cboOut[1].SelectedIndex)
            {
                MessageBox.Show(this, "出力1と出力2には別々のデバイスを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (!IsCable(cboOut[0].Text) || !IsCable(cboOut[1].Text))
            {
                DialogResult ans = MessageBox.Show(this,
                    "出力先が仮想ケーブルではないようです。\nスピーカーに出すとハウリングすることがあります。続けますか？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return false;
            }
            return true;
        }

        void SetRunningUi(bool running)
        {
            rbMic.Enabled = rbFile.Enabled = !running;
            cboIn.Enabled = btnRefresh.Enabled = btnBrowse.Enabled = cboMonitor.Enabled = !running;
            foreach (ComboBox c in cboOut) c.Enabled = !running;
            btnStart.Text = router != null ? "停止" : "開始";
            btnPlay.Text = player == null ? "▶ 再生" : player.IsPaused ? "▶ 再開" : "❚❚ 一時停止";
            UpdateStatus();
        }

        // ---------- マイクモード ----------

        void StartRouting()
        {
            if (cboIn.SelectedIndex < 0)
            {
                MessageBox.Show(this, "入力デバイスを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ValidateOutputs()) return;

            AudioRouter r = new AudioRouter(cboIn.SelectedIndex, new int[] { cboOut[0].SelectedIndex, cboOut[1].SelectedIndex });
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                r.SetMuted(i, muted[i]);
                r.SetVolume(i, trkVolume[i].Value / 100f);
            }
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
            SetRunningUi(true);
        }

        void StopRouting(string error)
        {
            if (router != null)
            {
                router.Dispose();
                router = null;
            }
            ResetMeters();
            SetRunningUi(false);
            if (error != null)
                MessageBox.Show(this, "停止しました。\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ---------- 音声ファイルモード ----------

        void BrowseFile()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "WAV ファイル (*.wav)|*.wav|すべてのファイル (*.*)|*.*";
                if (File.Exists(txtFile.Text)) dlg.InitialDirectory = Path.GetDirectoryName(txtFile.Text);
                if (dlg.ShowDialog(this) == DialogResult.OK) SelectFile(dlg.FileName, true);
            }
        }

        bool SelectFile(string path, bool showErrors)
        {
            try
            {
                using (WavReader r = new WavReader(path))
                {
                    fileDuration = r.Duration;
                    lblFileInfo.Text = r.Description + (r.Channels == 1 ? "（モノラル：左右に同じ音）" : "");
                }
            }
            catch (Exception ex)
            {
                if (showErrors)
                    MessageBox.Show(this, "ファイルを開けませんでした。\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            txtFile.Text = path;
            txtFile.SelectionStart = path.Length;
            filePosition = 0;
            finishedNote = null;
            UpdatePositionUi(0);
            return true;
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            bool ok = files != null && files.Length > 0 && router == null && player == null;
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            if (SelectFile(files[0], true)) rbFile.Checked = true;
        }

        void OnPlayClick()
        {
            if (player == null) StartPlayback();
            else if (player.IsPaused) player.Resume();
            else player.Pause();
            SetRunningUi(player != null);
        }

        void StartPlayback()
        {
            if (!File.Exists(txtFile.Text))
            {
                MessageBox.Show(this, "再生する WAV ファイルを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ValidateOutputs()) return;

            int monitor = cboMonitor.SelectedIndex - 1;
            if (monitor >= 0 && (monitor == cboOut[0].SelectedIndex || monitor == cboOut[1].SelectedIndex))
            {
                MessageBox.Show(this, "モニターには出力1・出力2と別のデバイスを選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            FilePlayer p = new FilePlayer(txtFile.Text, new int[] { cboOut[0].SelectedIndex, cboOut[1].SelectedIndex }, monitor);
            for (int i = 0; i < AudioRouter.OutputCount; i++)
            {
                p.SetSource(i, (SourceChannel)cboSource[i].SelectedIndex);
                p.SetMuted(i, muted[i]);
                p.SetVolume(i, trkVolume[i].Value / 100f);
            }
            try
            {
                p.Start(filePosition);
            }
            catch (Exception ex)
            {
                p.Dispose();
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            player = p;
            finishedNote = null;
        }

        void StopPlayback(string error, bool rewind)
        {
            if (player != null)
            {
                if (!rewind) filePosition = player.PositionSeconds;
                player.Dispose();
                player = null;
            }
            if (rewind) filePosition = 0;
            UpdatePositionUi(filePosition);
            ResetMeters();
            SetRunningUi(false);
            if (error != null)
                MessageBox.Show(this, "停止しました。\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        double TrackToSeconds()
        {
            return fileDuration * trkPos.Value / TrackMax;
        }

        void ApplySeek()
        {
            double sec = TrackToSeconds();
            filePosition = sec;
            if (player != null) player.Seek(sec);
            UpdateTimeLabel(sec);
        }

        void UpdatePositionUi(double seconds)
        {
            if (!seeking)
                trkPos.Value = fileDuration > 0 ? Math.Max(0, Math.Min(TrackMax, (int)(seconds / fileDuration * TrackMax))) : 0;
            UpdateTimeLabel(seconds);
        }

        void UpdateTimeLabel(double seconds)
        {
            lblTime.Text = FormatTime(seconds) + " / " + FormatTime(fileDuration);
        }

        static string FormatTime(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        // ---------- 共通 ----------

        void ResetMeters()
        {
            meterIn.Level = 0;
            foreach (LevelMeter m in meterOut) m.Level = 0;
        }

        void UpdateStatus()
        {
            if (lblStatus == null || rbFile == null) return;
            string state;
            if (FileMode)
                state = player == null ? (finishedNote ?? "停止中") : player.IsPaused ? "一時停止中" : "再生中";
            else
                state = router != null ? "動作中" : "停止中";
            string s = state + "　／　出力1: " + (muted[0] ? "ミュート" : "ON") + "　出力2: " + (muted[1] ? "ミュート" : "ON");
            if (!hotkeysOk) s += "\n※ ショートカットは他のアプリと重複して使えません（ボタンで操作してください）";
            lblStatus.Text = s;
        }

        void OnTick(object sender, EventArgs e)
        {
            if (router != null)
            {
                if (router.LastError != null) { StopRouting(router.LastError); return; }
                meterIn.Level = router.InputPeak / 32768.0;
                for (int i = 0; i < AudioRouter.OutputCount; i++)
                    meterOut[i].Level = router.GetOutputPeak(i) / 32768.0;
            }
            if (player != null)
            {
                if (player.LastError != null) { StopPlayback(player.LastError, false); return; }
                if (player.IsCompleted)
                {
                    StopPlayback(null, true);
                    finishedNote = "再生が終わりました";
                    UpdateStatus();
                    return;
                }
                meterIn.Level = player.SourcePeak / 32768.0;
                for (int i = 0; i < AudioRouter.OutputCount; i++)
                    meterOut[i].Level = player.GetOutputPeak(i) / 32768.0;
                UpdatePositionUi(player.PositionSeconds);
            }
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
            StopPlayback(null, true);
            for (int id = 1; id <= 3; id++) UnregisterHotKey(Handle, id);
            base.OnFormClosing(e);
        }

        void LoadSettings()
        {
            string inName = null, out1 = null, out2 = null, monitorName = null, file = null;
            bool fileMode = false;
            try
            {
                if (File.Exists(settingsPath))
                {
                    foreach (string line in File.ReadAllLines(settingsPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                        int v;
                        if (key == "input") inName = val;
                        else if (key == "output1") out1 = val;
                        else if (key == "output2") out2 = val;
                        else if (key == "monitor") monitorName = val;
                        else if (key == "mute1") muted[0] = val == "1";
                        else if (key == "mute2") muted[1] = val == "1";
                        else if ((key == "vol1" || key == "vol2") && int.TryParse(val, out v) && v >= 0 && v <= MaxVolumePercent)
                            trkVolume[key == "vol1" ? 0 : 1].Value = v;
                        else if (key == "topmost") chkTop.Checked = val == "1";
                        else if (key == "source") fileMode = val == "file";
                        else if (key == "file") file = val;
                        else if (key == "src1" && int.TryParse(val, out v) && v >= 0 && v <= 1) cboSource[0].SelectedIndex = v;
                        else if (key == "src2" && int.TryParse(val, out v) && v >= 0 && v <= 1) cboSource[1].SelectedIndex = v;
                    }
                }
            }
            catch (Exception) { }
            LoadDevices(inName, out1, out2, monitorName);
            if (!string.IsNullOrEmpty(file) && File.Exists(file)) SelectFile(file, false);
            rbFile.Checked = fileMode;
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
                    "monitor=" + (cboMonitor.SelectedIndex > 0 ? cboMonitor.Text : ""),
                    "mute1=" + (muted[0] ? "1" : "0"),
                    "mute2=" + (muted[1] ? "1" : "0"),
                    "vol1=" + trkVolume[0].Value,
                    "vol2=" + trkVolume[1].Value,
                    "topmost=" + (chkTop.Checked ? "1" : "0"),
                    "source=" + (FileMode ? "file" : "mic"),
                    "file=" + txtFile.Text,
                    "src1=" + cboSource[0].SelectedIndex,
                    "src2=" + cboSource[1].SelectedIndex,
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
