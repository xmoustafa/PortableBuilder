using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortableBuilder
{
    public sealed class BuilderForm : Form
    {
        private DataGridView _grid;
        private TextBox _txtInstall;
        private TextBox _txtExe;
        private TextBox _txtData;
        private TextBox _txtReg;
        private TextBox _txtServices;
        private TextBox _txtArgs;
        private TextBox _txtOutput;
        private ComboBox _cmbMode;
        private CheckBox _chkRegistry;
        private CheckBox _chkWait;
        private CheckBox _chkRestore;
        private CheckBox _chkSymlink;
        private CheckBox _chkDeep;
        private CheckBox _chkCapture;
        private CheckBox _chkAutoSave;
        private CheckBox _chkCleanPaths;
        private CheckBox _chkRobloxSetup;
        private CheckBox _chkGameLoopSetup;
        private CheckBox _chkFiveMSetup;
        private CheckBox _chkBestJunction;
        private ToolTip _tips;
        private Button _btnRefresh;
        private Button _btnBrowseExe;
        private Button _btnBrowseFolder;
        private Button _btnManualSearch;
        private Button _btnBuild;
        private Button _btnOpen;
        private Button _btnSaveDb;
        private Button _btnOut;
        private Button _btnLog;
        private Label _lblCount;
        private Label _lblNotes;
        private Label _lblRegWarning;
        private readonly List<string> _registryWarnings = new List<string>();
        private Panel _headerPanel;
        private Panel _bodyPanel;
        private GroupBox _detailsPanel;
        private GroupBox _optionsPanel;
        private readonly object _logLock = new object();
        private string _logPath;

        private SystemIndex _index;
        private AppDatabase _db;
        private DetectedApp _current;
        private List<DetectedApp> _detected = new List<DetectedApp>();
        private bool _busy;
        private string _lastOutput;
        private string _lastAutoOutput;

        private sealed class DetectionResult
        {
            public AppDatabase Db;
            public SystemIndex Index;
            public List<DetectedApp> Apps;
        }

        public BuilderForm()
        {
            Text = "PortableBuilder V1.1 - Moustafa";
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1000, 700);
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(247, 246, 252);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            _logPath = ""; // no temp/log files written by the builder

            BuildUi();
            Shown += delegate
            {
                LayoutUi();
                BeginInvoke(new Action(delegate
                {
                    LayoutUi();
                    ResizeGridColumns();
                }));
                Updater.ScheduleCheck(this);
            };
            Resize += delegate { LayoutUi(); };
            Load += delegate { RefreshDetection(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!_busy) return;
                DialogResult answer = MessageBox.Show(this,
                    "A scan or build is still running.\r\n\r\n" +
                    "Closing now can leave a partially written package.\r\n\r\nClose anyway?",
                    "PortableBuilder V1.1", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) e.Cancel = true;
            };
        }

        private void BuildUi()
        {
            // ---------------- header / body containers ----------------
            Panel header = new Panel();
            header.BackColor = Color.White;
            header.Padding = new Padding(16, 8, 16, 8);
            _headerPanel = header;
            Controls.Add(header);

            Panel accent = new Panel();
            accent.BackColor = Color.FromArgb(47, 111, 186);
            accent.Height = 3;
            accent.Dock = DockStyle.Bottom;
            header.Controls.Add(accent);

            TableLayoutPanel headerTable = new TableLayoutPanel();
            headerTable.Dock = DockStyle.Fill;
            headerTable.ColumnCount = 1;
            headerTable.RowCount = 3;
            headerTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            headerTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            headerTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            headerTable.Padding = new Padding(62, 0, 0, 0);
            header.Controls.Add(headerTable);

            PictureBox logo = new PictureBox();
            logo.Size = new Size(48, 48);
            logo.Location = new Point(12, 10);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.Transparent;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream("PortableBuilder.app.ico"))
                {
                    if (stream != null)
                    {
                        // app.ico contains PNG-compressed entries; Icon.ToBitmap()
                        // renders those as noise, so decode the PNG entry directly.
                        Bitmap png = LoadIcoPng(stream, 128);
                        if (png != null) logo.Image = png;
                    }
                }
            }
            catch { }
            if (logo.Image == null && Icon != null)
            {
                try { logo.Image = Icon.ToBitmap(); } catch { }
            }
            header.Controls.Add(logo);
            logo.BringToFront();
            FormClosed += delegate
            {
                try { if (logo.Image != null) logo.Image.Dispose(); } catch { }
            };

            Label title = new Label();
            title.Text = "PortableBuilder V1.1";
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(35, 35, 45);
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            headerTable.Controls.Add(title, 0, 0);

            TableLayoutPanel subtitleBox = new TableLayoutPanel();
            subtitleBox.Dock = DockStyle.Fill;
            subtitleBox.ColumnCount = 1;
            subtitleBox.RowCount = 2;
            subtitleBox.Margin = Padding.Empty;
            subtitleBox.Padding = Padding.Empty;
            subtitleBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            subtitleBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            headerTable.Controls.Add(subtitleBox, 0, 1);

            Label subtitle = new Label();
            subtitle.Text = "Detect an installed app  ->  review paths and registry  ->  build a click-and-run package";
            subtitle.ForeColor = Color.FromArgb(120, 120, 130);
            subtitle.Dock = DockStyle.Fill;
            subtitle.TextAlign = ContentAlignment.MiddleLeft;
            subtitle.Margin = Padding.Empty;
            subtitleBox.Controls.Add(subtitle, 0, 0);

            Label developer = new Label();
            developer.Text = "Developed by : Moustafa";
            developer.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            developer.ForeColor = Color.FromArgb(47, 111, 186);
            developer.Dock = DockStyle.Fill;
            developer.TextAlign = ContentAlignment.MiddleLeft;
            developer.Margin = Padding.Empty;
            subtitleBox.Controls.Add(developer, 0, 1);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.BackColor = Color.Transparent;
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 4, 0, 0);
            headerTable.Controls.Add(actions, 0, 2);

            _btnRefresh = MakeButton("Refresh", 88);
            _btnRefresh.Click += delegate { RefreshDetection(); };
            _btnBrowseExe = MakeButton("EXE...", 72);
            _btnBrowseExe.Click += delegate { BrowseExe(); };
            _btnBrowseFolder = MakeButton("Folder...", 86);
            _btnBrowseFolder.Click += delegate { BrowseFolder(); };
            _btnManualSearch = MakeButton("Manual Search...", 118);
            _btnManualSearch.Click += delegate { ManualSearch(); };
            _lblCount = new Label();
            _lblCount.Text = "Ready";
            _lblCount.AutoSize = true;
            _lblCount.ForeColor = Color.FromArgb(90, 90, 100);
            _lblCount.Margin = new Padding(12, 10, 0, 0);

            _btnBuild = MakeButton("Build Package", 150);
            _btnBuild.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _btnBuild.Margin = new Padding(20, 0, 6, 0);
            _btnBuild.Enabled = false;
            _btnBuild.Click += delegate { BuildPackage(); };

            _btnOpen = MakeButton("Open", 70);
            _btnOpen.Enabled = false;
            _btnOpen.Click += delegate { OpenOutput(); };

            _btnSaveDb = MakeButton("Save to DB", 92);
            _btnSaveDb.Enabled = false;
            _btnSaveDb.Click += delegate { SaveToDb(); };

            _btnLog = MakeButton("View Log", 90);
            _btnLog.Click += delegate { OpenLog(); };

            actions.Controls.Add(_btnRefresh);
            actions.Controls.Add(_btnBrowseExe);
            actions.Controls.Add(_btnBrowseFolder);
            actions.Controls.Add(_btnManualSearch);
            actions.Controls.Add(_lblCount);
            actions.Controls.Add(_btnBuild);
            actions.Controls.Add(_btnOpen);
            actions.Controls.Add(_btnSaveDb);
            actions.Controls.Add(_btnLog);

            StyleButton(_btnRefresh, Color.White, Color.FromArgb(47, 111, 186));
            StyleButton(_btnBrowseExe, Color.White, Color.FromArgb(47, 111, 186));
            StyleButton(_btnBrowseFolder, Color.White, Color.FromArgb(47, 111, 186));
            StyleButton(_btnManualSearch, Color.White, Color.FromArgb(47, 111, 186));
            StyleButton(_btnBuild, Color.FromArgb(47, 111, 186), Color.White);
            StyleButton(_btnOpen, Color.White, Color.FromArgb(47, 111, 186));
            StyleButton(_btnSaveDb, Color.White, Color.FromArgb(47, 111, 186));
            StyleButton(_btnLog, Color.White, Color.FromArgb(47, 111, 186));
            foreach (Button b in new Button[] { _btnRefresh, _btnBrowseExe, _btnBrowseFolder, _btnManualSearch, _btnOpen, _btnSaveDb, _btnLog })
            {
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = Color.FromArgb(185, 205, 230);
            }

            // ---------------- body ----------------
            Panel body = new Panel();
            _bodyPanel = body;
            Controls.Add(body);

            // ---------------- app grid ----------------
            _grid = new DataGridView();
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 30;
            _grid.RowTemplate.Height = 27;
            _grid.Columns.Add("Name", "Application");
            _grid.Columns.Add("Category", "Category");
            _grid.Columns.Add("Install", "Install path");
            _grid.Columns[0].FillWeight = 30;
            _grid.Columns[1].FillWeight = 16;
            _grid.Columns[2].FillWeight = 54;
            _grid.Columns[0].MinimumWidth = 150;
            _grid.Columns[1].MinimumWidth = 100;
            _grid.Columns[2].MinimumWidth = 300;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 241, 244);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(40, 40, 50);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(240, 241, 244);
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(40, 40, 50);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(47, 111, 186);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 252);
            _grid.GridColor = Color.FromArgb(228, 230, 235);
            _grid.SelectionChanged += delegate { SelectionChanged(); };
            _grid.Resize += delegate { ResizeGridColumns(); };
            body.Controls.Add(_grid);

            // ---------------- details ----------------
            GroupBox details = new GroupBox();
            details.Text = "Selected application";
            details.Padding = new Padding(10, 8, 10, 8);
            details.BackColor = Color.White;
            details.ForeColor = Color.FromArgb(55, 45, 80);
            _detailsPanel = details;
            body.Controls.Add(details);

            TableLayoutPanel detailTable = new TableLayoutPanel();
            detailTable.Dock = DockStyle.Fill;
            detailTable.ColumnCount = 2;
            detailTable.RowCount = 6;
            detailTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            detailTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            detailTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            detailTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            detailTable.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            detailTable.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            detailTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            detailTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            details.Controls.Add(detailTable);

            _txtInstall = MakeTextBox(false);
            _txtExe = MakeTextBox(false);
            _txtData = MakeTextBox(true);
            _txtReg = MakeTextBox(true);
            _txtReg.TextChanged += delegate { UpdateRegistryWarning(); };
            _txtServices = MakeTextBox(true);
            _txtArgs = MakeTextBox(false);

            TableLayoutPanel regHost = new TableLayoutPanel();
            regHost.Dock = DockStyle.Fill;
            regHost.ColumnCount = 1;
            regHost.RowCount = 2;
            regHost.Margin = new Padding(4, 3, 4, 3);
            regHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            regHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            _txtReg.Dock = DockStyle.Fill;
            _txtReg.Margin = Padding.Empty;
            regHost.Controls.Add(_txtReg, 0, 0);

            _lblRegWarning = new Label();
            _lblRegWarning.Dock = DockStyle.Fill;
            _lblRegWarning.Margin = Padding.Empty;
            _lblRegWarning.TextAlign = ContentAlignment.MiddleLeft;
            _lblRegWarning.RightToLeft = RightToLeft.Yes;
            _lblRegWarning.BackColor = Color.FromArgb(255, 246, 205);
            _lblRegWarning.ForeColor = Color.FromArgb(145, 95, 0);
            _lblRegWarning.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            _lblRegWarning.Cursor = Cursors.Hand;
            _lblRegWarning.Visible = false;
            _lblRegWarning.Click += delegate { ShowRegistryWarnings(); };
            regHost.Controls.Add(_lblRegWarning, 0, 1);

            AddDetailRow(detailTable, 0, "Install folder", _txtInstall);
            AddDetailRow(detailTable, 1, "Main EXE", _txtExe);
            AddDetailRow(detailTable, 2, "Data folders (one per line)", _txtData);
            AddDetailRow(detailTable, 3, "Registry keys (one per line)", regHost);
            AddDetailRow(detailTable, 4, "Services (one per line)", _txtServices);
            AddDetailRow(detailTable, 5, "Arguments", _txtArgs);

            // ---------------- options ----------------
            GroupBox options = new GroupBox();
            options.Text = "Build options";
            options.Padding = new Padding(10, 8, 10, 8);
            options.BackColor = Color.White;
            options.ForeColor = Color.FromArgb(55, 45, 80);
            _optionsPanel = options;
            Controls.Add(options);

            TableLayoutPanel optionTable = new TableLayoutPanel();
            optionTable.Dock = DockStyle.Fill;
            optionTable.ColumnCount = 2;
            optionTable.RowCount = 3;
            optionTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            optionTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            optionTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            optionTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 90F));
            optionTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            options.Controls.Add(optionTable);

            Label lblOut = MakeLabel("Output folder:");
            optionTable.Controls.Add(lblOut, 0, 0);

            TableLayoutPanel outputRow = new TableLayoutPanel();
            outputRow.Dock = DockStyle.Fill;
            outputRow.ColumnCount = 2;
            outputRow.RowCount = 1;
            outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            optionTable.Controls.Add(outputRow, 1, 0);

            _txtOutput = MakeTextBox(false);
            _txtOutput.Margin = new Padding(0, 5, 4, 5);
            outputRow.Controls.Add(_txtOutput, 0, 0);

            _btnOut = MakeButton("Browse...", 88);
            _btnOut.Dock = DockStyle.Fill;
            _btnOut.Margin = new Padding(4, 5, 0, 5);
            _btnOut.Click += delegate { BrowseOutput(); };
            StyleButton(_btnOut, Color.White, Color.FromArgb(47, 111, 186));
            _btnOut.FlatAppearance.BorderSize = 1;
            _btnOut.FlatAppearance.BorderColor = Color.FromArgb(185, 205, 230);
            outputRow.Controls.Add(_btnOut, 1, 0);
            Label lblMode = MakeLabel("Mode:");
            optionTable.Controls.Add(lblMode, 0, 1);

            FlowLayoutPanel modeRow = new FlowLayoutPanel();
            modeRow.Dock = DockStyle.Fill;
            modeRow.FlowDirection = FlowDirection.LeftToRight;
            modeRow.WrapContents = true;
            modeRow.AutoScroll = true;
            modeRow.Margin = new Padding(0, 0, 0, 0);
            optionTable.Controls.Add(modeRow, 1, 1);

            _cmbMode = new ComboBox();
            _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMode.Width = 220;
            _cmbMode.Margin = new Padding(0, 4, 12, 4);
            _cmbMode.Items.Add("Junction (NTFS)");
            _cmbMode.Items.Add("Copy (USB/FAT32)");
            _cmbMode.SelectedIndex = 0;
            modeRow.Controls.Add(_cmbMode);

            _chkRegistry = MakeCheck("Import registry");
            _chkRegistry.Checked = true;
            _chkWait = MakeCheck("Wait for exit");
            _chkWait.Checked = true;
            _chkRestore = MakeCheck("Restore on exit");
            _chkRestore.Checked = false;
            _chkSymlink = MakeCheck("Best Portable layout");
            _chkSymlink.Checked = true;
            _chkSymlink.CheckedChanged += delegate
            {
                _cmbMode.Enabled = !_chkSymlink.Checked && !_busy;
                _chkBestJunction.Enabled = _chkSymlink.Checked && !_busy && !IsSymlinkForcedApp();
                if (!_chkSymlink.Checked) _chkBestJunction.Checked = false;
            };
            _chkBestJunction = MakeCheck("Best + Junction (local NTFS)");
            _chkBestJunction.Checked = false;
            _chkBestJunction.Enabled = false;
            _chkDeep = MakeCheck("Deep registry");
            _chkDeep.Checked = true;
            _chkCapture = MakeCheck("Capture updates on exit");
            _chkCapture.Checked = false;
            _chkCapture.CheckedChanged += delegate
            {
                if (_chkCapture.Checked)
                {
                    _chkWait.Checked = true;
                    _chkRegistry.Checked = true;
                }
            };
            _chkRestore.CheckedChanged += delegate
            {
                if (_chkRestore.Checked) _chkWait.Checked = true;
            };
            _chkAutoSave = MakeCheck("Auto-save detected");
            _chkAutoSave.Checked = false;
            _chkCleanPaths = MakeCheck("Clean paths (no D:/E:)");
            _chkCleanPaths.Checked = true;
            _chkRobloxSetup = MakeCheck("Roblox Portable Setup (sharing + Enable_Sharing)");
            _chkRobloxSetup.Checked = true;
            _chkRobloxSetup.Enabled = false;
            _chkGameLoopSetup = MakeCheck("GameLoop Portable Setup (Apply/Remove + Snapshot/Temp)");
            _chkGameLoopSetup.Checked = true;
            _chkGameLoopSetup.Enabled = false;
            _chkFiveMSetup = MakeCheck("FiveM Portable Setup (split share)");
            _chkFiveMSetup.Checked = true;
            _chkFiveMSetup.Enabled = false;
            modeRow.Controls.Add(_chkRegistry);
            modeRow.Controls.Add(_chkWait);
            modeRow.Controls.Add(_chkRestore);
            modeRow.Controls.Add(_chkSymlink);
            modeRow.Controls.Add(_chkBestJunction);
            modeRow.Controls.Add(_chkDeep);
            modeRow.Controls.Add(_chkCapture);
            modeRow.Controls.Add(_chkAutoSave);
            modeRow.Controls.Add(_chkCleanPaths);
            modeRow.Controls.Add(_chkRobloxSetup);
            modeRow.Controls.Add(_chkGameLoopSetup);
            modeRow.Controls.Add(_chkFiveMSetup);
            _cmbMode.Enabled = false;

            // Egyptian-Arabic hover hints for every build option.
            _tips = new ToolTip();
            _tips.AutoPopDelay = 20000;
            _tips.InitialDelay = 350;
            _tips.ReshowDelay = 100;
            _tips.ShowAlways = true;
            _tips.UseFading = true;
            _tips.UseAnimation = true;

            AddTip(_chkRegistry,
                "بيستورد مفاتيح الريجستري اللي في الخانة اللي فوق جوه الباكدج،\r\n" +
                "عشان البرنامج يشتغل من غير تثبيت. شيل العلامة لو مش عايز ريجستري.");
            AddTip(_chkWait,
                "اللانشر يستنى البرنامج أو اللعبة تقفل قبل ما يكمّل.\r\n" +
                "لازم تكون مفعّلة لو عايز Restore أو Capture.");
            AddTip(_chkRestore,
                "بعد ما البرنامج يقفل، يشيل اللينكات ويرجّع الملفات الأصلية.\r\n" +
                "البيانات اللي جوه الباكدج بتفضل زي ما هي.");
            AddTip(_chkSymlink,
                "أحسن ترتيب للباكدج: AppData + ملف .reg واحد + Symlink.\r\n" +
                "لو شيلت العلامة هيبني Normal layout (Junction أو Copy).");
            AddTip(_chkDeep,
                "بحث أعمق في الريجستري وقت اكتشاف البرامج.\r\n" +
                "بيجيب برامج أكتر، بس بياخد وقت أطول.");
            AddTip(_chkCapture,
                "بعد الخروج، يحفظ تغييرات الريجستري جوه الباكدج.\r\n" +
                "بيشغّل Wait for exit و Import registry لوحده.");
            AddTip(_chkAutoSave,
                "أي برنامج يتكتشف من Uninstall أو Program Files يتحفظ\r\n" +
                "في قاعدة البيانات (apps.json) تلقائيًا بعد الفحص.");
            AddTip(_chkCleanPaths,
                "لو البرنامج متثبت في مسار غريب زي D:\\Programs،\r\n" +
                "يخلّي الباكدج يستخدم %ProgramFiles%\\<App> بدل المسار ده.");
            AddTip(_chkRobloxSetup,
                "بناء Roblox للمشاركة على الشبكة:\r\n" +
                "Roblox.exe + Enable_Sharing.exe + Uninstall.exe.\r\n" +
                "شيل العلامة لو عايز نسخة بورتابل عادية من غير شير.");
            AddTip(_chkGameLoopSetup,
                "بناء GameLoop بالكامل: GameLoop.exe + PUBG Mobile.exe\r\n" +
                "+ Apply Gameloop.exe + Remove Gameloop.exe + Snapshot/Temp.\r\n" +
                "لازم يكون على D:\\TxGameAssistant محلي — مش على شير شبكة.\r\n" +
                "شيل العلامة لو عايز نسخة بورتابل عادية.");
            AddTip(_chkFiveMSetup,
                "بناء FiveM للمشاركة: التطبيق محلي على كل جهاز،\r\n" +
                "والداتا فقط (resources والخرايط والـ shaders والتحميلات)\r\n" +
                "في مجلد مستقل باسم الجهاز على الشير.\r\n" +
                "كده كل جهاز معزول ومافيش تعارض في المابات أو الكاش.");
            AddTip(_chkBestJunction,
                "Best layout + Junction: نفس ترتيب Best لكن من غير Admin.\r\n" +
                "يشتغل بس لو الباكدج على قرص محلي NTFS (مش شير شبكة).\r\n" +
                "Roblox وFiveM بيفرضوا Symlink تلقائيًا.");
            AddTip(_cmbMode,
                "Junction: محلي NTFS ومن غير Admin.\r\n" +
                "Copy: نسخ كامل للداتا، مناسب USB/FAT32.");
            AddTip(lblMode,
                "Junction و Copy بيتفعّلوا لما تشيل علامة Best Portable layout.\r\n" +
                "Best layout بيستخدم Symlink عشان يشتغل مع الشبكة.");

            FormClosed += delegate
            {
                try { if (_tips != null) _tips.Dispose(); } catch { }
            };

            _lblNotes = new Label();
            _lblNotes.Text = "Select an app to begin.";
            _lblNotes.ForeColor = Color.FromArgb(95, 85, 120);
            _lblNotes.Dock = DockStyle.Fill;
            _lblNotes.TextAlign = ContentAlignment.MiddleLeft;
            _lblNotes.AutoSize = false;
            _lblNotes.Margin = new Padding(0, 0, 0, 0);
            optionTable.Controls.Add(_lblNotes, 1, 2);

            // The feature options are visible again. The log button stays hidden:
            // no log file is written by default (LogFile is empty).
            _btnLog.Visible = false;
        }

        private Button MakeButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(width, 28);
            button.Margin = new Padding(0, 0, 6, 0);
            button.Padding = new Padding(8, 0, 8, 0);
            button.TextAlign = ContentAlignment.MiddleCenter;
            return button;
        }

        private static void StyleButton(Button button, Color backColor, Color foreColor)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        private Label MakeLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(0, 0, 4, 0);
            return label;
        }

        private TextBox MakeTextBox(bool multiline)
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Multiline = multiline;
            box.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
            box.WordWrap = false;
            box.Margin = new Padding(4, 3, 4, 3);
            return box;
        }

        private CheckBox MakeCheck(string text)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.AutoSize = true;
            box.Margin = new Padding(0, 5, 12, 0);
            return box;
        }

        private void AddTip(Control control, string text)
        {
            if (control == null || _tips == null || text == null) return;

            // Wrap each line with RLE/PDF so Windows renders the mixed
            // Arabic/English tooltip right-to-left.
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            StringBuilder rtl = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                rtl.Append('\u202B').Append(lines[i]).Append('\u202C');
                if (i < lines.Length - 1) rtl.Append("\r\n");
            }
            _tips.SetToolTip(control, rtl.ToString());
        }

        // ICO files may store each size as PNG instead of BMP. Icon.ToBitmap()
        // misreads those as noise, so pull the PNG bytes out of the directory.
        private static Bitmap LoadIcoPng(Stream stream, int desiredSize)
        {
            if (stream == null) return null;

            byte[] bytes;
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            if (bytes.Length < 6) return null;

            int count = BitConverter.ToUInt16(bytes, 4);
            int bestOffset = -1;
            int bestSize = 0;
            int bestDiff = int.MaxValue;

            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                if (entry + 16 > bytes.Length) break;

                int width = bytes[entry];
                if (width == 0) width = 256;
                int size = BitConverter.ToInt32(bytes, entry + 8);
                int offset = BitConverter.ToInt32(bytes, entry + 12);
                if (size <= 0 || offset < 0 || offset + size > bytes.Length) continue;

                if (!(bytes[offset] == 0x89 && bytes[offset + 1] == 0x50 &&
                      bytes[offset + 2] == 0x4E && bytes[offset + 3] == 0x47))
                    continue;

                int diff = Math.Abs(width - desiredSize);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestOffset = offset;
                    bestSize = size;
                }
            }

            if (bestOffset < 0) return null;
            try
            {
                using (MemoryStream png = new MemoryStream(bytes, bestOffset, bestSize, false))
                using (Image image = Image.FromStream(png))
                    return new Bitmap(image);
            }
            catch { return null; }
        }

        private void AddDetailRow(TableLayoutPanel table, int row, string label, Control control)
        {
            Label lbl = new Label();
            lbl.Text = label;
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.Margin = new Padding(0, 0, 4, 0);
            table.Controls.Add(lbl, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private void RefreshDetection()
        {
            if (_busy) return;

            _busy = true;
            SetBusy(true);
            _grid.Rows.Clear();
            _detected.Clear();
            _current = null;
            ClearDetails();
            _lblCount.Text = "Scanning...";
            Log("Loading application database...");

            TaskScheduler ui = TaskScheduler.FromCurrentSynchronizationContext();
            bool deepScan = _chkDeep.Checked;
            Task<DetectionResult> task;
            try
            {
                task = Task.Factory.StartNew<DetectionResult>(delegate
                {
                    DetectionResult result = new DetectionResult();
                    result.Db = AppDatabase.Load();
                    Log("Database: " + result.Db.apps.Count + " applications");
                    result.Index = SystemIndex.Build(Log, deepScan);
                    result.Apps = AppDetector.DetectAll(result.Db, result.Index, Log);
                    return result;
                });
            }
            catch (Exception ex)
            {
                _busy = false;
                SetBusy(false);
                _lblCount.Text = "Scan failed";
                Log("Could not start the scan: " + ex.Message);
                return;
            }

            task.ContinueWith(delegate(Task<DetectionResult> t)
            {
                if (IsDisposed || Disposing) return;
                try
                {
                    _busy = false;
                    SetBusy(false);

                    if (t.IsFaulted)
                    {
                        Exception ex = t.Exception == null
                            ? new InvalidOperationException("Unknown scan error.")
                            : t.Exception.GetBaseException();
                        _lblCount.Text = "Scan failed";
                        Log("Scan failed: " + ex.Message);
                        MessageBox.Show(this, "Detection failed:\r\n" + ex.Message, "PortableBuilder V1.1",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    if (t.IsCanceled)
                    {
                        _lblCount.Text = "Scan cancelled";
                        Log("Scan cancelled.");
                        return;
                    }

                    DetectionResult result = t.Result;
                    _index = result.Index;
                    _db = result.Db;
                    _detected = result.Apps;
                    _lblCount.Text = result.Apps.Count + " apps detected";

                    if (_chkAutoSave.Checked)
                    {
                        int autoSaved = 0;
                        foreach (DetectedApp d in result.Apps)
                        {
                            if (string.Equals(d.Source, "uninstall", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(d.Source, "programfiles", StringComparison.OrdinalIgnoreCase))
                            {
                                _db.UpsertFromDetected(d, "Detected", false);
                                autoSaved++;
                            }
                        }
                        if (autoSaved > 0)
                        {
                            try
                            {
                                _db.Save();
                                Log("Auto-saved " + autoSaved + " detected app(s) to the database.");
                            }
                            catch (Exception ex) { Log("Auto-save failed: " + ex.Message); }
                        }
                    }

                    _grid.SuspendLayout();
                    try
                    {
                        foreach (DetectedApp app in result.Apps)
                        {
                            int index = _grid.Rows.Add(app.Name, app.Category, app.InstallPath);
                            _grid.Rows[index].Tag = app;
                        }
                    }
                    finally
                    {
                        _grid.ResumeLayout();
                    }
                    ResizeGridColumns();
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (IsDisposed || Disposing) return;
                            try { ResizeGridColumns(); }
                            catch { }
                        }));
                    }
                    catch { }

                    Log("Detected " + result.Apps.Count + " applications.");
                    if (result.Apps.Count > 0)
                    {
                        _grid.ClearSelection();
                        _grid.FirstDisplayedScrollingRowIndex = 0;
                        DetectedApp first = _grid.Rows[0].Tag as DetectedApp;
                        if (first != null) ShowDetected(first);

                        Timer scrollTimer = new Timer();
                        scrollTimer.Interval = 700;
                        scrollTimer.Tick += delegate
                        {
                            scrollTimer.Stop();
                            scrollTimer.Dispose();
                            if (IsDisposed || Disposing) return;
                            try
                            {
                                _grid.FirstDisplayedScrollingRowIndex = 0;
                                ResizeGridColumns();
                            }
                            catch { }
                        };
                        scrollTimer.Start();
                    }
                    else
                    {
                        Log("Nothing detected. Use Browse EXE / Browse Folder for a manual app.");
                    }
                }
                catch (Exception ex)
                {
                    try { _busy = false; SetBusy(false); } catch { }
                    try { _lblCount.Text = "Scan failed"; } catch { }
                    Log("Scan UI update failed: " + ex.Message);
                }
            }, ui);
        }

        private void LayoutUi()
        {
            if (_headerPanel == null || _bodyPanel == null || _detailsPanel == null ||
                _optionsPanel == null || _grid == null) return;

            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width < 200 || height < 200) return;

            int headerHeight = 140;
            int optionsHeight = 190;

            _headerPanel.Bounds = new Rectangle(0, 0, width, headerHeight);
            _optionsPanel.Bounds = new Rectangle(12, headerHeight, Math.Max(200, width - 24), optionsHeight);

            int bodyY = headerHeight + optionsHeight;
            int bodyHeight = Math.Max(160, height - bodyY - 12);
            _bodyPanel.Bounds = new Rectangle(12, bodyY, Math.Max(200, width - 24), bodyHeight);

            int bodyWidth = _bodyPanel.ClientSize.Width;
            int gridHeight = Math.Min(220, Math.Max(130, bodyHeight / 3));
            _grid.Bounds = new Rectangle(0, 0, bodyWidth, gridHeight);
            _detailsPanel.Bounds = new Rectangle(0, gridHeight, bodyWidth, Math.Max(100, bodyHeight - gridHeight));

            ResizeGridColumns();
        }

        private void ResizeGridColumns()
        {
            if (_grid == null || _grid.Columns.Count < 3) return;

            int width = _grid.ClientSize.Width - 2;
            if (width < 420) return;

            int name = (int)(width * 0.30);
            int category = (int)(width * 0.16);
            int install = width - name - category;

            if (install < 260)
            {
                install = 260;
                name = width - category - install;
                if (name < 140)
                {
                    name = 140;
                    category = Math.Max(90, width - name - install);
                    install = width - name - category;
                }
            }

            _grid.Columns[0].Width = name;
            _grid.Columns[1].Width = category;
            _grid.Columns[2].Width = install;
        }

        private void SelectionChanged()        {
            if (_grid.SelectedRows.Count == 0) return;
            DetectedApp app = _grid.SelectedRows[0].Tag as DetectedApp;
            if (app != null) ShowDetected(app);
        }

        private void ShowDetected(DetectedApp app)
        {
            _current = app;
            _txtInstall.Text = app.InstallPath;
            _txtExe.Text = app.ExePath;
            _txtData.Text = JoinLines(app.DataPaths);
            _txtReg.Text = JoinLines(app.RegistryKeys);
            _txtServices.Text = JoinLines(app.Services);
            _txtArgs.Text = app.App != null ? app.App.args : "";
            string notes = string.IsNullOrEmpty(app.Notes) ? "Ready." : app.Notes;
            if (!string.IsNullOrEmpty(app.InstallTarget))
            {
                string target = PathUtil.Expand(app.InstallTarget);
                notes += "  •  Links install to: " + target;
            }
            _lblNotes.Text = notes;

            string name = string.IsNullOrEmpty(app.Name) ? "MyApp" : app.Name;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop)) desktop = AppDomain.CurrentDomain.BaseDirectory;
            string suggested = Path.Combine(desktop, PathUtil.SafeName(name) + " Portable");
            if (string.IsNullOrEmpty(_txtOutput.Text) || _txtOutput.Text == _lastAutoOutput)
            {
                _txtOutput.Text = suggested;
                _lastAutoOutput = suggested;
                BeginInvoke(new Action(delegate
                {
                    try
                    {
                        _txtOutput.SelectionStart = 0;
                        _txtOutput.SelectionLength = 0;
                        _txtOutput.ScrollToCaret();
                    }
                    catch { }
                }));
            }

            bool isRobloxApp = app.App != null &&
                               string.Equals(app.App.id, "roblox", StringComparison.OrdinalIgnoreCase);
            bool isGameLoopApp = app.App != null &&
                                 string.Equals(app.App.id, "gameloop", StringComparison.OrdinalIgnoreCase);
            bool isFiveMApp = IsSelectedApp("fivem");
            bool robloxWasEnabled = _chkRobloxSetup.Enabled;
            bool gameLoopWasEnabled = _chkGameLoopSetup.Enabled;
            bool fiveMWasEnabled = _chkFiveMSetup.Enabled;

            _chkRobloxSetup.Enabled = !_busy && isRobloxApp;
            _chkGameLoopSetup.Enabled = !_busy && isGameLoopApp;
            _chkFiveMSetup.Enabled = !_busy && isFiveMApp;
            if (isRobloxApp && !robloxWasEnabled) _chkRobloxSetup.Checked = true;
            if (isGameLoopApp && !gameLoopWasEnabled) _chkGameLoopSetup.Checked = true;
            if (isFiveMApp && !fiveMWasEnabled) _chkFiveMSetup.Checked = true;
            if (!isRobloxApp) _chkRobloxSetup.Checked = false;
            if (!isGameLoopApp) _chkGameLoopSetup.Checked = false;
            if (!isFiveMApp) _chkFiveMSetup.Checked = false;

            _chkBestJunction.Enabled = !_busy && _chkSymlink.Checked && !IsSymlinkForcedApp();
            if (IsSymlinkForcedApp()) _chkBestJunction.Checked = false;

            _btnBuild.Enabled = !_busy;
            _btnSaveDb.Enabled = !_busy;
        }

        private void ClearDetails()
        {
            _txtInstall.Text = "";
            _txtExe.Text = "";
            _txtData.Text = "";
            _txtReg.Text = "";
            _txtServices.Text = "";
            _txtArgs.Text = "";
            UpdateRegistryWarning();
            _lblNotes.Text = "Select an app to begin.";
            _btnBuild.Enabled = false;
            _btnSaveDb.Enabled = false;
            _chkRobloxSetup.Enabled = false;
            _chkRobloxSetup.Checked = false;
            _chkGameLoopSetup.Enabled = false;
            _chkGameLoopSetup.Checked = false;
            _chkFiveMSetup.Enabled = false;
            _chkFiveMSetup.Checked = false;
            _chkBestJunction.Enabled = false;
            _chkBestJunction.Checked = false;
        }

        private void UpdateRegistryWarning()
        {
            _registryWarnings.Clear();
            foreach (string key in SplitLines(_txtReg.Text))
            {
                string note = RegistryUtil.SensitiveRegistryNote(key);
                if (!string.IsNullOrEmpty(note))
                    _registryWarnings.Add(key + "  —  " + note);
            }

            if (_lblRegWarning == null) return;
            if (_registryWarnings.Count == 0)
            {
                _lblRegWarning.Visible = false;
                if (_tips != null) _tips.SetToolTip(_lblRegWarning, "");
                return;
            }

            _lblRegWarning.Text = "⚠  مفاتيح قد تحفظ بيانات حساب/إيميل — اضغط للتفاصيل";
            _lblRegWarning.Visible = true;

            StringBuilder tip = new StringBuilder();
            tip.AppendLine("⚠ راجع مفاتيح Registry دي قبل مشاركة الباكدج:");
            int shown = 0;
            foreach (string warning in _registryWarnings)
            {
                if (shown++ >= 8) { tip.AppendLine("... وغيرها"); break; }
                tip.AppendLine("• " + warning);
            }
            if (_tips != null) _tips.SetToolTip(_lblRegWarning, tip.ToString().Trim());
        }

        private void ShowRegistryWarnings()
        {
            if (_registryWarnings.Count == 0) return;
            StringBuilder text = new StringBuilder();
            text.AppendLine("المفاتيح دي قد تحتوي على بيانات شخصية (اسم مستخدم، إيميل، جلسة، ترخيص، سجل استخدام...).");
            text.AppendLine("لو هتشارك الباكدج، راجعها أو شيلها من خانة Registry keys.");
            text.AppendLine();
            int shown = 0;
            foreach (string warning in _registryWarnings)
            {
                if (shown++ >= 20) { text.AppendLine("..."); break; }
                text.AppendLine("• " + warning);
            }
            MessageBox.Show(this, text.ToString(), "مراجعة بيانات Registry",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private bool IsSelectedApp(string id)
        {
            if (_current == null) return false;
            if (_current.App != null &&
                string.Equals(_current.App.id, id, StringComparison.OrdinalIgnoreCase))
                return true;

            // Manual add / undetected folder: recognize FiveM by its only path.
            if (string.Equals(id, "fivem", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(_current.InstallPath))
            {
                string folder = Path.GetFileName(_current.InstallPath.TrimEnd('\\'));
                return string.Equals(folder, "FiveM", StringComparison.OrdinalIgnoreCase) &&
                       File.Exists(Path.Combine(_current.InstallPath, "FiveM.exe"));
            }
            return false;
        }

        private bool IsSymlinkForcedApp()
        {
            return IsSelectedApp("roblox") || IsSelectedApp("fivem") || IsSelectedApp("gameloop");
        }

        private void BrowseExe()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the main executable";
                dialog.Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string name = null;
                try { name = FileVersionInfo.GetVersionInfo(dialog.FileName).FileDescription; }
                catch { }
                if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(dialog.FileName);
                CreateManual(name, Path.GetDirectoryName(dialog.FileName), dialog.FileName);
            }
        }

        private void BrowseFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the application folder (or the folder containing the EXE)";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string path = dialog.SelectedPath;
                string name = Path.GetFileName(path.TrimEnd('\\'));
                if (string.IsNullOrEmpty(name)) name = "MyApp";

                KnownApp known = FindKnown(_db, name);
                string exe = known != null
                    ? AppDetector.FindExe(path, known.exe, null)
                    : AppDetector.FindExe(path, "", null);
                CreateManual(name, path, exe);
            }
        }

        private void CreateManual(string name, string path, string exe)
        {
            try
            {
                DetectedApp app;
                KnownApp known = FindKnown(_db, name);
                if (known != null)
                {
                    known.Prepare();
                    string install = Directory.Exists(path) ? path : (Path.GetDirectoryName(exe) ?? path);
                    app = AppDetector.Enrich(known, install, exe, _index);
                    app.Source = "manual";
                }
                else
                {
                    app = AppDetector.Manual(name, path, exe, _index);
                }

                _detected.Insert(0, app);
                _grid.Rows.Insert(0, app.Name, app.Category, app.InstallPath);
                _grid.Rows[0].Tag = app;
                _grid.ClearSelection();
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
                ShowDetected(app);
                try { _grid.FirstDisplayedScrollingRowIndex = 0; } catch { }

                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    Log("No EXE detected. Set the Main EXE field manually before building.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not inspect this folder:\r\n" + ex.Message,
                    "PortableBuilder V1.1", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BrowseOutput()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the output folder";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _txtOutput.Text = dialog.SelectedPath;
                    _lastAutoOutput = null;
                }
            }
        }

        private void ManualSearch()
        {
            if (_busy) return;

            AppDatabase db = _db;
            if (db == null)
            {
                try { db = AppDatabase.Load(); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not load the application database:\r\n" + ex.Message,
                        "PortableBuilder V1.1", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // The full system index may still be building. An empty index still
            // searches the application database and its install paths, so the
            // dialog stays usable from the first second.
            SystemIndex index = _index ?? new SystemIndex();

            using (ManualSearchForm form = new ManualSearchForm(db, index))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;

                DetectedApp app = form.Result;
                if (app == null) return;

                _detected.Insert(0, app);
                _grid.Rows.Insert(0, app.Name, app.Category, app.InstallPath);
                _grid.Rows[0].Tag = app;
                _grid.ClearSelection();
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
                ShowDetected(app);
                try { _grid.FirstDisplayedScrollingRowIndex = 0; } catch { }

                if (string.IsNullOrEmpty(app.ExePath) || !File.Exists(app.ExePath))
                    Log("No EXE detected. Set the Main EXE field manually before building.");
                Log("Manual search added: " + app.Name + " (" + app.InstallPath + ")");
            }
        }

        private void SaveToDb()
        {
            if (_busy || _current == null || _db == null) return;

            string category = _current.App != null && !string.IsNullOrEmpty(_current.App.category)
                ? _current.App.category : "Utility";

            using (SaveDbForm form = new SaveDbForm(_db, _current.Name, category))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                category = form.Category;
            }

            try
            {
                KnownApp saved = _db.UpsertFromDetected(_current, category);
                string path = AppDatabase.ExternalPath();
                Log("Saved to database: " + saved.name + " (" + path + ")");
                _lblNotes.Text = "Saved '" + saved.name + "' to the database (" + saved.id + ").";
                MessageBox.Show(this,
                    "Saved '" + saved.name + "' to the database.\r\n\r\n" + path +
                    "\r\n\r\nIt will be auto-detected on the next scan.",
                    "PortableBuilder V1.1", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save to the database:\r\n" + ex.Message,
                    "PortableBuilder V1.1", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BuildPackage()
        {
            if (_busy) return;

            string install = _txtInstall.Text.Trim();
            string exe = _txtExe.Text.Trim();
            string output = _txtOutput.Text.Trim();

            bool isGameLoopApp = _current != null && _current.App != null &&
                                 string.Equals(_current.App.id, "gameloop", StringComparison.OrdinalIgnoreCase);
            bool isRobloxApp = _current != null && _current.App != null &&
                               string.Equals(_current.App.id, "roblox", StringComparison.OrdinalIgnoreCase);
            bool isFiveMApp = IsSelectedApp("fivem");
            bool gameLoopSetup = isGameLoopApp && _chkGameLoopSetup.Checked;
            bool robloxSetup = isRobloxApp && _chkRobloxSetup.Checked;
            bool fiveMSetup = isFiveMApp && _chkFiveMSetup.Checked;

            if (gameLoopSetup)
            {
                using (GameLoopSandboxForm form = new GameLoopSandboxForm(install, output))
                {
                    if (form.ShowDialog(this) != DialogResult.OK) return;
                    install = form.SourceFolder;
                    output = form.TargetFolder;
                    _txtInstall.Text = install;
                    _txtOutput.Text = output;

                    string uiExe = Path.Combine(install, "UI", "AndroidEmulatorEn.exe");
                    if (File.Exists(uiExe))
                    {
                        exe = uiExe;
                        _txtExe.Text = exe;
                    }
                }
            }

            if (install.Length == 0 || !Directory.Exists(install))
            {
                MessageBox.Show(this, "Install folder not found:\r\n" + install, "PortableBuilder V1.1",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!robloxSetup && !gameLoopSetup && !fiveMSetup && (exe.Length == 0 || !File.Exists(exe)))
            {
                MessageBox.Show(this, "Main EXE not found:\r\n" + exe, "PortableBuilder V1.1",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (output.Length == 0)
            {
                MessageBox.Show(this, "Choose an output folder.", "PortableBuilder V1.1",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!gameLoopSetup && !robloxSetup && !fiveMSetup && Directory.Exists(output))
            {
                DialogResult answer = MessageBox.Show(this,
                    "The output folder already exists:\r\n" + output + "\r\n\r\n" +
                    "Rebuild into it? (Package files are replaced; other files are kept.)",
                    "PortableBuilder V1.1", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }

            BuildRequest request = new BuildRequest();
            request.AppName = _current != null && !string.IsNullOrEmpty(_current.Name)
                ? _current.Name : Path.GetFileName(install.TrimEnd('\\'));
            request.AppId = _current != null && _current.App != null ? _current.App.id : "";
            if (fiveMSetup) request.AppId = "fivem";
            request.InstallTarget = _current != null && _current.App != null ? _current.App.installTarget : "";
            request.InstallPath = install;
            request.ExePath = exe;
            request.OutputDir = output;
            request.Args = _txtArgs.Text.Trim();
            request.CopyMode = _cmbMode.SelectedIndex == 1;
            request.ForceSymlink = _chkSymlink.Checked;
            request.BestLayout = _chkSymlink.Checked;
            if (request.BestLayout) request.CopyMode = false;
            request.IncludeRegistry = _chkRegistry.Checked;
            request.CaptureUpdates = _chkCapture.Checked;
            request.CleanPaths = _chkCleanPaths.Checked;
            request.WaitForExit = _chkWait.Checked;
            request.RestoreOnExit = _chkRestore.Checked;
            request.EnableRobloxSetup = robloxSetup;
            request.EnableGameLoopSetup = gameLoopSetup;
            request.EnableFiveMSetup = fiveMSetup;
            request.BestJunction = _chkBestJunction.Checked && !robloxSetup && !gameLoopSetup && !fiveMSetup;
            request.DataPaths = SplitLines(_txtData.Text);
            request.RegistryKeys = SplitLines(_txtReg.Text);
            request.Services = SplitLines(_txtServices.Text);
            if (_current != null && _current.App != null)
                request.ExcludePaths = _current.App.excludes;
            if (_current != null && _current.App != null)
                request.RegistryReplace = _current.App.registryReplace;

            _busy = true;
            SetBusy(true);
            Log("========================================");
            Log("Building: " + request.AppName);
            Log("Install: " + request.InstallPath);
            Log("Output:  " + request.OutputDir);

            TaskScheduler ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task<BuildResult> task;
            try
            {
                task = Task.Factory.StartNew<BuildResult>(delegate
                {
                    return TargetedBuilder.Build(request, Log);
                });
            }
            catch (Exception ex)
            {
                _busy = false;
                SetBusy(false);
                Log("Could not start the build: " + ex.Message);
                MessageBox.Show(this, "Build failed to start:\r\n" + ex.Message, "PortableBuilder V1.1",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            task.ContinueWith(delegate(Task<BuildResult> t)
            {
                if (IsDisposed || Disposing) return;
                try
                {
                    _busy = false;
                    SetBusy(false);

                    if (t.IsFaulted)
                    {
                        Exception ex = t.Exception == null
                            ? new InvalidOperationException("Unknown build error.")
                            : t.Exception.GetBaseException();
                        Log("Build failed: " + ex.Message);
                        MessageBox.Show(this, "Build failed:\r\n" + ex.Message, "PortableBuilder V1.1",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    if (t.IsCanceled)
                    {
                        Log("Build cancelled.");
                        return;
                    }

                    BuildResult result = t.Result;
                    if (!result.Success)
                    {
                        Log("Build failed: " + result.Error);
                        MessageBox.Show(this, "Build failed:\r\n" + result.Error, "PortableBuilder V1.1",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    _lastOutput = result.OutputDir;
                    _btnOpen.Enabled = true;
                    _lblNotes.Text = "Done. Open the output folder and run the app.";
                    Log("Done. Files: " + result.FilesCopied +
                        ", registry files: " + result.RegistryFiles +
                        ", services: " + result.Services);

                    MessageBox.Show(this,
                        "Portable package created:\r\n\r\n" + result.OutputDir +
                        "\r\n\r\nOpen the folder and run the app's .exe.",
                        "PortableBuilder V1.1", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    if (Directory.Exists(result.OutputDir))
                    {
                        try
                        {
                            Process.Start(Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                                result.OutputDir);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { _busy = false; SetBusy(false); } catch { }
                    Log("Build UI update failed: " + ex.Message);
                }
            }, ui);
        }

        private void OpenOutput()
        {
            if (string.IsNullOrEmpty(_lastOutput) || !Directory.Exists(_lastOutput)) return;
            try
            {
                Process.Start(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    _lastOutput);
            }
            catch { }
        }

        private void SetBusy(bool busy)
        {
            _btnRefresh.Enabled = !busy;
            _btnBrowseExe.Enabled = !busy;
            _btnBrowseFolder.Enabled = !busy;
            _btnManualSearch.Enabled = !busy;
            _btnOut.Enabled = !busy;
            _cmbMode.Enabled = !busy && !_chkSymlink.Checked;
            _chkRegistry.Enabled = !busy;
            _chkWait.Enabled = !busy;
            _chkRestore.Enabled = !busy;
            _chkSymlink.Enabled = !busy;
            _chkDeep.Enabled = !busy;
            _chkCapture.Enabled = !busy;
            _chkAutoSave.Enabled = !busy;
            _chkCleanPaths.Enabled = !busy;
            _chkRobloxSetup.Enabled = !busy && IsSelectedApp("roblox");
            _chkGameLoopSetup.Enabled = !busy && IsSelectedApp("gameloop");
            _chkFiveMSetup.Enabled = !busy && IsSelectedApp("fivem");
            _chkBestJunction.Enabled = !busy && _chkSymlink.Checked && !IsSymlinkForcedApp();
            _btnBuild.Enabled = !busy && _current != null;
            _btnSaveDb.Enabled = !busy && _current != null;
            _btnOpen.Enabled = !busy && !string.IsNullOrEmpty(_lastOutput) && Directory.Exists(_lastOutput);

            // UseWaitCursor alone can leave the DataGridView showing the wait
            // cursor after a scan; reset the grid cursor explicitly.
            if (_grid != null) _grid.Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            UseWaitCursor = busy;
            Cursor = Cursors.Default; // buttons/grid keep their own cursors
            if (busy) _lblCount.Text = "Working...";
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            if (!string.IsNullOrEmpty(_logPath))
            {
                lock (_logLock)
                {
                    try { File.AppendAllText(_logPath, line + Environment.NewLine); }
                    catch { }
                }
            }

            if (IsHandleCreated)
            {
                try
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                if (!IsDisposed && !Disposing && _lblNotes != null)
                                    _lblNotes.Text = message;
                            }
                            catch { }
                        }));
                    }
                    else if (_lblNotes != null)
                    {
                        _lblNotes.Text = message;
                    }
                }
                catch { }
            }
        }

        private void OpenLog()
        {
            try
            {
                if (File.Exists(_logPath))
                    Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + _logPath + "\"");
                else MessageBox.Show(this, "No log yet.", "PortableBuilder V1.1",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private static string JoinLines(List<string> values)
        {
            if (values == null || values.Count == 0) return "";
            return string.Join("\r\n", values.ToArray());
        }

        private static List<string> SplitLines(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string value = line.Trim();
                if (value.Length > 0) result.Add(value);
            }
            return result;
        }

        internal static KnownApp FindKnown(AppDatabase db, string name)
        {
            if (db == null || db.apps == null) return null;

            string wanted = PathUtil.Normalize(name);
            KnownApp partial = null;
            foreach (KnownApp app in db.apps)
            {
                if (app == null) continue;
                if (string.Equals(app.NormName, wanted, StringComparison.OrdinalIgnoreCase)) return app;
                foreach (string alias in app.NormAliases)
                {
                    if (string.Equals(alias, wanted, StringComparison.OrdinalIgnoreCase)) return app;
                }
                if (partial == null && app.NormName.Length >= 4 && wanted.Length >= 4 &&
                    (app.NormName.Contains(wanted) || wanted.Contains(app.NormName)))
                    partial = app;
            }
            return partial;
        }
    }

    internal sealed class ManualSearchForm : Form
    {
        private readonly AppDatabase _db;
        private readonly SystemIndex _index;

        private TextBox _txtName;
        private ComboBox _cmbKnown;
        private DataGridView _grid;
        private TextBox _txtInstall;
        private TextBox _txtExe;
        private TextBox _txtData;
        private TextBox _txtReg;
        private TextBox _txtServices;
        private Label _lblStatus;
        private Label _lblRegPrivacy;
        private ToolTip _regTip;
        private readonly List<string> _regPrivacy = new List<string>();

        private SearchResult _last = new SearchResult();

        public DetectedApp Result { get; private set; }

        public ManualSearchForm(AppDatabase db, SystemIndex index)
        {
            _db = db;
            _index = index;

            Text = "Manual search";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            int dialogHeight = 740;
            try
            {
                int working = Screen.PrimaryScreen == null ? 0 : Screen.PrimaryScreen.WorkingArea.Height;
                if (working > 0) dialogHeight = Math.Min(dialogHeight, Math.Max(520, working - 80));
            }
            catch { }
            ClientSize = new Size(760, dialogHeight);
            AutoScroll = true;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(247, 246, 252);
            Result = null;

            BuildUi();
        }

        private void BuildUi()
        {
            // ---- known apps picker ----
            Label knownLabel = MakeLabel("From list:", new Point(12, 16), 100);
            Controls.Add(knownLabel);

            _cmbKnown = new ComboBox();
            _cmbKnown.Bounds = new Rectangle(112, 13, 500, 26);
            _cmbKnown.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbKnown.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cmbKnown.Items.Add("(type a name below)");
            List<string> knownNames = new List<string>();
            if (_db != null && _db.apps != null)
                foreach (KnownApp app in _db.apps)
                    if (app != null && !string.IsNullOrEmpty(app.name)) knownNames.Add(app.name);
            knownNames.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string n in knownNames) _cmbKnown.Items.Add(n);
            _cmbKnown.SelectedIndex = 0;
            _cmbKnown.SelectedIndexChanged += delegate
            {
                if (_cmbKnown.SelectedIndex <= 0) return;
                string selected = _cmbKnown.SelectedItem as string;
                if (string.IsNullOrEmpty(selected)) return;
                _txtName.Text = selected;
                DoSearch();
            };
            Controls.Add(_cmbKnown);

            // ---- name row ----
            Label nameLabel = MakeLabel("App name:", new Point(12, 50), 100);
            Controls.Add(nameLabel);

            _txtName = new TextBox();
            _txtName.Bounds = new Rectangle(112, 47, 500, 26);
            _txtName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_txtName);

            Button searchBtn = MakeButton("Search", new Rectangle(620, 46, 128, 28));
            searchBtn.Click += delegate { DoSearch(); };
            Controls.Add(searchBtn);
            AcceptButton = searchBtn;

            Label matchesLabel = MakeLabel("Matches — select a row to load its paths:", new Point(12, 84), 320);
            Controls.Add(matchesLabel);

            // ---- results grid ----
            _grid = new DataGridView();
            _grid.Bounds = new Rectangle(12, 108, 736, 180);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.ColumnHeadersHeight = 28;
            _grid.RowTemplate.Height = 25;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 241, 244);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(40, 40, 50);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(47, 111, 186);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.Columns.Add("Name", "Application");
            _grid.Columns.Add("Install", "Install path");
            _grid.Columns.Add("Match", "Match");
            _grid.Columns[0].FillWeight = 38;
            _grid.Columns[1].FillWeight = 50;
            _grid.Columns[2].FillWeight = 12;
            _grid.Columns[0].MinimumWidth = 180;
            _grid.Columns[1].MinimumWidth = 260;
            _grid.Columns[2].MinimumWidth = 70;
            _grid.SelectionChanged += delegate { ApplySelection(); };
            Controls.Add(_grid);

            // ---- paths group ----
            GroupBox paths = new GroupBox();
            paths.Text = "Paths (edit manually if the search missed anything)";
            paths.Bounds = new Rectangle(12, 294, 736, 380);
            paths.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            paths.BackColor = Color.White;
            paths.ForeColor = Color.FromArgb(55, 45, 80);
            Controls.Add(paths);

            Label installLabel = MakeLabel("Install folder:", new Point(16, 30), 110);
            paths.Controls.Add(installLabel);
            _txtInstall = MakeBox(paths, new Rectangle(130, 27, 500, 24), false);
            Button installBrowse = MakeButton("Browse...", new Rectangle(638, 26, 84, 26));
            installBrowse.Click += delegate { BrowseInstall(); };
            paths.Controls.Add(installBrowse);

            Label exeLabel = MakeLabel("Main EXE:", new Point(16, 66), 110);
            paths.Controls.Add(exeLabel);
            _txtExe = MakeBox(paths, new Rectangle(130, 63, 500, 24), false);
            Button exeBrowse = MakeButton("Browse...", new Rectangle(638, 62, 84, 26));
            exeBrowse.Click += delegate { BrowseExe(); };
            paths.Controls.Add(exeBrowse);

            Label dataLabel = MakeLabel("Data folders:", new Point(16, 102), 110);
            paths.Controls.Add(dataLabel);
            _txtData = MakeBox(paths, new Rectangle(130, 100, 592, 76), true);

            Label regLabel = MakeLabel("Registry keys:", new Point(16, 186), 110);
            paths.Controls.Add(regLabel);
            _txtReg = MakeBox(paths, new Rectangle(130, 184, 592, 62), true);

            _lblRegPrivacy = new Label();
            _lblRegPrivacy.Bounds = new Rectangle(130, 248, 592, 18);
            _lblRegPrivacy.BackColor = Color.FromArgb(255, 246, 205);
            _lblRegPrivacy.ForeColor = Color.FromArgb(145, 95, 0);
            _lblRegPrivacy.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            _lblRegPrivacy.TextAlign = ContentAlignment.MiddleLeft;
            _lblRegPrivacy.RightToLeft = RightToLeft.Yes;
            _lblRegPrivacy.Cursor = Cursors.Hand;
            _lblRegPrivacy.Visible = false;
            _lblRegPrivacy.Click += delegate { ShowRegPrivacy(); };
            paths.Controls.Add(_lblRegPrivacy);

            _regTip = new ToolTip();
            _regTip.AutoPopDelay = 20000;
            _regTip.InitialDelay = 300;
            _regTip.ReshowDelay = 100;
            FormClosed += delegate { try { _regTip.Dispose(); } catch { } };
            _txtReg.TextChanged += delegate { UpdateRegPrivacy(); };

            Label servicesLabel = MakeLabel("Services:", new Point(16, 270), 110);
            paths.Controls.Add(servicesLabel);
            _txtServices = MakeBox(paths, new Rectangle(130, 268, 592, 48), true);

            _lblStatus = new Label();
            _lblStatus.Bounds = new Rectangle(130, 328, 592, 40);
            _lblStatus.ForeColor = Color.FromArgb(95, 85, 120);
            _lblStatus.Text = "Type an app name and press Search.";
            paths.Controls.Add(_lblStatus);

            // ---- buttons ----
            Button addBtn = MakeButton("Add", new Rectangle(560, 694, 90, 32));
            addBtn.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            addBtn.FlatAppearance.BorderSize = 0;
            addBtn.FlatStyle = FlatStyle.Flat;
            addBtn.BackColor = Color.FromArgb(47, 111, 186);
            addBtn.ForeColor = Color.White;
            addBtn.Cursor = Cursors.Hand;
            addBtn.UseVisualStyleBackColor = false;
            addBtn.Click += delegate { Add(); };
            Controls.Add(addBtn);

            Button cancelBtn = MakeButton("Cancel", new Rectangle(658, 694, 90, 32));
            cancelBtn.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancelBtn);
            CancelButton = cancelBtn;

            Shown += delegate { _txtName.Focus(); };
        }

        private Label MakeLabel(string text, Point location, int width)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = location;
            label.Size = new Size(width, 22);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(50, 50, 60);
            return label;
        }

        private Button MakeButton(string text, Rectangle bounds)
        {
            Button button = new Button();
            button.Text = text;
            button.Bounds = bounds;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(185, 205, 230);
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(47, 111, 186);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private TextBox MakeBox(Control parent, Rectangle bounds, bool multiline)
        {
            TextBox box = new TextBox();
            box.Bounds = bounds;
            box.Multiline = multiline;
            box.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
            box.WordWrap = false;
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            parent.Controls.Add(box);
            return box;
        }

        private void DoSearch()
        {
            string name = _txtName.Text.Trim();
            if (name.Length == 0)
            {
                _lblStatus.Text = "Enter an app name first.";
                return;
            }

            Cursor = Cursors.WaitCursor;
            try
            {
                _last = AppDetector.SearchByName(name, _db, _index);
                PopulateGrid();
                ApplyFirstResult();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Search failed: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void PopulateGrid()
        {
            _grid.Rows.Clear();

            foreach (DetectedApp app in _last.Candidates)
            {
                int idx = _grid.Rows.Add(app.Name, app.InstallPath, MatchLabel(app.MatchScore));
                _grid.Rows[idx].Tag = app;
            }
            foreach (string hint in _last.InstallHints)
            {
                int idx = _grid.Rows.Add("(folder) " + Path.GetFileName(hint.TrimEnd('\\')), hint, "Folder");
                _grid.Rows[idx].Tag = hint;
            }
        }

        private static string MatchLabel(int score)
        {
            if (score >= 900) return "Exact";
            if (score >= 700) return "Strong";
            if (score >= 500) return "Good";
            if (score >= 300) return "Related";
            return "Weak";
        }

        private void ApplyFirstResult()
        {
            if (_last.Candidates.Count > 0)
            {
                ApplyCandidate(_last.Candidates[0]);
            }
            else
            {
                _txtInstall.Text = "";
                _txtExe.Text = "";
                _txtData.Text = JoinLines(_last.DataFolders);
                _txtReg.Text = JoinLines(_last.RegistryKeys);
                _txtServices.Text = JoinLines(_last.Services);
            }
        }

        private void ApplySelection()
        {
            if (_grid.SelectedRows.Count == 0) return;
            object tag = _grid.SelectedRows[0].Tag;
            if (tag is DetectedApp)
            {
                ApplyCandidate((DetectedApp)tag);
            }
            else if (tag is string)
            {
                string hint = (string)tag;
                _txtInstall.Text = hint;
                string exe = AppDetector.FindExe(hint, "", null);
                _txtExe.Text = string.IsNullOrEmpty(exe) ? "" : exe;
                // keep the partial data/registry/services from the search
                if (string.IsNullOrEmpty(_txtData.Text)) _txtData.Text = JoinLines(_last.DataFolders);
                if (string.IsNullOrEmpty(_txtReg.Text)) _txtReg.Text = JoinLines(_last.RegistryKeys);
                if (string.IsNullOrEmpty(_txtServices.Text)) _txtServices.Text = JoinLines(_last.Services);
            }
        }

        private void ApplyCandidate(DetectedApp app)
        {
            _txtInstall.Text = app.InstallPath;
            _txtExe.Text = app.ExePath;
            _txtData.Text = JoinLines(app.DataPaths);
            _txtReg.Text = JoinLines(app.RegistryKeys);
            _txtServices.Text = JoinLines(app.Services);
        }

        private void UpdateStatus()
        {
            int c = _last.Candidates.Count;
            int h = _last.InstallHints.Count;
            int d = _last.DataFolders.Count;
            int r = _last.RegistryKeys.Count;
            int s = _last.Services.Count;

            if (c == 0 && h == 0 && d == 0 && r == 0 && s == 0)
                _lblStatus.Text = "Nothing found. Fill the paths below manually, then press Add.";
            else
                _lblStatus.Text = string.Format("Found {0} app(s), {1} folder(s), {2} data folder(s), {3} registry key(s), {4} service(s).",
                    c, h, d, r, s);
        }

        private void UpdateRegPrivacy()
        {
            _regPrivacy.Clear();
            if (_lblRegPrivacy == null) return;
            foreach (string key in SplitLines(_txtReg.Text))
            {
                string note = RegistryUtil.SensitiveRegistryNote(key);
                if (!string.IsNullOrEmpty(note))
                    _regPrivacy.Add(key + "  —  " + note);
            }
            if (_regPrivacy.Count == 0)
            {
                _lblRegPrivacy.Visible = false;
                if (_regTip != null) _regTip.SetToolTip(_lblRegPrivacy, "");
                return;
            }

            _lblRegPrivacy.Text = "⚠  " + _regPrivacy.Count + " مفتاح Registry قد يحفظ بيانات حساب/إيميل — اضغط للتفاصيل";
            _lblRegPrivacy.Visible = true;

            StringBuilder tip = new StringBuilder();
            tip.AppendLine("⚠ بيانات شخصية محتملة:");
            int shown = 0;
            foreach (string warning in _regPrivacy)
            {
                if (shown++ >= 6) { tip.AppendLine("..."); break; }
                tip.AppendLine("• " + warning);
            }
            if (_regTip != null) _regTip.SetToolTip(_lblRegPrivacy, tip.ToString().Trim());
        }

        private void ShowRegPrivacy()
        {
            if (_regPrivacy.Count == 0) return;
            StringBuilder text = new StringBuilder();
            text.AppendLine("المفاتيح دي قد تحتوي على بيانات شخصية (اسم مستخدم، إيميل، جلسة، ترخيص...).");
            text.AppendLine("راجعها قبل مشاركة الباكدج.");
            text.AppendLine();
            int shown = 0;
            foreach (string warning in _regPrivacy)
            {
                if (shown++ >= 20) { text.AppendLine("..."); break; }
                text.AppendLine("• " + warning);
            }
            MessageBox.Show(this, text.ToString(), "مراجعة بيانات Registry",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void BrowseInstall()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the install folder";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _txtInstall.Text = dialog.SelectedPath;
            }
        }

        private void BrowseExe()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the main executable";
                dialog.Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _txtExe.Text = dialog.FileName;
            }
        }

        private void Add()
        {
            string name = _txtName.Text.Trim();
            if (name.Length == 0) name = "MyApp";

            string install = _txtInstall.Text.Trim();
            string exe = _txtExe.Text.Trim();

            if (install.Length == 0 && exe.Length > 0)
            {
                try { install = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ""; } catch { }
            }
            if (install.Length == 0)
            {
                MessageBox.Show(this,
                    "Choose an install folder (or a main EXE) before adding the app.",
                    "PortableBuilder V1.1", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (install.Length > 0 && !Directory.Exists(install))
            {
                DialogResult answer = MessageBox.Show(this,
                    "Install folder not found:\r\n" + install + "\r\n\r\nAdd anyway?",
                    "PortableBuilder V1.1", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }

            Result = BuildResult(name, install, exe);
            DialogResult = DialogResult.OK;
        }

        private DetectedApp BuildResult(string name, string install, string exe)
        {
            KnownApp known = BuilderForm.FindKnown(_db, name);
            KnownApp app;
            if (known != null)
            {
                app = known;
                app.Prepare();
            }
            else
            {
                app = new KnownApp();
                app.id = "manual";
                app.name = name;
                app.category = "Manual";
                app.aliases = new List<string>();
                app.tokens = PathUtil.WordTokens(name);
                app.installPaths = new List<string>();
                app.exe = "";
                app.dataPaths = new List<string>();
                app.registryKeys = new List<string>();
                app.processNames = new List<string>();
                app.excludes = new List<string>();
                app.registryReplace = new List<string[]>();
                app.installTarget = "";
                app.args = "";
                app.notes = "";
                app.Prepare();
            }

            DetectedApp d = new DetectedApp();
            d.App = app;
            d.InstallPath = install;
            d.ExePath = exe;
            d.DisplayName = name;
            d.Notes = app.notes;
            d.Source = "manual";
            d.DataPaths = SplitLines(_txtData.Text);
            d.RegistryKeys = SplitLines(_txtReg.Text);
            d.Services = SplitLines(_txtServices.Text);
            d.ExcludePaths = new List<string>(app.excludes);
            d.RegistryReplace = new List<string[]>(app.registryReplace);
            d.InstallTarget = app.installTarget;
            return d;
        }

        private static string JoinLines(List<string> values)
        {
            if (values == null || values.Count == 0) return "";
            return string.Join("\r\n", values.ToArray());
        }

        private static List<string> SplitLines(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string value = line.Trim();
                if (value.Length > 0) result.Add(value);
            }
            return result;
        }
    }

    internal sealed class SaveDbForm : Form
    {
        private readonly ComboBox _cmbCategory;

        public string Category { get { return _cmbCategory.Text.Trim(); } }

        public SaveDbForm(AppDatabase db, string appName, string defaultCategory)
        {
            Text = "Save to database";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 150);
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(247, 246, 252);

            Label nameLabel = new Label();
            nameLabel.Text = "App:";
            nameLabel.Bounds = new Rectangle(16, 22, 90, 22);
            nameLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(nameLabel);

            Label nameValue = new Label();
            nameValue.Text = appName;
            nameValue.Bounds = new Rectangle(110, 22, 290, 22);
            nameValue.TextAlign = ContentAlignment.MiddleLeft;
            nameValue.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            Controls.Add(nameValue);

            Label catLabel = new Label();
            catLabel.Text = "Category:";
            catLabel.Bounds = new Rectangle(16, 58, 90, 24);
            catLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(catLabel);

            _cmbCategory = new ComboBox();
            _cmbCategory.Bounds = new Rectangle(110, 56, 290, 26);
            _cmbCategory.DropDownStyle = ComboBoxStyle.DropDown;
            HashSet<string> categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (db != null && db.apps != null)
                foreach (KnownApp a in db.apps)
                    if (a != null && !string.IsNullOrEmpty(a.category)) categories.Add(a.category);
            List<string> sorted = new List<string>(categories);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string c in sorted) _cmbCategory.Items.Add(c);
            _cmbCategory.Text = string.IsNullOrEmpty(defaultCategory) ? "Utility" : defaultCategory;
            Controls.Add(_cmbCategory);

            Button saveBtn = new Button();
            saveBtn.Text = "Save";
            saveBtn.Bounds = new Rectangle(230, 106, 86, 30);
            saveBtn.FlatStyle = FlatStyle.Flat;
            saveBtn.FlatAppearance.BorderSize = 0;
            saveBtn.BackColor = Color.FromArgb(47, 111, 186);
            saveBtn.ForeColor = Color.White;
            saveBtn.Cursor = Cursors.Hand;
            saveBtn.UseVisualStyleBackColor = false;
            saveBtn.Click += delegate
            {
                if (Category.Length == 0) _cmbCategory.Text = "Utility";
                DialogResult = DialogResult.OK;
            };
            Controls.Add(saveBtn);
            AcceptButton = saveBtn;

            Button cancelBtn = new Button();
            cancelBtn.Text = "Cancel";
            cancelBtn.Bounds = new Rectangle(322, 106, 86, 30);
            cancelBtn.FlatStyle = FlatStyle.Flat;
            cancelBtn.FlatAppearance.BorderSize = 1;
            cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(185, 205, 230);
            cancelBtn.BackColor = Color.White;
            cancelBtn.ForeColor = Color.FromArgb(47, 111, 186);
            cancelBtn.Cursor = Cursors.Hand;
            cancelBtn.UseVisualStyleBackColor = false;
            cancelBtn.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancelBtn);
            CancelButton = cancelBtn;

            Shown += delegate { _cmbCategory.Focus(); _cmbCategory.SelectAll(); };
        }
    }

    // ------------------------------------------------------------------
    //  GameLoop build sandbox: pick the source installation and the
    //  portable folder before building.
    // ------------------------------------------------------------------
    internal sealed class GameLoopSandboxForm : Form
    {
        private readonly TextBox _txtSource;
        private readonly TextBox _txtTarget;
        private readonly Label _lblStatus;
        private readonly List<RadioButton> _candidates = new List<RadioButton>();

        public string SourceFolder { get; private set; }
        public string TargetFolder { get; private set; }

        public GameLoopSandboxForm(string install, string output)
        {
            Text = "GameLoop Sandbox";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(700, 470);
            Font = new Font("Segoe UI", 9f);

            Label title = new Label();
            title.Text = "Choose the GameLoop source installation and the portable folder.";
            title.SetBounds(16, 12, 660, 20);
            Controls.Add(title);

            GroupBox sourceGroup = new GroupBox();
            sourceGroup.Text = "Source install (must contain UI\\AndroidEmulatorEn.exe)";
            sourceGroup.SetBounds(14, 40, 672, 210);
            Controls.Add(sourceGroup);

            List<string> seen = new List<string>();
            List<string> candidates = new List<string>();
            AddCandidate(candidates, seen, install);
            AddCandidate(candidates, seen, @"D:\Program Files\TxGameAssistant");
            AddCandidate(candidates, seen, @"D:\TxGameAssistant");
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
                AddCandidate(candidates, seen, Path.Combine(pf, "TxGameAssistant"));
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86))
                AddCandidate(candidates, seen, Path.Combine(pf86, "TxGameAssistant"));

            int y = 22;
            bool selected = false;
            foreach (string candidate in candidates)
            {
                RadioButton radio = new RadioButton();
                bool valid = IsGameLoop(candidate);
                radio.Text = candidate + (valid ? "" : "   (no UI\\AndroidEmulatorEn.exe)");
                radio.Enabled = valid;
                radio.Tag = candidate;
                radio.SetBounds(14, y, 640, 20);
                radio.CheckedChanged += delegate
                {
                    RadioButton rb = (RadioButton)radio;
                    if (rb.Checked && _txtSource != null)
                    {
                        _txtSource.Text = Convert.ToString(rb.Tag);
                        UpdateStatus();
                    }
                };
                sourceGroup.Controls.Add(radio);
                _candidates.Add(radio);
                y += 22;
                if (valid && !selected && SamePath(candidate, ResolveRoot(install)))
                {
                    radio.Checked = true;
                    selected = true;
                }
            }
            if (!selected)
            {
                foreach (RadioButton radio in _candidates)
                {
                    if (radio.Enabled) { radio.Checked = true; break; }
                }
            }

            Label sourceLabel = new Label();
            sourceLabel.Text = "Folder:";
            sourceLabel.SetBounds(14, 150, 50, 20);
            sourceGroup.Controls.Add(sourceLabel);

            _txtSource = new TextBox();
            _txtSource.SetBounds(66, 148, 470, 24);
            _txtSource.Text = builtSource(install, candidates);
            _txtSource.TextChanged += delegate { UpdateStatus(); };
            sourceGroup.Controls.Add(_txtSource);

            Button browseSource = new Button();
            browseSource.Text = "Browse...";
            browseSource.SetBounds(546, 147, 100, 26);
            browseSource.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select the GameLoop / TxGameAssistant folder";
                    dialog.SelectedPath = _txtSource.Text.Trim();
                    dialog.ShowNewFolderButton = false;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _txtSource.Text = dialog.SelectedPath;
                }
            };
            sourceGroup.Controls.Add(browseSource);

            _lblStatus = new Label();
            _lblStatus.SetBounds(14, 178, 640, 24);
            _lblStatus.ForeColor = Color.DimGray;
            sourceGroup.Controls.Add(_lblStatus);

            GroupBox targetGroup = new GroupBox();
            targetGroup.Text = "Portable folder (target)";
            targetGroup.SetBounds(14, 258, 672, 86);
            Controls.Add(targetGroup);

            Label targetLabel = new Label();
            targetLabel.Text = "Folder:";
            targetLabel.SetBounds(14, 26, 50, 20);
            targetGroup.Controls.Add(targetLabel);

            _txtTarget = new TextBox();
            _txtTarget.SetBounds(66, 24, 470, 24);
            _txtTarget.Text = string.IsNullOrEmpty(output) ? @"D:\TxGameAssistant" : output;
            targetGroup.Controls.Add(_txtTarget);

            Button browseTarget = new Button();
            browseTarget.Text = "Browse...";
            browseTarget.SetBounds(546, 23, 100, 26);
            browseTarget.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select the portable GameLoop folder";
                    dialog.SelectedPath = _txtTarget.Text.Trim();
                    dialog.ShowNewFolderButton = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _txtTarget.Text = dialog.SelectedPath;
                }
            };
            targetGroup.Controls.Add(browseTarget);

            Label targetNote = new Label();
            targetNote.Text = "Recommended: D:\\TxGameAssistant (GameLoop is path-sensitive).";
            targetNote.SetBounds(14, 56, 640, 20);
            targetNote.ForeColor = Color.DimGray;
            targetGroup.Controls.Add(targetNote);

            CheckBox includeData = new CheckBox();
            includeData.Text = "Include Snapshot + Temp inside the package and link them to the standard paths";
            includeData.SetBounds(18, 354, 640, 22);
            includeData.Checked = true;
            includeData.Enabled = false;
            Controls.Add(includeData);

            CheckBox exportReg = new CheckBox();
            exportReg.Text = "Export the Tencent registry keys from this PC now (Tencent.reg / Tencent2.reg / Uninstall.reg)";
            exportReg.SetBounds(18, 378, 640, 22);
            exportReg.Checked = true;
            exportReg.Enabled = false;
            Controls.Add(exportReg);

            Button ok = new Button();
            ok.Text = "Build";
            ok.SetBounds(486, 420, 96, 30);
            ok.Click += delegate
            {
                string source = ResolveRoot(_txtSource.Text.Trim());
                if (source == null)
                {
                    MessageBox.Show(this,
                        "This folder is not a GameLoop installation.\r\n\r\n" +
                        "Expected UI\\AndroidEmulatorEn.exe or AppMarket\\AppMarket.exe inside it.",
                        "GameLoop Sandbox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string target = _txtTarget.Text.Trim().Trim('"');
                target = Environment.ExpandEnvironmentVariables(target).Replace('/', '\\');
                if (target.Length == 0)
                {
                    MessageBox.Show(this, "Choose the portable folder.", "GameLoop Sandbox",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try { target = Path.GetFullPath(target).TrimEnd('\\'); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Invalid folder:\r\n" + ex.Message, "GameLoop Sandbox",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (PathUtil.IsDriveRoot(target))
                {
                    MessageBox.Show(this, "Choose a folder, not a drive root.", "GameLoop Sandbox",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (PathUtil.IsNetworkPath(target))
                {
                    MessageBox.Show(this,
                        "GameLoop must stay on a local folder (D:\\TxGameAssistant).\r\n" +
                        "A UNC/network path (including a mapped network drive) would share Snapshot and Temp between PCs.\r\n\r\n" +
                        "Use a local drive only.",
                        "GameLoop Sandbox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (SamePath(source, target) || PathUtil.IsSameOrSubPath(target, source) ||
                    PathUtil.IsSameOrSubPath(source, target))
                {
                    MessageBox.Show(this,
                        "The source and the portable folder must not overlap.\r\n\r\n" +
                        "Source: " + source + "\r\nTarget: " + target + "\r\n\r\n" +
                        "Use D:\\Program Files\\TxGameAssistant as the source, or pick another target.",
                        "GameLoop Sandbox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!SamePath(target, @"D:\TxGameAssistant"))
                {
                    DialogResult answer = MessageBox.Show(this,
                        "The recommended target is D:\\TxGameAssistant.\r\n\r\n" +
                        "Continue with:\r\n" + target + " ?",
                        "GameLoop Sandbox", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) return;
                }

                SourceFolder = source;
                TargetFolder = target;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(590, 420, 96, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            UpdateStatus();
        }

        private string builtSource(string install, List<string> candidates)
        {
            string resolved = ResolveRoot(install);
            if (resolved != null) return resolved;
            foreach (string candidate in candidates)
                if (IsGameLoop(candidate)) return candidate;
            return install ?? "";
        }

        private void AddCandidate(List<string> candidates, List<string> seen, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string resolved;
            try { resolved = Path.GetFullPath(path.Trim('"')).TrimEnd('\\'); }
            catch { return; }
            foreach (string existing in seen)
                if (SamePath(existing, resolved)) return;
            seen.Add(resolved);
            candidates.Add(resolved);
        }

        private void UpdateStatus()
        {
            if (_lblStatus == null) return;
            string source = ResolveRoot(_txtSource.Text.Trim());
            if (source == null)
            {
                _lblStatus.ForeColor = Color.Firebrick;
                _lblStatus.Text = "Not a GameLoop folder (UI\\AndroidEmulatorEn.exe missing).";
            }
            else
            {
                _lblStatus.ForeColor = Color.SeaGreen;
                _lblStatus.Text = "OK: " + source;
            }
        }

        private static string ResolveRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
                path = Path.GetFullPath(path);
            }
            catch { return null; }

            if (File.Exists(path)) path = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
            if (IsGameLoop(path)) return path.TrimEnd('\\');

            string child = Path.Combine(path, "TxGameAssistant");
            if (IsGameLoop(child)) return child.TrimEnd('\\');

            string name = Path.GetFileName(path.TrimEnd('\\'));
            if (string.Equals(name, "UI", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "AppMarket", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "AOW_Rootfs_100", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(path.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent) && IsGameLoop(parent)) return parent.TrimEnd('\\');
            }
            return null;
        }

        private static bool IsGameLoop(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            if (File.Exists(Path.Combine(path, "UI", "AndroidEmulatorEn.exe"))) return true;
            if (File.Exists(Path.Combine(path, "ui", "AndroidEmulatorEn.exe"))) return true;
            return false;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
