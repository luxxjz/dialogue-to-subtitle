// MainForm.cs - 主窗体: 布局 / 事件 / config 绑定 / 后台线程调度
// 布局: ①输入 ②分离&输出 ③识别 ④LLM后处理 ⑤输出预览 操作按钮 日志区
// C# / .NET Framework 4.8, csc 编译

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SrtGui
{
    public class MainForm : Form
    {
        // ---- ① 输入 ----
        TextBox txtFile;
        Button btnBrowse, btnProbe;
        ComboBox cmbTrack;          // 音轨语言列表 (Tag="idx|lang")
        ComboBox cmbLang;           // 识别语言 (可编辑, 支持手输任意 whisper 语言码)
        ComboBox cmbTgt;            // 目标语言 (可编辑, 翻译成什么语言码)
        CheckBox chkAuto;
        Label lblHint;

        // ---- ② 分离 & 输出 ----
        ComboBox cmbSepModel;       // Tag: ""=Inst_HQ_3 / "kim" / "mdx23c"
        CheckBox chkSaveVocals, chkSaveInst, chkBreakLine, chkTradToSimp, chkSep;
        ComboBox cmbOverlap;

        // ---- ③ 识别 ----
        ComboBox cmbVad;
        CheckBox chkVad;            // VAD 切片开关 (false=整轨直推)
        ComboBox cmbGpu;            // whisper GPU 下拉 (Tag: -1=自动 / 0..N)
        NumericUpDown numParallel;
        ComboBox cmbWhisper;        // Tag=bin 路径

        // ---- ④ LLM 后处理 ----
        ComboBox cmbLlmType;        // Tag: ollama/openai
        TextBox txtUrl, txtApiKey;
        Button btnProbeLlm;
        ComboBox cmbLlmModel;
        NumericUpDown numBatch;
        ComboBox cmbTimeout;        // LLM 单次请求超时 (秒)
        ComboBox cmbRetry;          // LLM 单批最大尝试次数
        RadioButton rbNone, rbTranslate, rbRefine;
        RadioButton rbEmbedded, rbInterface;   // LLM 后端: 内嵌引擎 / 外部接口 (单选联动)

        // ---- ⑤ 输出 ----
        RadioButton rbOutSource, rbOutProgram, rbOutCustom;
        Button btnBrowseOut;
        Label lblOutDir;
        ComboBox cmbTxtOut;          // 纯文本输出: 不生成/分行版/段落版/两者
        NumericUpDown numParaGap;    // 段落阈值(秒)

        // ---- 操作 ----
        Button btnStart, btnStop;
        Label lblStatus;

        // ---- 日志 ----
        TextBox txtLog;
        StreamWriter _logFile;
        readonly object _logLock = new object();

        // ---- 分组引用 (形态切换用) ----
        GroupBox _g2, _g3;
        Label _lblKey;

        Config _cfg;
        Pipeline _pipe;
        string _root;               // exe 所在目录
        bool _modeSubtitle;         // 当前输入是字幕直翻模式
        ProcRunner _runnerUi;       // 探测时拉起的引擎进程归属 (退出程序时回收)
        LlmServer _probeServer;     // 探测拉起的引擎引用

        // ============================================================ ctor
        public MainForm()
        {
            _root = AppDomain.CurrentDomain.BaseDirectory;
            _cfg = new Config(Path.Combine(_root, "config.txt"));
            _cfg.Load();
            // 窗口左上角图标: 从自身 exe 提取 (与文件图标一致, 单目录移动版无需附带图标文件)
            try
            {
                System.Drawing.Icon appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) Icon = appIcon;
            }
            catch { }
            _runnerUi = new ProcRunner();
            _pipe = new Pipeline();
            _pipe.Root = _root;
            _pipe.Log = delegate(string m) { UiLogFile(m); };
            _pipe.Status = delegate(string s) { UiStatus(s); };
            _pipe.OnStep = delegate(Step s) { UiStep(s); };
            _pipe.OnFinished = delegate(string r) { UiFinished(r); };

            BuildUi();
            LoadCfgToUi();
            UiLog("就绪。选择视频文件 -> 解析音轨 -> 开始处理 (断点续跑, 停止后重开跳过已完成步骤)");
        }

        // ============================================================ 界面构建
        void BuildUi()
        {
            Text = "SRT 自动字幕生成";
            ClientSize = new Size(760, 700);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);

            // ---- ① 输入 ----
            GroupBox g1 = new GroupBox();
            g1.Text = "① 输入";
            g1.SetBounds(10, 10, 740, 80);
            Controls.Add(g1);

            AddLabel(g1, "文件", 15, 24);
            txtFile = AddTextBox(g1, 55, 22, 470);
            btnBrowse = AddButton(g1, "浏览...", 530, 19, 60, 27, BtnBrowse_Click);
            btnProbe = AddButton(g1, "解析", 595, 19, 50, 27, BtnProbe_Click);
            AddLabel(g1, "音轨", 15, 54);
            cmbTrack = AddCombo(g1, 55, 52, 120);
            AddLabel(g1, "识别语言", 185, 54);
            cmbLang = AddComboEditable(g1, 255, 52, 110);
            FillRecogLangs();
            chkAuto = AddCheck(g1, "自动识别", 375, 54, ChkAuto_Checked);
            AddLabel(g1, "目标语言", 465, 54);
            cmbTgt = AddComboEditable(g1, 535, 52, 110);
            FillTgtLangs();
            lblHint = AddLabel(g1, "", 655, 54);

            // ---- ② 人声分离输出 ----
            GroupBox g2 = new GroupBox();
            g2.Text = "";   // 无自带标题, "② 人声分离输出"复选框作标题+总开关
            g2.SetBounds(10, 98, 740, 80);
            Controls.Add(g2);
            _g2 = g2;

            // 总开关兼标题: 文字在前, 勾选框在字符后面; 取消勾选整组灰掉
            chkSep = AddCheck(g2, "② 人声分离输出", 10, 3, ChkSep_Changed);
            chkSep.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
            chkSep.Checked = true;

            AddLabel(g2, "分离模型", 15, 30);
            cmbSepModel = AddCombo(g2, 80, 28, 170);
            FillSepModels();

            chkSaveVocals = AddCheck(g2, "保存人声轨", 270, 30, null);
            chkSaveInst = AddCheck(g2, "保存伴奏轨", 370, 30, null);
            AddLabel(g2, "overlap", 480, 30);
            cmbOverlap = AddCombo(g2, 545, 28, 60);
            cmbOverlap.Items.Add(new ItemTag("0.25", "0.25"));
            cmbOverlap.Items.Add(new ItemTag("0.5", "0.5"));
            cmbOverlap.SelectedIndex = 0;

            // ---- ③ 识别 ----
            GroupBox g3 = new GroupBox();
            g3.Text = "③ 识别";
            g3.SetBounds(10, 186, 740, 100);
            Controls.Add(g3);
            _g3 = g3;

            // 第一行: VAD 开关在最前, 与灵敏度/并行实例关联 (取消勾选两者灰掉)
            chkVad = AddCheck(g3, "VAD 切片", 15, 24, ChkVad_Changed);
            chkVad.Checked = true;
            AddLabel(g3, "VAD 灵敏度", 110, 24);
            cmbVad = AddCombo(g3, 190, 22, 130);
            cmbVad.Items.Add(new ItemTag("标准 (0.5/250ms)", "standard"));
            cmbVad.Items.Add(new ItemTag("灵敏 (0.3/100ms)", "sensitive"));
            cmbVad.SelectedIndex = 0;
            AddLabel(g3, "并行实例", 335, 24);
            numParallel = AddNumeric(g3, 405, 22, 45, 1, 8, 2);
            // 断行/繁转简: 字幕文本处理选项, 随识别区 (并行实例后面)
            chkBreakLine = AddCheck(g3, "断行", 480, 24, null);
            chkTradToSimp = AddCheck(g3, "繁转简", 540, 24, null);
            // 第二行: whisper 模型(左) + whisper GPU(右)
            AddLabel(g3, "whisper 模型", 15, 54);
            cmbWhisper = AddCombo(g3, 90, 52, 250);
            FillWhisperModels();
            AddLabel(g3, "whisper GPU", 355, 54);
            cmbGpu = AddCombo(g3, 430, 52, 280);
            FillGpuList();

            // ---- ④ LLM 后处理 ----
            GroupBox g4 = new GroupBox();
            g4.Text = "④ LLM 后处理";
            g4.SetBounds(10, 294, 740, 100);
            Controls.Add(g4);

            // 后端单选放入独立 Panel, 与下方模式单选(rbNone/rbTranslate/rbRefine)分组隔离 (WinForms 同容器 radio 自动同组)
            Panel pBackend = new Panel();
            pBackend.SetBounds(0, 18, 155, 30);
            g4.Controls.Add(pBackend);
            rbEmbedded = AddRadio(pBackend, "内嵌引擎", 12, 6, false, RbLlmBackend_Changed);
            rbInterface = AddRadio(pBackend, "接口", 100, 6, true, RbLlmBackend_Changed);
            AddLabel(g4, "接口", 160, 24);
            cmbLlmType = AddCombo(g4, 200, 22, 80);
            cmbLlmType.Items.Add(new ItemTag("Ollama", "ollama"));
            cmbLlmType.Items.Add(new ItemTag("OpenAI 兼容", "openai"));
            cmbLlmType.SelectedIndex = 0;
            cmbLlmType.SelectedIndexChanged += CmbLlmType_Changed;
            AddLabel(g4, "地址", 292, 24);
            txtUrl = AddTextBox(g4, 332, 22, 168);
            btnProbeLlm = AddButton(g4, "探测", 505, 19, 50, 27, BtnProbeLlm_Click);
            AddLabel(g4, "模型", 565, 24);
            cmbLlmModel = AddCombo(g4, 605, 22, 125);

            rbNone = AddRadio(g4, "不处理", 12, 56, false, RbMode_Changed);
            rbTranslate = AddRadio(g4, "翻译到目标语言", 115, 56, true, RbMode_Changed);
            rbRefine = AddRadio(g4, "中文纠错", 240, 56, false, RbMode_Changed);
            AddLabel(g4, "超时s", 315, 58);
            cmbTimeout = AddCombo(g4, 352, 56, 48);
            cmbTimeout.Items.AddRange(new object[] { "10", "20", "30", "40", "50", "60" });
            AddLabel(g4, "重试", 408, 58);
            cmbRetry = AddCombo(g4, 440, 56, 40);
            cmbRetry.Items.AddRange(new object[] { "1", "2", "3", "4", "5" });
            AddLabel(g4, "批大小", 492, 58);
            numBatch = AddNumeric(g4, 545, 56, 42, 1, 200, 40);
            Label lblKey = AddLabel(g4, "API Key", 597, 58);
            lblKey.Visible = false;
            _lblKey = lblKey;
            txtApiKey = AddTextBox(g4, 645, 56, 85);
            txtApiKey.Visible = false;

            // ---- ⑤ 输出 ----
            GroupBox g5 = new GroupBox();
            g5.Text = "⑤ 输出";
            g5.SetBounds(10, 402, 740, 52);
            Controls.Add(g5);
            rbOutSource = AddRadio(g5, "原始文件夹", 15, 14, false, OutRadio_Changed);
            rbOutProgram = AddRadio(g5, "程序文件夹", 110, 14, true, OutRadio_Changed);
            rbOutCustom = AddRadio(g5, "自定义文件夹", 210, 14, false, OutRadio_Changed);
            btnBrowseOut = AddButton(g5, "浏览...", 300, 12, 60, 22, BtnBrowseOut_Click);
            btnBrowseOut.Enabled = false;
            AddLabel(g5, "纯文本输出", 375, 15);
            cmbTxtOut = AddCombo(g5, 445, 12, 70);
            cmbTxtOut.Items.Add(new ItemTag("不生成", "0"));
            cmbTxtOut.Items.Add(new ItemTag("分行版", "1"));
            cmbTxtOut.Items.Add(new ItemTag("段落版", "2"));
            cmbTxtOut.Items.Add(new ItemTag("两者", "3"));
            cmbTxtOut.SelectedIndex = 0;
            AddLabel(g5, "段落阈值(s)", 535, 15);
            numParaGap = AddNumeric(g5, 620, 12, 47, 0.5m, 5.0m, 1.5m);
            numParaGap.DecimalPlaces = 1;
            numParaGap.Increment = 0.1m;
            lblOutDir = AddLabel(g5, "(选择文件后显示)", 15, 36);

            // ---- 操作 ----
            btnStart = AddButton(this, "开始处理", 15, 462, 110, 34, BtnStart_Click);
            btnStop = AddButton(this, "停止", 135, 462, 80, 34, BtnStop_Click);
            btnStop.Enabled = false;
            lblStatus = AddLabel(this, "", 230, 472);

            // ---- 日志 ----
            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(17, 20, 24);
            txtLog.ForeColor = Color.FromArgb(207, 232, 207);
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.SetBounds(10, 504, 740, 182);
            Controls.Add(txtLog);

            txtFile.TextChanged += delegate { UpdateModeAndOut(); };

            // 文件路径框支持拖放: 拖入文件/文件夹直接填入路径
            txtFile.AllowDrop = true;
            txtFile.DragEnter += TxtFile_DragEnter;
            txtFile.DragDrop += TxtFile_DragDrop;
        }

        void FillSepModels()
        {
            cmbSepModel.Items.Clear();
            string modelsDir = Path.Combine(_root, "models");
            if (Directory.Exists(modelsDir))
            {
                foreach (string f in Directory.GetFiles(modelsDir, "*.onnx"))
                {
                    cmbSepModel.Items.Add(new ItemTag(Path.GetFileName(f), f));
                }
            }
            if (cmbSepModel.Items.Count == 0)
            {
                cmbSepModel.Items.Add(new ItemTag("(未找到 models\\*.onnx)", ""));
            }
            cmbSepModel.SelectedIndex = 0;
        }

        void FillWhisperModels()
        {
            cmbWhisper.Items.Clear();
            string modelsDir = Path.Combine(_root, "models");
            if (Directory.Exists(modelsDir))
            {
                foreach (string f in Directory.GetFiles(modelsDir, "*.bin"))
                {
                    string fn = Path.GetFileName(f).ToLower();
                    // 排除 VAD 专用模型 (silero / vad), 它们不是识别模型
                    if (fn.Contains("silero") || fn.Contains("vad")) { continue; }
                    cmbWhisper.Items.Add(new ItemTag(Path.GetFileName(f), f));
                }
            }
            if (cmbWhisper.Items.Count == 0)
            {
                cmbWhisper.Items.Add(new ItemTag("(未找到 models\\*.bin)", ""));
            }
            cmbWhisper.SelectedIndex = 0;
        }

        // whisper GPU 下拉: 自动(NVIDIA 优先) + 枚举到的 Vulkan 设备
        void FillGpuList()
        {
            cmbGpu.Items.Clear();
            cmbGpu.Items.Add(new ItemTag("自动 (NVIDIA 优先)", "-1"));
            foreach (VkUtil.GpuInfo g in VkUtil.EnumerateDevices())
            {
                cmbGpu.Items.Add(new ItemTag(g.Name + (g.IsNvidia ? "  [NVIDIA]" : ""), g.Index.ToString()));
            }
            if (cmbGpu.Items.Count == 1)
            {
                cmbGpu.Items.Add(new ItemTag("(未检测到 Vulkan 设备)", "-1"));
            }
            cmbGpu.SelectedIndex = 0;
        }

        // 人声分离开关: 取消勾选 -> ② 组内除开关外全部灰掉 (直接识别原始轨)
        void ChkSep_Changed(object sender, EventArgs e)
        {
            bool en = chkSep.Checked;
            foreach (Control c in _g2.Controls)
            {
                if (c == chkSep) { continue; }
                c.Enabled = en;
            }
        }

        // VAD 开关: 取消勾选 -> 灵敏度/并行实例灰掉 (整轨直推时两者无效)
        void ChkVad_Changed(object sender, EventArgs e)
        {
            bool en = chkVad.Checked;
            if (cmbVad != null) { cmbVad.Enabled = en; }
            if (numParallel != null) { numParallel.Enabled = en; }
        }

        // ============================================================ 控件工厂
        Label AddLabel(Control parent, string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            parent.Controls.Add(l);
            return l;
        }
        TextBox AddTextBox(Control parent, int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.SetBounds(x, y, w, 23);
            parent.Controls.Add(t);
            return t;
        }
        Button AddButton(Control parent, string text, int x, int y, int w, int h, EventHandler hdl)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            if (hdl != null) { b.Click += hdl; }
            parent.Controls.Add(b);
            return b;
        }
        ComboBox AddCombo(Control parent, int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.SetBounds(x, y, w, 23);
            parent.Controls.Add(c);
            return c;
        }
        ComboBox AddComboEditable(Control parent, int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDown;   // 可编辑: 下拉 + 手输
            c.SetBounds(x, y, w, 23);
            parent.Controls.Add(c);
            return c;
        }
        CheckBox AddCheck(Control parent, string text, int x, int y, EventHandler hdl)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Location = new Point(x, y);
            if (hdl != null) { c.CheckedChanged += hdl; }
            parent.Controls.Add(c);
            return c;
        }
        RadioButton AddRadio(Control parent, string text, int x, int y, bool checked_, EventHandler hdl)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.Location = new Point(x, y);
            r.Checked = checked_;
            if (hdl != null) { r.CheckedChanged += hdl; }
            parent.Controls.Add(r);
            return r;
        }
        NumericUpDown AddNumeric(Control parent, int x, int y, int w, decimal min, decimal max, decimal val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Value = val;
            n.SetBounds(x, y, w, 23);
            parent.Controls.Add(n);
            return n;
        }

        // ComboBox 条目: 显示文本 + Tag
        class ItemTag
        {
            public string Text;
            public string Tag;
            public ItemTag(string text, string tag) { Text = text; Tag = tag; }
            public override string ToString() { return Text; }
        }

        // ============================================================ config <-> UI
        void LoadCfgToUi()
        {
            txtUrl.Text = _cfg.LlmUrl;
            numBatch.Value = Math.Max(1, Math.Min(200, _cfg.BatchSize));
            SelectComboValue(cmbTimeout, _cfg.TimeoutSec.ToString());
            SelectComboValue(cmbRetry, _cfg.RetryCount.ToString());
            rbNone.Checked = (_cfg.Mode == "none");
            rbTranslate.Checked = (_cfg.Mode == "translate");
            rbRefine.Checked = (_cfg.Mode == "refine");
            if (_cfg.Mode != "none" && _cfg.Mode != "refine") { rbTranslate.Checked = true; }
            chkSaveVocals.Checked = _cfg.SaveVocals;
            chkSaveInst.Checked = _cfg.SaveInst;
            chkBreakLine.Checked = _cfg.BreakLine;
            chkTradToSimp.Checked = _cfg.TradToSimp;
            chkSep.Checked = _cfg.EnableSep;
            chkVad.Checked = _cfg.EnableVad;
            SelectComboTag(cmbGpu, _cfg.WhisperGpuIndex.ToString());
            if (cmbGpu.SelectedIndex < 0) { cmbGpu.SelectedIndex = 0; }
            numParallel.Value = Math.Max(1, Math.Min(8, _cfg.Parallel));
            txtApiKey.Text = _cfg.LlmKey;
            if (_cfg.LlmUrl != "") { txtUrl.Text = _cfg.LlmUrl; }
            if (_cfg.LastInput != "" && File.Exists(_cfg.LastInput)) { txtFile.Text = _cfg.LastInput; }
            SelectComboTag(cmbVad, _cfg.VadPreset);
            rbEmbedded.Checked = (_cfg.LlmType == "llama");
            rbInterface.Checked = !rbEmbedded.Checked;
            SelectComboTag(cmbLlmType, (_cfg.LlmType == "llama") ? "ollama" : _cfg.LlmType);
            RbLlmBackend_Changed(null, EventArgs.Empty);
            SelectComboByFileName(cmbSepModel, _cfg.SepModel);
            SelectComboTag(cmbOverlap, _cfg.Overlap.ToString("0.##"));
            SetRecogLang(_cfg.RecogLang);
            SetTgtLang(_cfg.TgtLang);
            if (_cfg.LlmModel != "") { cmbLlmModel.Text = _cfg.LlmModel; }
            if (_cfg.WhisperModel != "")
            {
                for (int i = 0; i < cmbWhisper.Items.Count; i++)
                {
                    ItemTag it = (ItemTag)cmbWhisper.Items[i];
                    if (it.Tag != "" && Path.GetFileName(it.Tag) == Path.GetFileName(_cfg.WhisperModel)) { cmbWhisper.SelectedIndex = i; }
                }
            }
            // 输出模式
            if (_cfg.OutMode == "source") { rbOutSource.Checked = true; }
            else if (_cfg.OutMode == "custom") { rbOutCustom.Checked = true; }
            else { rbOutProgram.Checked = true; }
            btnBrowseOut.Enabled = rbOutCustom.Checked;
            // 纯文本输出
            SelectComboTag(cmbTxtOut, _cfg.TxtOut.ToString());
            if (cmbTxtOut.SelectedIndex < 0) { cmbTxtOut.SelectedIndex = 0; }
            numParaGap.Value = Math.Max(0.5m, Math.Min(5.0m, (decimal)_cfg.TxtParaGap));
            UpdateOutPreview();
        }

        void SelectComboByFileName(ComboBox cb, string tag)
        {
            if (tag == "") { return; }
            for (int i = 0; i < cb.Items.Count; i++)
            {
                ItemTag it = cb.Items[i] as ItemTag;
                if (it != null && Path.GetFileName(it.Tag) == Path.GetFileName(tag)) { cb.SelectedIndex = i; return; }
            }
        }

        void SelectComboTag(ComboBox cb, string tag)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                ItemTag it = cb.Items[i] as ItemTag;
                if (it != null && it.Tag == tag) { cb.SelectedIndex = i; return; }
            }
        }

        // 按显示文本选中下拉项 (超时/重试等纯数字项)
        void SelectComboValue(ComboBox cb, string v)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                if (Convert.ToString(cb.Items[i]) == v) { cb.SelectedIndex = i; return; }
            }
            if (cb.Items.Count > 0) { cb.SelectedIndex = 0; }
        }

        // 读取下拉框数值, 解析失败用默认值
        int ParseComboInt(ComboBox cb, int def)
        {
            if (cb.SelectedItem == null) { return def; }
            int r; return int.TryParse(Convert.ToString(cb.SelectedItem), out r) ? r : def;
        }

        void SaveUiToCfg()
        {
            _cfg.LlmUrl = txtUrl.Text.Trim();
            _cfg.LlmModel = cmbLlmModel.Text.Trim();
            _cfg.BatchSize = (int)numBatch.Value;
            _cfg.TimeoutSec = ParseComboInt(cmbTimeout, 60);
            _cfg.RetryCount = ParseComboInt(cmbRetry, 4);
            _cfg.Mode = rbNone.Checked ? "none" : (rbTranslate.Checked ? "translate" : "refine");
            _cfg.SaveVocals = chkSaveVocals.Checked;
            _cfg.SaveInst = chkSaveInst.Checked;
            _cfg.BreakLine = chkBreakLine.Checked;
            _cfg.TradToSimp = chkTradToSimp.Checked;
            _cfg.EnableSep = chkSep.Checked;
            _cfg.EnableVad = chkVad.Checked;
            ItemTag gt = cmbGpu.SelectedItem as ItemTag;
            if (gt != null)
            {
                int gi; _cfg.WhisperGpuIndex = int.TryParse(gt.Tag, out gi) ? gi : -1;
            }
            _cfg.Parallel = (int)numParallel.Value;
            _cfg.LlmKey = txtApiKey.Text.Trim();
            ItemTag vt = cmbVad.SelectedItem as ItemTag;
            if (vt != null) { _cfg.VadPreset = vt.Tag; }
            ItemTag lt = cmbLlmType.SelectedItem as ItemTag;
            _cfg.LlmType = rbEmbedded.Checked ? "llama" : ((lt != null) ? lt.Tag : "ollama");
            ItemTag st = cmbSepModel.SelectedItem as ItemTag;
            if (st != null) { _cfg.SepModel = st.Tag; }
            ItemTag ot = cmbOverlap.SelectedItem as ItemTag;
            if (ot != null) { _cfg.Overlap = double.Parse(ot.Tag); }
            ItemTag wt = cmbWhisper.SelectedItem as ItemTag;
            if (wt != null && wt.Tag != "") { _cfg.WhisperModel = wt.Tag; }
            _cfg.RecogLang = RecogLangCode();
            _cfg.TgtLang = TgtLangCode();
            _cfg.OutMode = rbOutSource.Checked ? "source" : (rbOutCustom.Checked ? "custom" : "program");
            ItemTag tt = cmbTxtOut.SelectedItem as ItemTag;
            if (tt != null) { _cfg.TxtOut = int.Parse(tt.Tag); }
            _cfg.TxtParaGap = (double)numParaGap.Value;
            _cfg.LastInput = txtFile.Text.Trim();
            if (_cfg.LastInput != "") { _cfg.LastDir = Path.GetDirectoryName(_cfg.LastInput); }
        }

        // ============================================================ 形态切换
        void UpdateModeAndOut()
        {
            string f = txtFile.Text.Trim();
            bool isSub = IsSubtitleFile(f);
            _modeSubtitle = isSub;

            _g2.Enabled = !isSub;
            _g3.Enabled = !isSub;
            chkAuto.Enabled = !isSub;
            cmbTrack.Enabled = !isSub;      // 字幕直翻不选音轨, 源语言用识别语言下拉
            rbNone.Enabled = !isSub;    // 字幕直翻必须翻译, 不允许"不处理"

            if (isSub)
            {
                lblHint.Text = "字幕直翻模式";
            }
            else
            {
                lblHint.Text = "";
                cmbTrack.Items.Clear();
                cmbTrack.Text = "";
            }

            // 输出目录预览 (按输出模式)
            UpdateOutPreview();
        }

        void FillRecogLangs()
        {
            FillLangs(cmbLang, true);
        }

        void FillTgtLangs()
        {
            FillLangs(cmbTgt, false);
        }

        void FillLangs(ComboBox cb, bool withAuto)
        {
            if (withAuto) { cb.Items.Add(new ItemTag("自动识别 (auto)", "auto")); }
            cb.Items.Add(new ItemTag("中文 (zh)", "zh"));
            cb.Items.Add(new ItemTag("英文 (en)", "en"));
            cb.Items.Add(new ItemTag("日文 (ja)", "ja"));
            cb.Items.Add(new ItemTag("韩文 (ko)", "ko"));
            cb.Items.Add(new ItemTag("西班牙文 (es)", "es"));
            cb.Items.Add(new ItemTag("法文 (fr)", "fr"));
            cb.Items.Add(new ItemTag("德文 (de)", "de"));
            cb.Items.Add(new ItemTag("俄文 (ru)", "ru"));
            cb.Items.Add(new ItemTag("意大利文 (it)", "it"));
            cb.Items.Add(new ItemTag("葡萄牙文 (pt)", "pt"));
            cb.Items.Add(new ItemTag("阿拉伯文 (ar)", "ar"));
            cb.Items.Add(new ItemTag("越南文 (vi)", "vi"));
            cb.Items.Add(new ItemTag("泰文 (th)", "th"));
            cb.Items.Add(new ItemTag("印尼文 (id)", "id"));
            cb.Items.Add(new ItemTag("马来文 (ms)", "ms"));
            cb.Items.Add(new ItemTag("印地文 (hi)", "hi"));
            cb.Items.Add(new ItemTag("乌尔都文 (ur)", "ur"));
            cb.Items.Add(new ItemTag("孟加拉文 (bn)", "bn"));
            cb.Items.Add(new ItemTag("泰米尔文 (ta)", "ta"));
            cb.Items.Add(new ItemTag("土耳其文 (tr)", "tr"));
            cb.Items.Add(new ItemTag("荷兰文 (nl)", "nl"));
            cb.Items.Add(new ItemTag("瑞典文 (sv)", "sv"));
            cb.Items.Add(new ItemTag("丹麦文 (da)", "da"));
            cb.Items.Add(new ItemTag("挪威文 (no)", "no"));
            cb.Items.Add(new ItemTag("芬兰文 (fi)", "fi"));
            cb.Items.Add(new ItemTag("波兰文 (pl)", "pl"));
            cb.Items.Add(new ItemTag("乌克兰文 (uk)", "uk"));
            cb.Items.Add(new ItemTag("希伯来文 (he)", "he"));
            cb.Items.Add(new ItemTag("波斯文 (fa)", "fa"));
            cb.Items.Add(new ItemTag("希腊文 (el)", "el"));
            cb.Items.Add(new ItemTag("捷克文 (cs)", "cs"));
            cb.Items.Add(new ItemTag("斯洛伐克文 (sk)", "sk"));
            cb.Items.Add(new ItemTag("匈牙利文 (hu)", "hu"));
            cb.Items.Add(new ItemTag("罗马尼亚文 (ro)", "ro"));
            cb.Items.Add(new ItemTag("保加利亚文 (bg)", "bg"));
            cb.Items.Add(new ItemTag("塞尔维亚文 (sr)", "sr"));
            cb.Items.Add(new ItemTag("克罗地亚文 (hr)", "hr"));
            cb.Items.Add(new ItemTag("加泰罗尼亚文 (ca)", "ca"));
            cb.Items.Add(new ItemTag("粤语 (yue)", "yue"));
            if (withAuto) { cb.SelectedIndex = 0; }
            else { SelectComboTag(cb, "zh"); if (cb.SelectedIndex < 0 && cb.Items.Count > 0) { cb.SelectedIndex = 0; } }
        }

        void SetRecogLang(string code)
        {
            if (code == "" || code == "und") { code = "auto"; }
            for (int i = 0; i < cmbLang.Items.Count; i++)
            {
                ItemTag it = (ItemTag)cmbLang.Items[i];
                if (it.Tag == code) { cmbLang.SelectedIndex = i; return; }
            }
            cmbLang.Text = code;   // 不在列表中的自定义语言码
        }

        // 当前识别语言码: chkAuto 勾选强制 auto; 否则取下拉 Tag 或手输文本
        string RecogLangCode()
        {
            if (chkAuto.Checked) { return "auto"; }
            ItemTag it = cmbLang.SelectedItem as ItemTag;
            string txt = cmbLang.Text.Trim();
            if (it != null && txt == it.Text) { return it.Tag; }
            int a = txt.IndexOf('(');
            int b = txt.IndexOf(')');
            if (a >= 0 && b > a) { return txt.Substring(a + 1, b - a - 1).Trim(); }
            return txt;
        }

        void SetTgtLang(string code)
        {
            if (code == "" || code == "und") { code = "zh"; }
            for (int i = 0; i < cmbTgt.Items.Count; i++)
            {
                ItemTag it = (ItemTag)cmbTgt.Items[i];
                if (it.Tag == code) { cmbTgt.SelectedIndex = i; return; }
            }
            cmbTgt.Text = code;   // 不在列表中的自定义语言码
        }

        // 当前目标语言码: 取下拉 Tag 或手输文本; 空值回退中文
        string TgtLangCode()
        {
            ItemTag it = cmbTgt.SelectedItem as ItemTag;
            string txt = cmbTgt.Text.Trim();
            if (it != null && txt == it.Text) { return it.Tag; }
            int a = txt.IndexOf('(');
            int b = txt.IndexOf(')');
            if (a >= 0 && b > a) { return txt.Substring(a + 1, b - a - 1).Trim(); }
            return (txt == "") ? "zh" : txt;
        }

        bool IsSubtitleFile(string f)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            return e == ".srt";
        }

        // ============================================================ 事件
        // 输出模式单选变化: 自定义模式才允许浏览目录
        void OutRadio_Changed(object sender, EventArgs e)
        {
            btnBrowseOut.Enabled = rbOutCustom.Checked;
            UpdateOutPreview();
        }

        // 自定义输出目录浏览
        void BtnBrowseOut_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "选择输出文件夹 (结果保存在所选目录下的同名子目录)";
            if (_cfg.CustomOutDir != "" && Directory.Exists(_cfg.CustomOutDir)) { dlg.SelectedPath = _cfg.CustomOutDir; }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _cfg.CustomOutDir = dlg.SelectedPath;
                UpdateOutPreview();
            }
        }

        // 输出目录预览 (按当前输出模式计算任务目录)
        void UpdateOutPreview()
        {
            string f = txtFile.Text.Trim();
            if (f == "" || !File.Exists(f)) { lblOutDir.Text = "(选择文件后显示)"; return; }
            string baseName = Path.GetFileNameWithoutExtension(f);
            string outRoot = _root;
            if (rbOutSource.Checked)
            {
                string d = Path.GetDirectoryName(f);
                if (!string.IsNullOrEmpty(d)) { outRoot = d; }
            }
            else if (rbOutCustom.Checked && _cfg.CustomOutDir != "")
            {
                outRoot = _cfg.CustomOutDir;
            }
            lblOutDir.Text = "任务目录: " + Path.Combine(outRoot, baseName) + "\\";
        }

        Pipeline.OutModeEnum OutModeFromUi()
        {
            if (rbOutSource.Checked) { return Pipeline.OutModeEnum.Source; }
            if (rbOutCustom.Checked) { return Pipeline.OutModeEnum.Custom; }
            return Pipeline.OutModeEnum.Program;
        }

        void TxtFile_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; }
            else { e.Effect = DragDropEffects.None; }
        }

        void TxtFile_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            string f;
            if (files == null || files.Length == 0) { return; }
            f = files[0].Trim();
            if (File.Exists(f) || Directory.Exists(f)) { txtFile.Text = f; }
            else { UiLog("拖入路径无效: " + f); }
        }

        void BtnBrowse_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择视频或字幕文件";
            dlg.Filter = "视频|*.mkv;*.mp4;*.avi;*.ts;*.m2ts;*.mov|音频|*.wav;*.mp3;*.flac;*.m4a|字幕|*.srt|所有|*.*";
            if (_cfg.LastDir != "" && Directory.Exists(_cfg.LastDir)) { dlg.InitialDirectory = _cfg.LastDir; }
            if (dlg.ShowDialog(this) == DialogResult.OK) { txtFile.Text = dlg.FileName; }
        }

        void BtnProbe_Click(object sender, EventArgs e)
        {
            string f = txtFile.Text.Trim();
            if (f == "" || !File.Exists(f)) { UiLog("错误: 请先选择存在的文件"); return; }
            if (IsSubtitleFile(f)) { UiLog("字幕文件无需解析, 直接选源语言翻译"); return; }
            ProbeTracks(f);
        }

        // 探测音轨: ffprobe csv -> 填识别语言下拉
        void ProbeTracks(string f)
        {
            UiStatus("解析音轨中...");
            string ffprobe = FindFfprobe();
            if (ffprobe == null) { UiLog("错误: 未找到 ffprobe.exe (在 ffmpeg 目录)"); UiStatus(""); return; }
            ProcessStartInfo psi = new ProcessStartInfo(ffprobe,
                "-v error -select_streams a -show_entries stream=index,codec_name,channels:stream_tags=language,title -of csv=p=0 \"" + f + "\"");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;   // ffprobe 输出 UTF-8
            psi.StandardErrorEncoding = Encoding.UTF8;
            try
            {
                Process p = Process.Start(psi);
                string outTxt = p.StandardOutput.ReadToEnd();
                string errTxt = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) { UiLog("ffprobe 失败: " + errTxt); UiStatus(""); return; }

                cmbTrack.Items.Clear();
                string[] rows = outTxt.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string row in rows)
                {
                    string[] c = row.Split(',');
                    if (c.Length < 3) { continue; }
                    string idx = c[0], codec = c[1], ch = c[2];
                    string lang = (c.Length > 3) ? c[3] : "";
                    string title = (c.Length > 4) ? c[4] : "";
                    string langName = LangName(lang);
                    cmbTrack.Items.Add(new ItemTag(
                        "音轨#" + idx + " " + codec + " " + ch + "ch " + (lang == "" ? "(无标签)" : langName),
                        idx + "|" + lang));
                }
                if (cmbTrack.Items.Count == 0) { UiLog("未探测到音轨"); }
                else
                {
                    cmbTrack.SelectedIndex = 0;
                    // 音轨带语言标签且识别语言还是默认 auto 时, 自动回填
                    if (cmbLang.Text == "自动识别 (auto)")
                    {
                        ItemTag first = cmbTrack.Items[0] as ItemTag;
                        if (first != null && first.Tag != "")
                        {
                            string[] parts = first.Tag.Split('|');
                            if (parts.Length == 2 && parts[1] != "" && parts[1] != "und")
                            {
                                // MKV/MP4 音轨语言可能是 ISO 639-2 三字母 (kor/jpn/chi...),
                                // whisper 只认两字母, 回填前必须归一化
                                SetRecogLang(SrtUtil.ToWhisperLang(parts[1]));
                            }
                        }
                    }
                    UiLog("解析完成: " + cmbTrack.Items.Count + " 条音轨");
                }
            }
            catch (Exception ex) { UiLog("探测异常: " + ex.Message); }
            UiStatus("");
        }

        string LangName(string code)
        {
            switch (code)
            {
                case "zh": return "中文";
                case "en": return "英文";
                case "ja": return "日文";
                case "ko": return "韩文";
                case "und": return "未知";
                default: return code;
            }
        }

        void ChkAuto_Checked(object sender, EventArgs e)
        {
            cmbLang.Enabled = !chkAuto.Checked;
            if (chkAuto.Checked) { SetRecogLang("auto"); }
        }

        // 接口类型 -> API Key 框显隐 (OpenAI 兼容才显示)
        void CmbLlmType_Changed(object sender, EventArgs e)
        {
            ItemTag it = cmbLlmType.SelectedItem as ItemTag;
            string tag = (it != null) ? it.Tag : "";
            bool isOpenai = (tag == "openai");
            _lblKey.Visible = isOpenai;
            txtApiKey.Visible = isOpenai;
        }

        // LLM 后端单选联动: 内嵌引擎 -> 接口相关控件全部灰掉 (固定内置 qwen3.5-mt:2b);
        // 接口 -> 可编辑 (Ollama/OpenAI 兼容)
        void RbLlmBackend_Changed(object sender, EventArgs e)
        {
            bool embedded = rbEmbedded.Checked;
            cmbLlmType.Enabled = !embedded;
            txtUrl.Enabled = !embedded;
            btnProbeLlm.Enabled = !embedded;
            cmbLlmModel.Enabled = !embedded;
            _lblKey.Enabled = !embedded;
            txtApiKey.Enabled = !embedded;
            if (embedded)
            {
                cmbLlmModel.Text = "qwen3.5-mt:2b";   // 内嵌引擎固定模型
                _lblKey.Visible = false;
                txtApiKey.Visible = false;
            }
            else
            {
                CmbLlmType_Changed(null, EventArgs.Empty);   // 恢复 API Key 显隐
            }
        }

        void RbMode_Changed(object sender, EventArgs e)
        {
            // 占位: 模式切换联动 (语言联动在下面处理)
        }

        void BtnProbeLlm_Click(object sender, EventArgs e)
        {
            string url = txtUrl.Text.Trim();
            if (!rbEmbedded.Checked && url == "") { UiLog("错误: 请先填写 LLM 地址"); return; }
            ItemTag lt = cmbLlmType.SelectedItem as ItemTag;
            string type = (lt != null) ? lt.Tag : "ollama";
            if (rbEmbedded.Checked) { type = "llama"; }
            UiStatus("探测模型列表...");
            Thread t = new Thread(delegate()
            {
                // 内嵌引擎: 先拉起本地 ollama serve (内嵌 qwen3.5-mt:2b), 再按 ollama 协议探测; 失败重试
                if (type == "llama")
                {
                    int tries = 0;
                    const int maxTries = 3;          // 首次 + 2 次重试
                    while (true)
                    {
                        try
                        {
                            if (tries == 0) { UiLog("启动内嵌引擎..."); }
                            else { UiLog("内嵌引擎未就绪, 第 " + tries + " 次重试..."); }
                            LlmServer server = new LlmServer();
                            string model;
                            string u = server.EnsureRunning(_cfg.LlmPort, _runnerUi, delegate(string s) { UiLog(s); }, null, out model);
                            _probeServer = server;
                            ProbeLlmModels("ollama", u);
                            return;
                        }
                        catch (Exception ex)
                        {
                            tries++;
                            if (tries >= maxTries)
                            {
                                UiLog("探测失败: " + ex.Message + " (可手动填写模型名)");
                                UiStatus("");
                                return;
                            }
                            UiLog("探测未就绪: " + ex.Message + "  稍后重试...");
                            Thread.Sleep(3000);
                        }
                    }
                }
                ProbeLlmModels(type, url);
            });
            t.IsBackground = true;
            t.Start();
        }

        void ProbeLlmModels(string type, string url)
        {
            try
            {
                string json = "";
                using (WebClient wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    if (type == "ollama")
                    {
                        json = wc.DownloadString(url.TrimEnd('/') + "/api/tags");
                    }
                    else
                    {
                        if (type == "openai" && txtApiKey.Text.Trim() != "") { wc.Headers.Add("Authorization", "Bearer " + txtApiKey.Text.Trim()); }
                        json = wc.DownloadString(url.TrimEnd('/') + "/v1/models");
                    }
                }
                List<string> names = new List<string>();
                foreach (Match m in Regex.Matches(json, "\"(?:name|id)\":\"([^\"]+)\""))
                {
                    names.Add(m.Groups[1].Value);
                }
                if (names.Count == 0) { UiLog("探测无结果, 请手动填写模型名"); }
                else
                {
                    UiInvoke(delegate
                    {
                        cmbLlmModel.Items.Clear();
                        foreach (string n in names) { cmbLlmModel.Items.Add(n); }
                        cmbLlmModel.Text = names[0];
                        UiLog("探测到 " + names.Count + " 个模型: " + string.Join(", ", names.ToArray()));
                    });
                }
            }
            catch (Exception ex) { UiLog("探测失败: " + ex.Message + " (可手动填写模型名)"); }
            UiStatus("");
        }

        // ============================================================ 开始/停止
        void BtnStart_Click(object sender, EventArgs e)
        {
            if (_pipe.Running) { return; }
            SaveUiToCfg();
            _cfg.Save();

            string input = txtFile.Text.Trim();
            if (input == "" || !File.Exists(input)) { UiLog("错误: 请先选择输入文件"); return; }

            if (_modeSubtitle)
            {
                // 字幕直翻: 直接把字幕送 LLM
                StartSubtitleDirect(input);
                return;
            }

            // 收集参数
            ItemTag vt = cmbVad.SelectedItem as ItemTag;
            _pipe.VadPreset = (vt != null) ? vt.Tag : "standard";
            _pipe.Parallel = (int)numParallel.Value;
            ItemTag txtItem = cmbTxtOut.SelectedItem as ItemTag;
            _pipe.TxtOut = (txtItem != null) ? int.Parse(txtItem.Tag) : 0;
            _pipe.TxtParaGap = (double)numParaGap.Value;
            ItemTag wt = cmbWhisper.SelectedItem as ItemTag;
            _pipe.WhisperModel = (wt != null && wt.Tag != "") ? wt.Tag : "models\\ggml-medium.bin";

            // 分离模型 (models\*.onnx 全路径)
            ItemTag st = cmbSepModel.SelectedItem as ItemTag;
            if (st != null && st.Tag != "")
            {
                if (!File.Exists(st.Tag)) { UiLog("错误: 未找到分离模型: " + st.Tag); return; }
                _pipe.SepModel = st.Tag;
            }
            else { _pipe.SepModel = ""; }
            ItemTag ot = cmbOverlap.SelectedItem as ItemTag;
            _pipe.Overlap = (ot != null) ? double.Parse(ot.Tag) : 0.25;
            _pipe.SaveVocals = chkSaveVocals.Checked;
            _pipe.SaveInst = chkSaveInst.Checked;
            _pipe.BreakLine = chkBreakLine.Checked;
            _pipe.TradToSimp = chkTradToSimp.Checked;

            // 识别语言: chkAuto 强制 auto, 否则用识别语言下拉 (下拉/手输)
            _pipe.Lang = RecogLangCode();
            if (_pipe.Lang == "") { _pipe.Lang = "auto"; }
            // 目标语言: 翻译成什么 (默认中文)
            _pipe.TgtLang = TgtLangCode();
            if (_pipe.TgtLang == "") { _pipe.TgtLang = "zh"; }
            // 分离/VAD/GPU 可选
            _pipe.UseSeparate = chkSep.Checked;
            _pipe.EnableVad = chkVad.Checked;
            ItemTag gt = cmbGpu.SelectedItem as ItemTag;
            int gpuIdx = -1;
            if (gt != null) { int.TryParse(gt.Tag, out gpuIdx); }
            _pipe.WhisperGpuIndex = gpuIdx;
            // 音轨序号 (音轨选择与自动识别无关)
            _pipe.TrackIndex = -1;
            if (cmbTrack.SelectedItem != null)
            {
                ItemTag tt = cmbTrack.SelectedItem as ItemTag;
                if (tt != null && tt.Tag != "")
                {
                    string[] parts = tt.Tag.Split('|');
                    int idx; if (parts.Length >= 1 && int.TryParse(parts[0], out idx)) { _pipe.TrackIndex = idx; }
                }
            }

            // LLM (内嵌引擎固定 qwen3.5-mt:2b + 本地端口; 接口走外部地址)
            ItemTag lt = cmbLlmType.SelectedItem as ItemTag;
            _pipe.LlmType = rbEmbedded.Checked ? "llama" : ((lt != null) ? lt.Tag : "ollama");
            _pipe.LlmUrl = rbEmbedded.Checked ? ("http://127.0.0.1:" + _cfg.LlmPort) : txtUrl.Text.Trim();
            _pipe.LlmModel = rbEmbedded.Checked ? "qwen3.5-mt:2b" : cmbLlmModel.Text.Trim();
            if (_pipe.LlmModel == "") { _pipe.LlmModel = "qwen3-14b:latest"; }
            _pipe.LlmKey = txtApiKey.Text.Trim();
            _pipe.LlmPort = _cfg.LlmPort;
            _pipe.LlmNgl = _cfg.LlmNgl;
            _pipe.BatchSize = (int)numBatch.Value;
            _pipe.LlmTimeoutSec = ParseComboInt(cmbTimeout, 60);
            _pipe.LlmRetries = ParseComboInt(cmbRetry, 4);
            _pipe.Mode = rbNone.Checked ? "none" : (rbTranslate.Checked ? "translate" : "refine");
            // 语言联动: 源=目标=中文 -> 纠错; 其余 -> 翻译 (选"不处理"时不覆盖; 源 auto 不联动)
            if (_pipe.Mode != "none")
            {
                if (_pipe.Lang == "zh" && _pipe.TgtLang == "zh") { _pipe.Mode = "refine"; rbRefine.Checked = true; }
                else if (_pipe.Lang != "auto") { _pipe.Mode = "translate"; rbTranslate.Checked = true; }
            }
            _pipe.Input = input;
            _pipe.OutMode = OutModeFromUi();
            _pipe.CustomOutDir = _cfg.CustomOutDir;

            UiLog("开始处理: " + input);
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            _pipe.Start();
        }

        // 字幕直翻: 输入 srt -> 复制为 raw.srt -> 只跑 LLM 步骤
        void StartSubtitleDirect(string srtFile)
        {
            _pipe.Input = srtFile;
            _pipe.UseSeparate = false;
            _pipe.SubtitleDirect = true;
            _pipe.OutMode = OutModeFromUi();
            _pipe.CustomOutDir = _cfg.CustomOutDir;

            ItemTag lt = cmbLlmType.SelectedItem as ItemTag;
            _pipe.LlmType = rbEmbedded.Checked ? "llama" : ((lt != null) ? lt.Tag : "ollama");
            _pipe.LlmUrl = rbEmbedded.Checked ? ("http://127.0.0.1:" + _cfg.LlmPort) : txtUrl.Text.Trim();
            _pipe.LlmModel = rbEmbedded.Checked ? "qwen3.5-mt:2b" : cmbLlmModel.Text.Trim();
            if (_pipe.LlmModel == "") { _pipe.LlmModel = "qwen3-14b:latest"; }
            _pipe.LlmKey = txtApiKey.Text.Trim();
            _pipe.LlmPort = _cfg.LlmPort;
            _pipe.LlmNgl = _cfg.LlmNgl;
            _pipe.BatchSize = (int)numBatch.Value;
            _pipe.LlmTimeoutSec = ParseComboInt(cmbTimeout, 60);
            _pipe.LlmRetries = ParseComboInt(cmbRetry, 4);
            _pipe.Mode = "translate";   // 直翻固定翻译
            _pipe.Lang = RecogLangCode();
            if (_pipe.Lang == "" || _pipe.Lang == "auto") { _pipe.Lang = "en"; }
            _pipe.TgtLang = TgtLangCode();
            if (_pipe.TgtLang == "") { _pipe.TgtLang = "zh"; }
            ItemTag tt = cmbTxtOut.SelectedItem as ItemTag;
            _pipe.TxtOut = (tt != null) ? int.Parse(tt.Tag) : 0;
            _pipe.TxtParaGap = (double)numParaGap.Value;

            UiLog("字幕直翻: " + srtFile + "  (源语言 " + _pipe.Lang + ")");
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            _pipe.Start();
        }

        void BtnStop_Click(object sender, EventArgs e)
        {
            if (_pipe.Running) { _pipe.Stop(); }
        }

        // ============================================================ 工具
        string FindFfprobe()
        {
            try
            {
                foreach (string f in Directory.GetFiles(_root, "ffprobe.exe", SearchOption.AllDirectories))
                {
                    return f;
                }
            }
            catch { }
            return null;
        }

        // ============================================================ UI 线程安全
        void UiInvoke(Action a)
        {
            if (InvokeRequired) { try { Invoke(a); } catch { } }
            else { a(); }
        }

        void UiLog(string msg)
        {
            UiInvoke(delegate
            {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
                txtLog.AppendText(line + "\r\n");
            });
        }

        // 日志: 界面显示 + 落盘到 <任务目录>\<电影名>.log (带时间戳)
        void UiLogFile(string msg)
        {
            UiLog(msg);
            lock (_logLock)
            {
                try
                {
                    if (_logFile == null)
                    {
                        if (string.IsNullOrEmpty(_pipe.LogPath)) { return; }
                        string dir = Path.GetDirectoryName(_pipe.LogPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
                        _logFile = new StreamWriter(new FileStream(_pipe.LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
                        _logFile.AutoFlush = true;
                    }
                    _logFile.WriteLine(string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), msg));
                }
                catch { }
            }
        }

        void UiStatus(string s)
        {
            UiInvoke(delegate { lblStatus.Text = s; });
        }

        void UiStep(Step s)
        {
            string[] names = { "就绪", "探测", "提取", "分离", "识别", "LLM" };
            int n = (int)s;
            string label = (n >= 1 && n <= 5) ? ("步骤 " + n + "/5 " + names[n]) : "";
            UiStatus(label);
        }

        void UiFinished(string result)
        {
            UiInvoke(delegate
            {
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                if (result == "ok") { lblStatus.Text = "完成 ✔ 全部步骤已跑完"; UiLog("完成 ✔"); }
                else if (result == "stopped") { lblStatus.Text = "已停止 (下次开始跳过已完成步骤)"; UiLog("已停止"); }
                else { lblStatus.Text = result; UiLog("失败: " + result); }
            });
        }

        // 关闭时保存配置 + 停止管线 + 回收探测引擎 + 释放日志
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveUiToCfg();
            _cfg.Save();
            if (_pipe.Running) { _pipe.Stop(); }
            try
            {
                if (_probeServer != null) { _probeServer.Stop(); _probeServer = null; }
                if (_runnerUi != null) { _runnerUi.Dispose(); _runnerUi = null; }
            }
            catch { }
            try
            {
                if (_logFile != null) { _logFile.Flush(); _logFile.Dispose(); _logFile = null; }
            }
            catch { }
            base.OnFormClosing(e);
        }
    }
}
