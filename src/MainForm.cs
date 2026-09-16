using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WebcamControl
{
    public class MainForm : Form
    {
        // --- etat ---
        ConfigDto cfg;
        CameraSession session;
        List<ControlDef> defs = new List<ControlDef>();
        readonly Dictionary<string, ControlRow> rows = new Dictionary<string, ControlRow>();
        Dictionary<string, ControlState> desired = new Dictionary<string, ControlState>();
        bool suspendEvents;
        bool reallyExit;
        bool startHidden;
        bool firstShowHandled;

        // --- widgets ---
        ComboBox profileBox;
        Button btnSave, btnNew, btnDelete, btnDefaults, btnLockAuto, btnReread;
        CheckBox chkWatchdog, chkThirds, chkAutoStart;
        NumericUpDown numWatchdog;
        Label status;
        PictureBox preview;
        Button btnPreview;
        FlowLayoutPanel groupsHost;
        Panel framingPad;

        // --- auto-exposition logicielle ---
        readonly SoftAe softAe = new SoftAe();
        readonly Timer aeTimer = new Timer();
        CheckBox chkAe;
        TrackBar aeTarget;
        Label aeTargetValue, aeStatus;
        NumericUpDown aeInterval;
        Button btnAeMeasure;
        double previewLuma = -1;
        DateTime previewLumaAt = DateTime.MinValue;

        readonly PreviewStream previewStream = new PreviewStream();
        readonly Timer watchdogTimer = new Timer();
        readonly Timer reconnectTimer = new Timer();
        readonly Timer deviceChangeTimer = new Timer();
        NotifyIcon tray;
        ContextMenuStrip trayMenu;

        const int WM_DEVICECHANGE = 0x0219;
        const int DBT_DEVNODES_CHANGED = 0x0007;

        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            cfg = Store.Load();
            BuildUi();
            BuildTray();

            watchdogTimer.Interval = Math.Max(1, cfg.WatchdogSeconds) * 1000;
            watchdogTimer.Tick += delegate { RunWatchdog(); };
            watchdogTimer.Enabled = cfg.WatchdogEnabled;

            reconnectTimer.Interval = 4000;
            reconnectTimer.Tick += delegate { if (session == null) Connect(true); };
            reconnectTimer.Enabled = true;

            WireSoftAe();
            aeTimer.Interval = 1000;
            aeTimer.Tick += delegate { try { softAe.Tick(); } catch (Exception ex) { Log.Write("AE: " + ex.Message); } };
            aeTimer.Enabled = true;

            deviceChangeTimer.Interval = 1500;   // anti-rebond sur les notifications de branchement
            deviceChangeTimer.Tick += delegate
            {
                deviceChangeTimer.Stop();
                // WM_DEVICECHANGE se declenche pour quantite de raisons, y compris
                // l'ouverture de la camera par notre propre mesure. Reconnecter dans
                // ce cas rechargerait le profil et annulerait l'auto-exposition en
                // cours. On ne reconnecte donc que si la session est reellement morte.
                if (SessionStillAlive()) return;
                Log.Write("Camera perdue, reconnexion");
                Connect(true);
            };

            Connect(false);
        }

        // Demarrage en zone de notification : on empeche le tout premier affichage
        // tout en forcant la creation du handle, sans quoi les timers et WndProc dorment.
        protected override void SetVisibleCore(bool value)
        {
            if (!firstShowHandled && startHidden)
            {
                firstShowHandled = true;
                if (!IsHandleCreated) CreateHandle();
                ShowInTaskbar = false;
                base.SetVisibleCore(false);
                return;
            }
            firstShowHandled = true;
            base.SetVisibleCore(value);
        }

        // ------------------------------------------------------------------ interface

        void BuildUi()
        {
            Text = "Webcam Control";
            ClientSize = new Size(1010, 665);
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            Icon = Glyph.MakeIcon();
            KeyPreview = true;
            KeyDown += OnKeyDown;

            // ---- bandeau profils ----
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 44;
            top.Padding = new Padding(10, 8, 10, 4);

            Label lp = new Label();
            lp.Text = "Profil";
            lp.AutoSize = true;
            lp.Location = new Point(12, 13);

            profileBox = new ComboBox();
            profileBox.DropDownStyle = ComboBoxStyle.DropDownList;
            profileBox.Location = new Point(58, 9);
            profileBox.Width = 200;
            profileBox.SelectedIndexChanged += delegate
            {
                if (suspendEvents) return;
                string name = profileBox.SelectedItem as string;
                if (name != null) LoadProfile(name, true);
            };

            btnSave = MakeButton("Enregistrer", 268, 8, 95);
            btnSave.Click += delegate { SaveCurrentProfile(); };
            btnNew = MakeButton("Nouveau", 368, 8, 85);
            btnNew.Click += delegate { NewProfile(); };
            btnDelete = MakeButton("Supprimer", 458, 8, 90);
            btnDelete.Click += delegate { DeleteProfile(); };

            btnLockAuto = MakeButton("Verrouiller les auto", 568, 8, 140);
            btnLockAuto.Click += delegate { LockAutoControls(); };
            toolTip.SetToolTip(btnLockAuto,
                "Fige l'exposition et la balance des blancs sur leur valeur actuelle.\n" +
                "C'est le remede a l'image qui pompe et aux couleurs qui virent.");

            btnDefaults = MakeButton("Defauts", 713, 8, 80);
            btnDefaults.Click += delegate { ApplyFactoryDefaults(); };
            btnReread = MakeButton("Relire", 798, 8, 75);
            btnReread.Click += delegate { ReadFromCamera(); };

            top.Controls.AddRange(new Control[] { lp, profileBox, btnSave, btnNew, btnDelete, btnLockAuto, btnDefaults, btnReread });

            // ---- barre de statut ----
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 38;

            status = new Label();
            status.AutoSize = false;
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Padding = new Padding(12, 0, 0, 0);

            chkWatchdog = new CheckBox();
            chkWatchdog.Text = "Reappliquer toutes les";
            chkWatchdog.AutoSize = true;
            chkWatchdog.Checked = cfg.WatchdogEnabled;
            chkWatchdog.Location = new Point(560, 10);
            chkWatchdog.CheckedChanged += delegate
            {
                cfg.WatchdogEnabled = chkWatchdog.Checked;
                watchdogTimer.Enabled = cfg.WatchdogEnabled;
                Store.Save(cfg);
            };

            numWatchdog = new NumericUpDown();
            numWatchdog.Minimum = 1; numWatchdog.Maximum = 600;
            numWatchdog.Value = Math.Max(1, Math.Min(600, cfg.WatchdogSeconds));
            numWatchdog.Width = 55;
            numWatchdog.Location = new Point(705, 8);
            numWatchdog.ValueChanged += delegate
            {
                cfg.WatchdogSeconds = (int)numWatchdog.Value;
                watchdogTimer.Interval = cfg.WatchdogSeconds * 1000;
                Store.Save(cfg);
            };

            Label sec = new Label();
            sec.Text = "s";
            sec.AutoSize = true;
            sec.Location = new Point(764, 11);

            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "Lancer au demarrage";
            chkAutoStart.AutoSize = true;
            chkAutoStart.Checked = AutoStart.Enabled;
            chkAutoStart.Location = new Point(790, 10);
            chkAutoStart.CheckedChanged += delegate { AutoStart.Set(chkAutoStart.Checked); };

            bottom.Controls.AddRange(new Control[] { chkWatchdog, numWatchdog, sec, chkAutoStart, status });
            bottom.Controls.SetChildIndex(status, bottom.Controls.Count - 1);

            // ---- colonne gauche : apercu ----
            preview = new PictureBox();
            preview.Dock = DockStyle.Fill;
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            preview.BackColor = Color.FromArgb(24, 24, 28);
            preview.Paint += OnPreviewPaint;

            Panel previewBar = new Panel();
            previewBar.Dock = DockStyle.Fill;

            btnPreview = MakeButton("Demarrer l'apercu", 0, 5, 140);
            btnPreview.Click += delegate { TogglePreview(); };

            chkThirds = new CheckBox();
            chkThirds.Text = "Grille des tiers";
            chkThirds.AutoSize = true;
            chkThirds.Checked = cfg.ShowThirds;
            chkThirds.Location = new Point(150, 9);
            chkThirds.CheckedChanged += delegate
            {
                cfg.ShowThirds = chkThirds.Checked;
                Store.Save(cfg);
                preview.Invalidate();
            };

            previewBar.Controls.AddRange(new Control[] { btnPreview, chkThirds });

            framingPad = BuildFramingPad();

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.ColumnCount = 1;
            left.RowCount = 3;
            left.Padding = new Padding(12, 8, 6, 8);
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            left.Controls.Add(preview, 0, 0);
            left.Controls.Add(previewBar, 0, 1);
            left.Controls.Add(framingPad, 0, 2);

            // ---- colonne droite : reglages ----
            groupsHost = new FlowLayoutPanel();
            groupsHost.Dock = DockStyle.Fill;
            groupsHost.FlowDirection = FlowDirection.TopDown;
            groupsHost.WrapContents = false;
            groupsHost.AutoScroll = true;
            groupsHost.Padding = new Padding(6, 8, 12, 8);
            groupsHost.Resize += delegate { FitGroupWidths(); };

            // ---- assemblage ----
            // Une grille explicite plutot qu'un empilement de Dock : l'ordre d'ajout
            // n'influe plus sur le partage de la surface.
            TableLayoutPanel split = new TableLayoutPanel();
            split.Dock = DockStyle.Fill;
            split.ColumnCount = 2;
            split.RowCount = 1;
            split.Margin = Padding.Empty;
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 478));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.Controls.Add(left, 0, 0);
            split.Controls.Add(groupsHost, 1, 0);

            top.Dock = DockStyle.Fill;
            bottom.Dock = DockStyle.Fill;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Margin = Padding.Empty;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(split, 0, 1);
            root.Controls.Add(bottom, 0, 2);

            Controls.Add(root);

            previewStream.FrameReady += OnFrameReady;
            previewStream.Stopped += OnPreviewStopped;
        }

        readonly ToolTip toolTip = new ToolTip();

        // ------------------------------------------------------------------ auto-exposition

        void WireSoftAe()
        {
            softAe.Ui = this;
            softAe.GetSession = delegate { return EnsureSession() ? session : null; };
            softAe.GetExposureDef = delegate { return FindDef("Exposure"); };
            softAe.GetGainDef = delegate { return FindDef("Gain"); };
            softAe.PreviewRunning = delegate { return previewStream.Running; };
            softAe.PreviewLuma = delegate
            {
                // une mesure de plus de 2 s n'est plus representative
                if (previewLuma < 0 || (DateTime.Now - previewLumaAt).TotalSeconds > 2) return -1;
                return previewLuma;
            };
            softAe.GetFfmpegPath = delegate { return cfg.FfmpegPath; };
            softAe.GetDeviceName = delegate { return session != null ? session.DeviceName : cfg.Device; };
            softAe.Applied = OnAeApplied;
            softAe.Status = delegate(string text)
            {
                if (aeStatus != null) aeStatus.Text = text;
            };
            softAe.Trace = delegate(string text) { Log.Write(text); };
        }

        ControlDef FindDef(string key)
        {
            foreach (ControlDef d in defs) if (d.Key == key) return d;
            return null;
        }

        // L'auto-exposition vient de corriger : on reporte son action dans l'interface
        // et dans l'etat de reference, sinon le watchdog la defairait aussitot.
        void OnAeApplied(int exposure, int gain)
        {
            desired["Gain"] = new ControlState(gain, false);
            ControlDef e = FindDef("Exposure");
            if (e != null) desired["Exposure"] = new ControlState(exposure, false);

            suspendEvents = true;
            try
            {
                ControlRow row;
                if (rows.TryGetValue("Gain", out row)) row.SetState(desired["Gain"]);
                if (e != null && rows.TryGetValue("Exposure", out row)) row.SetState(desired["Exposure"]);
            }
            finally { suspendEvents = false; }
        }

        GroupBox BuildSoftAeBox()
        {
            GroupBox box = new GroupBox();
            box.Text = "Auto-exposition adoucie";
            box.Height = 132;
            box.Margin = new Padding(3, 3, 3, 10);

            chkAe = new CheckBox();
            chkAe.Text = "Activer";
            chkAe.AutoSize = true;
            chkAe.Location = new Point(14, 22);
            chkAe.CheckedChanged += delegate
            {
                if (suspendEvents) return;
                softAe.Settings.Enabled = chkAe.Checked;
                softAe.Reset();
                if (chkAe.Checked) { softAe.Prime(); softAe.MeasureNow(); }
                UpdateAeDrivenRows();
                SetStatus(chkAe.Checked
                    ? "Auto-exposition adoucie activee : exposition et gain sont pilotes automatiquement"
                    : "Auto-exposition adoucie desactivee", false);
            };
            toolTip.SetToolTip(chkAe,
                "Maintient l'exposition et le gain en manuel, donc sans aucun saut du firmware,\n" +
                "et corrige la luminosite par petits pas au gain.\n" +
                "La mesure n'a lieu que lorsque la camera est libre ou que l'apercu est ouvert ;\n" +
                "pendant une visioconference, les reglages sont figes.");

            Label lt = new Label();
            lt.Text = "Luminosite visee";
            lt.AutoSize = true;
            lt.Location = new Point(14, 52);

            aeTarget = new TrackBar();
            aeTarget.Minimum = 40; aeTarget.Maximum = 200;
            aeTarget.TickStyle = TickStyle.None;
            aeTarget.SetBounds(130, 46, 180, 30);
            aeTarget.ValueChanged += delegate
            {
                if (aeTargetValue != null) aeTargetValue.Text = aeTarget.Value.ToString();
                if (suspendEvents) return;
                softAe.Settings.TargetLuma = aeTarget.Value;
            };

            aeTargetValue = new Label();
            aeTargetValue.AutoSize = false;
            aeTargetValue.TextAlign = ContentAlignment.MiddleRight;
            aeTargetValue.SetBounds(314, 52, 40, 20);

            Label li = new Label();
            li.Text = "Remesurer toutes les";
            li.AutoSize = true;
            li.Location = new Point(14, 86);

            aeInterval = new NumericUpDown();
            aeInterval.Minimum = 15; aeInterval.Maximum = 3600;
            aeInterval.Width = 62;
            aeInterval.Location = new Point(144, 84);
            aeInterval.ValueChanged += delegate
            {
                if (suspendEvents) return;
                softAe.Settings.IdleIntervalSeconds = (int)aeInterval.Value;
            };

            Label ls = new Label();
            ls.Text = "s";
            ls.AutoSize = true;
            ls.Location = new Point(210, 87);

            btnAeMeasure = new Button();
            btnAeMeasure.Text = "Mesurer maintenant";
            btnAeMeasure.SetBounds(232, 82, 130, 26);
            btnAeMeasure.Click += delegate { softAe.MeasureNow(); };

            aeStatus = new Label();
            aeStatus.AutoSize = false;
            aeStatus.ForeColor = SystemColors.GrayText;
            aeStatus.SetBounds(14, 110, 380, 18);
            aeStatus.Text = "inactive";

            box.Controls.AddRange(new Control[] { chkAe, lt, aeTarget, aeTargetValue, li, aeInterval, ls, btnAeMeasure, aeStatus });
            return box;
        }

        void PushAeSettingsToUi()
        {
            if (chkAe == null) return;
            suspendEvents = true;
            try
            {
                SoftAeDto s = softAe.Settings;
                chkAe.Checked = s.Enabled;
                aeTarget.Value = Math.Max(aeTarget.Minimum, Math.Min(aeTarget.Maximum, s.TargetLuma));
                aeTargetValue.Text = aeTarget.Value.ToString();
                aeInterval.Value = Math.Max(aeInterval.Minimum, Math.Min(aeInterval.Maximum, s.IdleIntervalSeconds));
            }
            finally { suspendEvents = false; }
            UpdateAeDrivenRows();
        }

        // Quand l'auto-exposition tient les commandes, ses deux curseurs deviennent
        // des indicateurs : les laisser actifs inviterait a se battre avec elle.
        void UpdateAeDrivenRows()
        {
            bool on = softAe.Settings.Enabled;
            ControlRow row;
            if (rows.TryGetValue("Exposure", out row)) row.SetDriven(on);
            if (rows.TryGetValue("Gain", out row)) row.SetDriven(on);
            if (btnLockAuto != null) btnLockAuto.Enabled = !on && session != null;
        }

        Button MakeButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Width = w;
            b.Height = 26;
            return b;
        }

        Panel BuildFramingPad()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;

            Label t = new Label();
            t.Text = "Cadrage rapide";
            t.AutoSize = true;
            t.Location = new Point(0, 4);
            t.Font = new Font(Font, FontStyle.Bold);

            const int col = 34;   // pas horizontal du pave directionnel
            const int row = 30;   // pas vertical
            int x0 = 40, y0 = 28;

            Button up = PadButton("▲", x0 + col, y0); up.Click += delegate { Nudge("Tilt", +1); };
            Button lf = PadButton("◀", x0, y0 + row); lf.Click += delegate { Nudge("Pan", -1); };
            Button ctr = PadButton("●", x0 + col, y0 + row); ctr.Click += delegate { CenterFraming(); };
            Button rt = PadButton("▶", x0 + 2 * col, y0 + row); rt.Click += delegate { Nudge("Pan", +1); };
            Button down = PadButton("▼", x0 + col, y0 + 2 * row); down.Click += delegate { Nudge("Tilt", -1); };
            toolTip.SetToolTip(ctr, "Recentrer le panoramique et l'inclinaison");

            Label zl = new Label();
            zl.Text = "Zoom";
            zl.AutoSize = true;
            zl.Location = new Point(200, y0 + row + 5);

            Button zOut = PadButton("−", 245, y0 + row); zOut.Click += delegate { Nudge("Zoom", -1); };
            Button zIn = PadButton("+", 283, y0 + row); zIn.Click += delegate { Nudge("Zoom", +1); };

            Label hint = new Label();
            hint.Text = "Fleches du clavier : panoramique et inclinaison";
            hint.AutoSize = true;
            hint.ForeColor = SystemColors.GrayText;
            hint.Location = new Point(0, y0 + 3 * row + 6);

            p.Controls.AddRange(new Control[] { t, up, down, lf, rt, ctr, zOut, zIn, zl, hint });
            return p;
        }

        Button PadButton(string text, int x, int y)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, 32, 26);
            b.Font = new Font("Segoe UI", 8f);
            return b;
        }

        // ------------------------------------------------------------------ connexion camera

        void Connect(bool silent)
        {
            if (session != null) { session.Dispose(); session = null; }

            session = CameraSession.Open(cfg.Device);
            if (session == null)
            {
                SetEnabled(false);
                SetStatus("Camera introuvable (" + cfg.Device + "). Nouvelle tentative dans quelques secondes.", true);
                return;
            }

            defs = session.Discover();
            if (defs.Count == 0)
            {
                SetEnabled(false);
                SetStatus("La camera " + session.DeviceName + " n'expose aucun reglage UVC.", true);
                return;
            }

            BuildControlRows();
            SetEnabled(true);

            // premier lancement : on cree deux profils de depart
            if (cfg.Profiles.Count == 0)
            {
                Dictionary<string, ControlState> current = ReadAllFromCamera();
                cfg.Upsert(ProfileDto.From("Mes reglages", current));
                cfg.Upsert(ProfileDto.From("Defauts constructeur", FactoryDefaults()));
                cfg.ActiveProfile = "Mes reglages";
                Store.Save(cfg);
            }

            RefreshProfileList();

            string active = cfg.ActiveProfile;
            if (cfg.Find(active) == null && cfg.Profiles.Count > 0) active = cfg.Profiles[0].Name;
            LoadProfile(active, true);

            if (!silent) Log.Write("Connecte a " + session.DeviceName + ", " + defs.Count + " reglages");
            SetStatus(session.DeviceName + " - " + defs.Count + " reglages disponibles", false);
        }

        // La session repond-elle encore ? Une lecture suffit et coute environ 1 ms.
        bool SessionStillAlive()
        {
            if (session == null || !session.IsOpen || defs.Count == 0) return false;
            ControlState st;
            return session.TryRead(defs[0], out st);
        }

        bool EnsureSession()
        {
            if (session != null && session.IsOpen) return true;
            if (session != null) { session.Dispose(); session = null; }
            session = CameraSession.Open(cfg.Device);
            return session != null;
        }

        Dictionary<string, ControlState> ReadAllFromCamera()
        {
            Dictionary<string, ControlState> d = new Dictionary<string, ControlState>();
            if (session == null) return d;
            foreach (ControlDef def in defs)
            {
                ControlState st;
                if (session.TryRead(def, out st)) d[def.Key] = st;
            }
            return d;
        }

        Dictionary<string, ControlState> FactoryDefaults()
        {
            Dictionary<string, ControlState> d = new Dictionary<string, ControlState>();
            foreach (ControlDef def in defs) d[def.Key] = new ControlState(def.Default, def.CanAuto);
            return d;
        }

        // ------------------------------------------------------------------ lignes de reglage

        void BuildControlRows()
        {
            groupsHost.SuspendLayout();
            groupsHost.Controls.Clear();
            rows.Clear();

            if (FindDef("Gain") != null) groupsHost.Controls.Add(BuildSoftAeBox());

            string[] order = { Catalog.GroupExposure, Catalog.GroupImage, Catalog.GroupFraming };
            foreach (string group in order)
            {
                List<ControlDef> inGroup = defs.FindAll(delegate(ControlDef d) { return d.Group == group; });
                if (inGroup.Count == 0) continue;

                GroupBox box = new GroupBox();
                box.Text = group;
                box.Height = 30 + inGroup.Count * 34;
                box.Margin = new Padding(3, 3, 3, 10);

                TableLayoutPanel grid = new TableLayoutPanel();
                grid.Dock = DockStyle.Fill;
                grid.ColumnCount = 4;
                grid.RowCount = inGroup.Count;
                grid.Padding = new Padding(6, 4, 6, 4);
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));

                for (int i = 0; i < inGroup.Count; i++)
                {
                    grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                    ControlRow row = new ControlRow(inGroup[i], this);
                    rows[inGroup[i].Key] = row;
                    grid.Controls.Add(row.NameLabel, 0, i);
                    grid.Controls.Add(row.AutoBox, 1, i);
                    grid.Controls.Add(row.Slider, 2, i);
                    grid.Controls.Add(row.ValueLabel, 3, i);
                }

                box.Controls.Add(grid);
                groupsHost.Controls.Add(box);
            }
            groupsHost.ResumeLayout();
            FitGroupWidths();
        }

        // Le FlowLayoutPanel ne redimensionne pas ses enfants : on le fait a la main,
        // en gardant la place de la barre de defilement verticale.
        void FitGroupWidths()
        {
            int w = groupsHost.ClientSize.Width - groupsHost.Padding.Horizontal - 8;
            if (w < 260) w = 260;
            foreach (Control c in groupsHost.Controls) c.Width = w;
        }

        // Appelee par ControlRow quand l'utilisateur bouge un curseur ou coche Auto.
        internal void OnRowChanged(ControlDef def, ControlState state)
        {
            if (suspendEvents) return;
            desired[def.Key] = state;
            if (EnsureSession()) session.Write(def, state);
        }

        void PushToUi(Dictionary<string, ControlState> states)
        {
            suspendEvents = true;
            try
            {
                foreach (KeyValuePair<string, ControlRow> kv in rows)
                {
                    ControlState st;
                    if (states.TryGetValue(kv.Key, out st)) kv.Value.SetState(st);
                }
            }
            finally { suspendEvents = false; }
        }

        void ApplyDesiredToCamera()
        {
            if (!EnsureSession()) return;
            foreach (ControlDef def in defs)
            {
                ControlState st;
                if (desired.TryGetValue(def.Key, out st)) session.Write(def, st);
            }
        }

        // ------------------------------------------------------------------ profils

        void RefreshProfileList()
        {
            suspendEvents = true;
            try
            {
                profileBox.Items.Clear();
                foreach (ProfileDto p in cfg.Profiles) profileBox.Items.Add(p.Name);
                if (cfg.ActiveProfile != null) profileBox.SelectedItem = cfg.ActiveProfile;
                if (profileBox.SelectedIndex < 0 && profileBox.Items.Count > 0) profileBox.SelectedIndex = 0;
            }
            finally { suspendEvents = false; }
            RebuildTrayMenu();
        }

        void LoadProfile(string name, bool apply)
        {
            ProfileDto p = cfg.Find(name);
            if (p == null) return;

            cfg.ActiveProfile = p.Name;
            Store.Save(cfg);

            Dictionary<string, ControlState> states = p.ToStates();
            // un profil peut etre incomplet (camera differente) : on complete avec les defauts
            foreach (ControlDef def in defs)
                if (!states.ContainsKey(def.Key)) states[def.Key] = new ControlState(def.Default, def.CanAuto);

            desired = states;
            PushToUi(desired);
            if (apply) ApplyDesiredToCamera();

            // Les reglages d'auto-exposition font partie du profil.
            softAe.Settings = p.EffectiveSoftAe();
            softAe.Reset();
            PushAeSettingsToUi();
            if (softAe.Settings.Enabled && apply) { softAe.Prime(); softAe.MeasureNow(); }

            suspendEvents = true;
            try { profileBox.SelectedItem = p.Name; }
            finally { suspendEvents = false; }

            RebuildTrayMenu();
            SetStatus("Profil « " + p.Name + " » applique", false);
        }

        void SaveCurrentProfile()
        {
            string name = profileBox.SelectedItem as string;
            if (name == null) { NewProfile(); return; }
            cfg.Upsert(ProfileDto.From(name, desired, softAe.Settings));
            Store.Save(cfg);
            SetStatus("Profil « " + name + " » enregistre", false);
        }

        void NewProfile()
        {
            string name = Prompt.Ask(this, "Nom du nouveau profil", "Profil " + (cfg.Profiles.Count + 1));
            if (string.IsNullOrEmpty(name)) return;
            cfg.Upsert(ProfileDto.From(name, desired, softAe.Settings));
            cfg.ActiveProfile = name;
            Store.Save(cfg);
            RefreshProfileList();
            SetStatus("Profil « " + name + " » cree", false);
        }

        void DeleteProfile()
        {
            string name = profileBox.SelectedItem as string;
            if (name == null) return;
            if (cfg.Profiles.Count <= 1)
            {
                MessageBox.Show(this, "Il faut garder au moins un profil.", "Webcam Control",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Supprimer le profil « " + name + " » ?", "Webcam Control",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            cfg.Remove(name);
            cfg.ActiveProfile = cfg.Profiles.Count > 0 ? cfg.Profiles[0].Name : null;
            Store.Save(cfg);
            RefreshProfileList();
            if (cfg.ActiveProfile != null) LoadProfile(cfg.ActiveProfile, true);
        }

        // ------------------------------------------------------------------ actions

        void ApplyFactoryDefaults()
        {
            desired = FactoryDefaults();
            PushToUi(desired);
            ApplyDesiredToCamera();
            SetStatus("Valeurs d'usine appliquees (non enregistrees)", false);
        }

        void ReadFromCamera()
        {
            if (!EnsureSession()) { SetStatus("Camera indisponible", true); return; }
            desired = ReadAllFromCamera();
            PushToUi(desired);
            SetStatus("Reglages relus depuis la camera", false);
        }

        // Fige chaque reglage encore en automatique sur la valeur ou il se trouve.
        void LockAutoControls()
        {
            if (!EnsureSession()) { SetStatus("Camera indisponible", true); return; }
            int locked = 0;
            foreach (ControlDef def in defs)
            {
                if (!def.CanAuto || !def.CanManual) continue;
                ControlState st;
                if (!session.TryRead(def, out st)) continue;
                if (!st.Auto) continue;
                ControlState manual = new ControlState(st.Value, false);
                session.Write(def, manual);
                desired[def.Key] = manual;
                locked++;
            }
            PushToUi(desired);
            SetStatus(locked == 0
                ? "Aucun reglage n'etait en automatique"
                : locked + " reglage(s) figes sur leur valeur actuelle. Pense a enregistrer le profil.", false);
        }

        void Nudge(string key, int delta)
        {
            ControlRow row;
            if (!rows.TryGetValue(key, out row)) return;
            row.Nudge(delta);
        }

        void CenterFraming()
        {
            ControlRow row;
            if (rows.TryGetValue("Pan", out row)) row.SetValueFromUser(0);
            if (rows.TryGetValue("Tilt", out row)) row.SetValueFromUser(0);
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control || e.Alt) return;
            switch (e.KeyCode)
            {
                case Keys.Left: Nudge("Pan", -1); e.Handled = true; break;
                case Keys.Right: Nudge("Pan", +1); e.Handled = true; break;
                case Keys.Up: Nudge("Tilt", +1); e.Handled = true; break;
                case Keys.Down: Nudge("Tilt", -1); e.Handled = true; break;
            }
        }

        // ------------------------------------------------------------------ watchdog

        void RunWatchdog()
        {
            if (desired.Count == 0) return;
            if (!EnsureSession()) { SetStatus("Camera absente, reconnexion en cours...", true); return; }

            int fixedCount = 0;
            foreach (ControlDef def in defs)
            {
                ControlState want;
                if (!desired.TryGetValue(def.Key, out want)) continue;
                ControlState actual;
                if (!session.TryRead(def, out actual)) continue;
                if (actual.Equals(want)) continue;
                if (session.Write(def, want)) fixedCount++;
            }

            if (fixedCount > 0)
            {
                Log.Write("Watchdog : " + fixedCount + " reglage(s) restaures");
                SetStatus(fixedCount + " reglage(s) avaient derive, restaures a " + DateTime.Now.ToString("HH:mm:ss"), false);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt32() == DBT_DEVNODES_CHANGED)
            {
                deviceChangeTimer.Stop();
                deviceChangeTimer.Start();
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------ apercu

        void TogglePreview()
        {
            if (previewStream.Running)
            {
                previewStream.Stop();
                btnPreview.Text = "Demarrer l'apercu";
                ClearPreviewImage();
                return;
            }
            string dev = session != null ? session.DeviceName : cfg.Device;
            previewStream.Start(cfg.FfmpegPath, dev, cfg.PreviewWidth, cfg.PreviewHeight, cfg.PreviewFps);
            btnPreview.Text = "Arreter l'apercu";
            SetStatus("Apercu demarre (" + cfg.PreviewWidth + "x" + cfg.PreviewHeight + ")", false);
        }

        void OnFrameReady(Bitmap bmp)
        {
            if (IsDisposed || !IsHandleCreated) { bmp.Dispose(); return; }
            // Mesure faite ici, sur le thread de lecture : le thread interface reste libre.
            double y = Metering.Luma(bmp);
            if (y >= 0) { previewLuma = y; previewLumaAt = DateTime.Now; }
            try { BeginInvoke(new Action<Bitmap>(SetFrame), bmp); }
            catch { bmp.Dispose(); }
        }

        void SetFrame(Bitmap bmp)
        {
            if (IsDisposed) { bmp.Dispose(); return; }
            Image old = preview.Image;
            preview.Image = bmp;
            if (old != null) old.Dispose();
        }

        void OnPreviewStopped(string message)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    btnPreview.Text = "Demarrer l'apercu";
                    ClearPreviewImage();
                    if (!string.IsNullOrEmpty(message))
                    {
                        SetStatus("Apercu arrete : " + message.Replace(Environment.NewLine, " "), true);
                        Log.Write("Apercu : " + message);
                    }
                }));
            }
            catch { }
        }

        void ClearPreviewImage()
        {
            Image old = preview.Image;
            preview.Image = null;
            if (old != null) old.Dispose();
            preview.Invalidate();
        }

        void OnPreviewPaint(object sender, PaintEventArgs e)
        {
            if (preview.Image == null)
            {
                string msg = "Apercu arrete";
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 150, 160)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    e.Graphics.DrawString(msg, Font, b, preview.ClientRectangle, sf);
                }
                return;
            }
            if (!cfg.ShowThirds) return;

            Rectangle r = ImageBounds();
            using (Pen p = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
            {
                p.DashStyle = DashStyle.Dash;
                for (int i = 1; i <= 2; i++)
                {
                    int x = r.Left + r.Width * i / 3;
                    int y = r.Top + r.Height * i / 3;
                    e.Graphics.DrawLine(p, x, r.Top, x, r.Bottom);
                    e.Graphics.DrawLine(p, r.Left, y, r.Right, y);
                }
            }
        }

        // Rectangle reellement occupe par l'image dans le PictureBox en mode Zoom.
        Rectangle ImageBounds()
        {
            Image img = preview.Image;
            if (img == null) return preview.ClientRectangle;
            Rectangle c = preview.ClientRectangle;
            double scale = Math.Min((double)c.Width / img.Width, (double)c.Height / img.Height);
            int w = (int)(img.Width * scale), h = (int)(img.Height * scale);
            return new Rectangle(c.Left + (c.Width - w) / 2, c.Top + (c.Height - h) / 2, w, h);
        }

        // ------------------------------------------------------------------ tray

        void BuildTray()
        {
            trayMenu = new ContextMenuStrip();
            tray = new NotifyIcon();
            tray.Icon = Glyph.MakeIcon();
            tray.Text = "Webcam Control";
            tray.Visible = true;
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowPanel(); };
            RebuildTrayMenu();
        }

        void RebuildTrayMenu()
        {
            if (trayMenu == null) return;
            trayMenu.Items.Clear();

            ToolStripMenuItem open = new ToolStripMenuItem("Ouvrir le panneau");
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += delegate { ShowPanel(); };
            trayMenu.Items.Add(open);
            trayMenu.Items.Add(new ToolStripSeparator());

            foreach (ProfileDto p in cfg.Profiles)
            {
                string name = p.Name;
                ToolStripMenuItem it = new ToolStripMenuItem(name);
                it.Checked = string.Equals(name, cfg.ActiveProfile, StringComparison.OrdinalIgnoreCase);
                it.Click += delegate { LoadProfile(name, true); };
                trayMenu.Items.Add(it);
            }

            trayMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem reapply = new ToolStripMenuItem("Reappliquer maintenant");
            reapply.Click += delegate { if (EnsureSession()) { ApplyDesiredToCamera(); SetStatus("Reglages reappliques", false); } };
            trayMenu.Items.Add(reapply);

            ToolStripMenuItem quit = new ToolStripMenuItem("Quitter");
            quit.Click += delegate { reallyExit = true; Close(); };
            trayMenu.Items.Add(quit);
        }

        void ShowPanel()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                tray.ShowBalloonTip(2000, "Webcam Control",
                    "Toujours actif dans la zone de notification. Les reglages restent surveilles.",
                    ToolTipIcon.Info);
                return;
            }
            previewStream.Dispose();
            if (session != null) session.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Store.Save(cfg);
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ divers

        void SetEnabled(bool on)
        {
            groupsHost.Enabled = on;
            profileBox.Enabled = on;
            btnSave.Enabled = on; btnNew.Enabled = on; btnDelete.Enabled = on;
            btnDefaults.Enabled = on; btnLockAuto.Enabled = on; btnReread.Enabled = on;
            framingPad.Enabled = on;
        }

        void SetStatus(string text, bool warn)
        {
            status.Text = text;
            status.ForeColor = warn ? Color.FromArgb(180, 60, 40) : SystemColors.ControlText;
        }
    }

    // ---------------------------------------------------------------------- une ligne de reglage

    internal class ControlRow
    {
        public readonly ControlDef Def;
        public readonly Label NameLabel;
        public readonly CheckBox AutoBox;
        public readonly TrackBar Slider;
        public readonly Label ValueLabel;

        readonly MainForm owner;
        readonly bool hasAuto;
        bool quiet;
        bool driven;      // pilote par l'auto-exposition logicielle

        public ControlRow(ControlDef def, MainForm owner)
        {
            Def = def;
            this.owner = owner;
            // On ne peut pas se fier a AutoBox.Visible : tant que la fenetre n'est pas
            // affichee, Visible renvoie false et tous les reglages passeraient pour manuels.
            hasAuto = def.CanAuto;

            NameLabel = new Label();
            NameLabel.Text = def.Label;
            NameLabel.AutoSize = false;
            NameLabel.Dock = DockStyle.Fill;
            NameLabel.TextAlign = ContentAlignment.MiddleLeft;

            AutoBox = new CheckBox();
            AutoBox.Text = "auto";
            AutoBox.AutoSize = false;
            AutoBox.Dock = DockStyle.Fill;
            AutoBox.Visible = def.CanAuto;
            AutoBox.Enabled = def.CanAuto && def.CanManual;
            AutoBox.CheckedChanged += delegate { if (!quiet) Push(); UpdateEnabled(); };

            Slider = new TrackBar();
            Slider.Minimum = def.Min;
            Slider.Maximum = def.Max;
            Slider.SmallChange = def.Step;
            Slider.LargeChange = Math.Max(def.Step, (def.Max - def.Min) / 10);
            Slider.TickStyle = TickStyle.None;
            Slider.Dock = DockStyle.Fill;
            Slider.ValueChanged += delegate { if (!quiet) Push(); RefreshLabel(); };

            ValueLabel = new Label();
            ValueLabel.AutoSize = false;
            ValueLabel.Dock = DockStyle.Fill;
            ValueLabel.TextAlign = ContentAlignment.MiddleRight;

            RefreshLabel();
            UpdateEnabled();
        }

        bool IsAuto { get { return hasAuto && AutoBox.Checked; } }

        public void SetDriven(bool value)
        {
            driven = value;
            NameLabel.Text = value ? Def.Label + " (auto)" : Def.Label;
            AutoBox.Enabled = !value && Def.CanAuto && Def.CanManual;
            UpdateEnabled();
        }

        void UpdateEnabled()
        {
            Slider.Enabled = !IsAuto && !driven;
            ValueLabel.ForeColor = Slider.Enabled ? SystemColors.ControlText : SystemColors.GrayText;
        }

        void RefreshLabel()
        {
            ValueLabel.Text = IsAuto ? "auto" : Def.Show(Slider.Value);
        }

        void Push()
        {
            owner.OnRowChanged(Def, Current());
        }

        public ControlState Current()
        {
            return new ControlState(Slider.Value, IsAuto);
        }

        public void SetState(ControlState st)
        {
            quiet = true;
            try
            {
                int v = st.Value;
                if (v < Slider.Minimum) v = Slider.Minimum;
                if (v > Slider.Maximum) v = Slider.Maximum;
                Slider.Value = v;
                AutoBox.Checked = st.Auto && hasAuto;
            }
            finally { quiet = false; }
            RefreshLabel();
            UpdateEnabled();
        }

        public void Nudge(int delta)
        {
            if (!Slider.Enabled) return;
            SetValueFromUser(Slider.Value + delta * Def.Step);
        }

        public void SetValueFromUser(int value)
        {
            if (value < Slider.Minimum) value = Slider.Minimum;
            if (value > Slider.Maximum) value = Slider.Maximum;
            Slider.Value = value;   // declenche ValueChanged donc Push()
        }
    }

    // ---------------------------------------------------------------------- petite boite de saisie

    internal static class Prompt
    {
        public static string Ask(IWin32Window parent, string caption, string preset)
        {
            using (Form f = new Form())
            {
                f.Text = "Webcam Control";
                f.ClientSize = new Size(330, 110);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false; f.MaximizeBox = false;

                Label l = new Label();
                l.Text = caption; l.SetBounds(12, 12, 300, 18); l.AutoSize = false;

                TextBox tb = new TextBox();
                tb.Text = preset; tb.SetBounds(12, 34, 306, 24);
                tb.SelectAll();

                Button ok = new Button();
                ok.Text = "OK"; ok.DialogResult = DialogResult.OK; ok.SetBounds(160, 70, 75, 26);

                Button cancel = new Button();
                cancel.Text = "Annuler"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(243, 70, 75, 26);

                f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;

                return f.ShowDialog(parent) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }

    // ---------------------------------------------------------------------- icone dessinee a la volee

    internal static class Glyph
    {
        public static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush body = new SolidBrush(Color.FromArgb(40, 44, 52)))
                        g.FillEllipse(body, 1, 1, 30, 30);
                    using (SolidBrush ring = new SolidBrush(Color.FromArgb(90, 170, 255)))
                        g.FillEllipse(ring, 7, 7, 18, 18);
                    using (SolidBrush lens = new SolidBrush(Color.FromArgb(20, 24, 32)))
                        g.FillEllipse(lens, 11, 11, 10, 10);
                    using (SolidBrush glint = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                        g.FillEllipse(glint, 13, 12, 4, 4);
                }
                IntPtr h = bmp.GetHicon();
                using (Icon tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
        }
    }
}
