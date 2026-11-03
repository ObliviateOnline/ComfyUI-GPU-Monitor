using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class MonitorPanel : Form
{
    // ---- HALOFLUX embed -------------------------------------------------
    private const int BridgePort = 8850;
    private const string BridgeUrl = "http://127.0.0.1:8850/lab.html";
    private readonly WebView2 web = new WebView2();
    private readonly Label webStatus = new Label();
    private readonly Label bridgeStatus = new Label();
    private readonly Button reloadButton = new Button();
    private readonly Button browserButton = new Button();
    private readonly System.Windows.Forms.Timer supervisor = new System.Windows.Forms.Timer();
    private Process bridgeProcess;
    private bool webReady;

    private readonly string appRoot = AppDomain.CurrentDomain.BaseDirectory;
    private readonly string comfyRoot = Environment.GetEnvironmentVariable("COMFYUI_ROOT") ?? @"D:\ComfyUI\ComfyUI";
    private readonly string monitorScript;
    private readonly string monitorLog;
    private readonly string controlRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComfyUiGpuWatch");
    private readonly StatCard monitorCard = new StatCard();
    private readonly StatCard gpuCard = new StatCard();
    private readonly StatCard comfyCard = new StatCard();
    private readonly Label refreshStatus = new Label();
    private readonly Label logLine = new Label();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private bool refreshBusy;

    private string PauseFile { get { return Path.Combine(controlRoot, "pause.flag"); } }
    private string StopFile { get { return Path.Combine(controlRoot, "stop.flag"); } }
    private string DisabledFile { get { return Path.Combine(controlRoot, "disabled.flag"); } }

    public MonitorPanel()
    {
        monitorScript = Path.Combine(appRoot, "gpu_comfy_watch.ps1");
        monitorLog = Path.Combine(comfyRoot, "logs", "gpu_comfy_watch.log");
        Text = "光环守护 · HALO GUARD";
        StartPosition = FormStartPosition.CenterScreen;
        // never open larger than the screen. ClientSize, not Size: control
        // coordinates are relative to the client area.
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        int formW = Math.Min(1440, work.Width - 40);
        int formH = Math.Min(880, work.Height - 80);
        ClientSize = new Size(formW, formH);
        MinimumSize = new Size(Math.Min(960, formW), Math.Min(660, formH));
        const int bodyTop = 226;
        const int bottomRow = 40;
        int bodyH = formH - bodyTop - 70;

        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(8, 9, 11);
        ForeColor = Color.FromArgb(207, 210, 216);
        // title bar / taskbar icon comes from the exe itself (embedded by csc /win32icon)
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { Icon = SystemIcons.Information; }

        Label title = new Label();
        title.Text = "光环守护";
        title.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(244, 245, 247);
        title.BackColor = Color.Transparent;
        title.Location = new Point(20, 8);
        title.Size = new Size(420, 38);
        Controls.Add(title);

        Label sub = new Label();
        sub.Text = "HALO GUARD  ·  LOCAL GPU / COMFYUI WATCHDOG  ·  127.0.0.1:" + BridgePort;
        sub.Font = new Font("Consolas", 8.25F);
        sub.ForeColor = Color.FromArgb(105, 110, 120);
        sub.BackColor = Color.Transparent;
        sub.Location = new Point(22, 48);
        sub.Size = new Size(760, 16);
        Controls.Add(sub);

        Panel rule = new Panel();
        rule.Location = new Point(22, 70);
        rule.Size = new Size(formW - 44, 2);
        rule.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        rule.Paint += delegate(object sender, PaintEventArgs pe)
        {
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, rule.Width), 2),
                Color.FromArgb(152, 224, 207), Color.FromArgb(0, 152, 224, 207), 0f))
                pe.Graphics.FillRectangle(lg, 0, 0, rule.Width, 2);
        };
        Controls.Add(rule);

        int colGap = 10;
        int cardW = Math.Max(150, (formW - 44 - colGap * 2) / 3);
        monitorCard.Location = new Point(22, 82);
        monitorCard.Size = new Size(cardW, 58);
        monitorCard.Caption = "监控状态 / WATCHDOG";
        monitorCard.Value = "检查中…";
        Controls.Add(monitorCard);

        gpuCard.Location = new Point(22 + cardW + colGap, 82);
        gpuCard.Size = new Size(cardW, 58);
        gpuCard.Caption = "NVIDIA GPU";
        gpuCard.Value = "探测中…";
        Controls.Add(gpuCard);

        comfyCard.Location = new Point(22 + (cardW + colGap) * 2, 82);
        comfyCard.Size = new Size(cardW, 58);
        comfyCard.Caption = "COMFYUI";
        comfyCard.Value = "探测中…";
        Controls.Add(comfyCard);

        AddButton("启用 / 继续", 22, 116, StartMonitoring);
        AddButton("暂停", 148, 90, PauseMonitoring);
        AddButton("停止并禁用自动启动", 248, 168, StopMonitoring);
        AddButton("取消待执行的重启", 426, 164, CancelRestart);
        AddButton("打开监控日志", 600, 130, OpenLog);

        Label policy = new Label();
        policy.Text = "自动重启需有新的 GPU/显示故障证据，并确认 ComfyUI 故障前运行过；先等待恢复。";
        policy.Location = new Point(22, 190);
        policy.Size = new Size(780, 18);
        policy.ForeColor = Color.FromArgb(105, 110, 120);
        policy.BackColor = Color.Transparent;
        policy.Font = new Font("Microsoft YaHei UI", 8F);
        Controls.Add(policy);

        bridgeStatus.Location = new Point(22, 208);
        bridgeStatus.Size = new Size(620, 18);
        bridgeStatus.ForeColor = Color.FromArgb(105, 110, 120);
        bridgeStatus.BackColor = Color.Transparent;
        bridgeStatus.Font = new Font("Consolas", 8.25F);
        bridgeStatus.Text = "● 数据桥 检查中…";
        Controls.Add(bridgeStatus);

        // the watchdog log is mostly "watchdog started" noise - one line is enough,
        // the button below still opens the full file
        logLine.Location = new Point(22, formH - 64);
        logLine.Size = new Size(formW - 44, 18);
        logLine.ForeColor = Color.Gray;
        logLine.Font = new Font("Microsoft YaHei UI", 8F);
        logLine.AutoEllipsis = true;
        logLine.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        logLine.Text = "最近日志：—";
        Controls.Add(logLine);

        web.Location = new Point(22, bodyTop);
        web.Size = new Size(formW - 44, bodyH);
        web.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(web);

        webStatus.Location = web.Location;
        webStatus.Size = web.Size;
        webStatus.Anchor = web.Anchor;
        webStatus.BackColor = Color.FromArgb(16, 18, 22);
        webStatus.ForeColor = Color.Gainsboro;
        webStatus.TextAlign = ContentAlignment.MiddleCenter;
        webStatus.Font = new Font("Microsoft YaHei UI", 10F);
        webStatus.Text = "正在启动内嵌浏览器…";
        Controls.Add(webStatus);
        webStatus.BringToFront();

        refreshStatus.Location = new Point(22, formH - 36);
        refreshStatus.Size = new Size(300, 22);
        refreshStatus.ForeColor = Color.Gray;
        refreshStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(refreshStatus);

        reloadButton.Text = "重新加载画面";
        reloadButton.Location = new Point(330, formH - bottomRow);
        reloadButton.Size = new Size(120, 28);
        reloadButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        reloadButton.Click += delegate { ReloadWeb(); };
        Controls.Add(reloadButton);

        browserButton.Text = "用浏览器打开";
        browserButton.Location = new Point(458, formH - bottomRow);
        browserButton.Size = new Size(120, 28);
        browserButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        browserButton.Click += delegate { OpenInBrowser(); };
        Controls.Add(browserButton);

        timer.Interval = 5000;
        timer.Tick += delegate { RefreshStatus(); };

        supervisor.Interval = 3000;
        supervisor.Tick += delegate { SuperviseBridge(); };

        Shown += delegate
        {
            RefreshStatus();
            timer.Start();
            SuperviseBridge();
            supervisor.Start();
            StartWeb();
        };

        FormClosed += delegate
        {
            timer.Stop();
            supervisor.Stop();
            try { if (bridgeProcess != null && !bridgeProcess.HasExited) bridgeProcess.Kill(); }
            catch { }
        };
    }

    private void AddButton(string text, int x, int width, Action action)
    {
        TechButton button = new TechButton();
        button.Text = text;
        button.Location = new Point(x, 150);
        button.Size = new Size(width, 30);
        button.Click += delegate { action(); RefreshStatus(); };
        Controls.Add(button);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // dark title bar (Win10 1809+ uses 20, the RTM builds used 19)
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, 4);
        }
        catch { }
    }

    // dark grid backdrop instead of a flat white form
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (SolidBrush bg = new SolidBrush(Color.FromArgb(8, 9, 11)))
            g.FillRectangle(bg, ClientRectangle);
        using (Pen pen = new Pen(Color.FromArgb(9, 255, 255, 255)))
        {
            for (int x = 0; x < Width; x += 32) g.DrawLine(pen, x, 0, x, Height);
            for (int y = 0; y < Height; y += 32) g.DrawLine(pen, 0, y, Width, y);
        }
    }

    // nvidia-smi -L gives "GPU 0: NVIDIA GeForce RTX 5070 Ti (UUID: GPU-...)" - keep the name
    private static string ShortGpu(string raw)
    {
        if (String.IsNullOrEmpty(raw)) return "—";
        string s = raw;
        int colon = s.IndexOf(": ");
        if (colon >= 0) s = s.Substring(colon + 2);
        int paren = s.IndexOf(" (");
        if (paren > 0) s = s.Substring(0, paren);
        s = s.Trim();
        return s.Length > 34 ? s.Substring(0, 34) + "…" : s;
    }

    // ================= HALOFLUX embed =====================================

    private void SuperviseBridge()
    {
        bool healthy;
        try { healthy = BridgeHealthy(); }
        catch { healthy = false; }

        if (!healthy)
        {
            bridgeStatus.ForeColor = Color.DarkOrange;
            bridgeStatus.Text = "数据桥：未响应，正在启动…";
            StartBridge();
            return;
        }

        if (bridgeProcess != null && bridgeProcess.HasExited)
        {
            bridgeStatus.ForeColor = Color.Firebrick;
            bridgeStatus.Text = "数据桥：已退出（退出码 " + bridgeProcess.ExitCode + "），正在重试…";
            bridgeProcess = null;
        }
        else
        {
            bridgeStatus.ForeColor = Color.ForestGreen;
            bridgeStatus.Text = "数据桥：运行中 · 127.0.0.1:" + BridgePort;
        }
        if (!webReady) StartWeb();
    }

    private void StartBridge()
    {
        if (bridgeProcess != null && !bridgeProcess.HasExited) return;

        string python = FindPython();
        string script = Path.Combine(appRoot, @"haloflux\bridge.py");
        if (python == null)
        {
            bridgeStatus.ForeColor = Color.Firebrick;
            bridgeStatus.Text = "数据桥：找不到 python.exe";
            return;
        }
        if (!File.Exists(script))
        {
            bridgeStatus.ForeColor = Color.Firebrick;
            bridgeStatus.Text = "数据桥：缺少 " + script;
            return;
        }

        try
        {
            ProcessStartInfo info = new ProcessStartInfo(python, "\"" + script + "\" --port " + BridgePort);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.WorkingDirectory = Path.GetDirectoryName(script);
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            bridgeProcess = new Process();
            bridgeProcess.StartInfo = info;
            bridgeProcess.EnableRaisingEvents = true;
            // drain the pipes so a chatty child can never block on a full buffer
            bridgeProcess.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { };
            bridgeProcess.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
            bridgeProcess.Start();
            bridgeProcess.BeginOutputReadLine();
            bridgeProcess.BeginErrorReadLine();

            bridgeStatus.ForeColor = Color.DarkOrange;
            bridgeStatus.Text = "数据桥：正在启动（" + Path.GetFileName(python) + "）…";
        }
        catch (Exception ex)
        {
            bridgeStatus.ForeColor = Color.Firebrick;
            bridgeStatus.Text = "数据桥启动失败：" + ex.Message;
        }
    }

    private static bool BridgeHealthy()
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + BridgePort + "/api/health");
            request.Timeout = 700;
            request.ReadWriteTimeout = 700;
            request.Proxy = null;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                return (int)response.StatusCode == 200;
        }
        catch { return false; }
    }

    private static string FindPython()
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        string[] fixedPaths = new string[]
        {
            @"C:\Python314\python.exe",
            @"C:\Python313\python.exe",
            @"C:\Python312\python.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Python\Python312\python.exe")
        };
        foreach (string candidate in fixedPaths)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private async void StartWeb()
    {
        if (webReady) return;
        try
        {
            string dataDir = Path.Combine(controlRoot, "WebView2");
            Directory.CreateDirectory(dataDir);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, dataDir, null);
            await web.EnsureCoreWebView2Async(environment);

            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webReady = true;
            web.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
            {
                Trace("process failed: " + e.ProcessFailedKind + " / " + e.Reason);
            };
            web.CoreWebView2.Navigate(BridgeUrl);

            webStatus.Visible = false;
        }
        catch (Exception ex)
        {
            webStatus.Text = "内嵌浏览器启动失败，光环无法显示。\r\n\r\n"
                + ex.Message
                + "\r\n\r\n可点「用浏览器打开」在默认浏览器里看；"
                + "\r\n或安装 Microsoft Edge WebView2 Runtime 后重启本程序。";
            webStatus.ForeColor = Color.MistyRose;
            webStatus.BringToFront();
            webStatus.Visible = true;
        }
    }

    private void ReloadWeb()
    {
        try
        {
            if (webReady && web.CoreWebView2 != null) web.CoreWebView2.Reload();
            else { webReady = false; StartWeb(); }
        }
        catch { }
    }

    private void OpenInBrowser()
    {
        try { Process.Start(BridgeUrl); } catch { }
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"ComfyUiGpuWatch\haloflux.log"),
                DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch { }
    }

    // ================= existing watchdog controls =========================

    private void StartMonitoring()
    {
        Directory.CreateDirectory(controlRoot);
        DeleteMarker(DisabledFile);
        DeleteMarker(StopFile);
        DeleteMarker(PauseFile);
        if (!WatchdogRunning())
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            info.Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + monitorScript + "\"";
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.CreateNoWindow = true;
            Process.Start(info);
        }
    }

    private void PauseMonitoring()
    {
        Directory.CreateDirectory(controlRoot);
        DeleteMarker(DisabledFile);
        DeleteMarker(StopFile);
        File.WriteAllText(PauseFile, "paused");
    }

    private void StopMonitoring()
    {
        Directory.CreateDirectory(controlRoot);
        DeleteMarker(DisabledFile);
        File.WriteAllText(DisabledFile, "disabled");
        File.WriteAllText(StopFile, "stop");
    }

    private void CancelRestart()
    {
        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        info.Arguments = "/a";
        info.CreateNoWindow = true;
        info.UseShellExecute = false;
        Process.Start(info);
    }

    private void OpenLog()
    {
        if (File.Exists(monitorLog))
            Process.Start("notepad.exe", "\"" + monitorLog + "\"");
    }

    private static void DeleteMarker(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool WatchdogRunning()
    {
        try
        {
            using (Mutex mutex = Mutex.OpenExisting(@"Local\ComfyUiGpuWatch"))
                return true;
        }
        catch { return false; }
    }

    private void RefreshStatus()
    {
        if (refreshBusy) return;
        refreshBusy = true;
        Task.Run(delegate { return ReadSnapshot(); }).ContinueWith(delegate(Task<Snapshot> task)
        {
            if (IsDisposed) { refreshBusy = false; return; }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (!IsDisposed && task.Status == TaskStatus.RanToCompletion) ApplySnapshot(task.Result);
                    refreshBusy = false;
                });
            }
            catch { refreshBusy = false; }
        });
    }

    private Snapshot ReadSnapshot()
    {
        Snapshot s = new Snapshot();
        s.Watchdog = WatchdogRunning();
        s.Disabled = File.Exists(DisabledFile);
        s.Paused = File.Exists(PauseFile);
        s.Gpu = ProbeGpu();
        s.Comfy = ProbeComfy();
        s.Log = ReadRecentLog();
        s.Time = DateTime.Now.ToString("HH:mm:ss");
        return s;
    }

    private static string ProbeGpu()
    {
        string exe = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe";
        if (!File.Exists(exe))
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            exe = path.Split(Path.PathSeparator)
                .Select(p => Path.Combine(p.Trim(), "nvidia-smi.exe"))
                .FirstOrDefault(File.Exists);
        }
        if (String.IsNullOrEmpty(exe)) return "nvidia-smi 不可用";
        try
        {
            ProcessStartInfo info = new ProcessStartInfo(exe, "-L");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (Process p = Process.Start(info))
            {
                Task<string> stdout = Task.Run(() => p.StandardOutput.ReadToEnd());
                Task<string> stderr = Task.Run(() => p.StandardError.ReadToEnd());
                if (!p.WaitForExit(3500)) { p.Kill(); return "nvidia-smi 超时"; }
                string output = (stdout.Result + " " + stderr.Result).Trim();
                return p.ExitCode == 0 && output.IndexOf("GPU ", StringComparison.OrdinalIgnoreCase) >= 0
                    ? output.Replace(Environment.NewLine, " ").Trim()
                    : "未能识别（" + output + ")";
            }
        }
        catch (Exception ex) { return "探测失败：" + ex.Message; }
    }

    private static string ProbeComfy()
    {
        foreach (int port in new int[] { 8188, 8189 })
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/system_stats");
                request.Timeout = 1300;
                request.ReadWriteTimeout = 1300;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return "服务正常响应（端口 " + port + "）";
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    int code = (int)response.StatusCode;
                    response.Close();
                    if (code < 500) return "服务正在响应（端口 " + port + "）";
                }
            }
            catch { }
        }
        foreach (int port in new int[] { 8188, 8189 })
        {
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
                    if (result.AsyncWaitHandle.WaitOne(250) && client.Connected) return "端口 " + port + " 正在监听，接口未响应";
                }
            }
            catch { }
        }
        return "当前未运行";
    }

    private string ReadRecentLog()
    {
        try
        {
            if (!File.Exists(monitorLog)) return "尚未生成（后台轮询保持安静，故障时才记录）";
            string[] lines = File.ReadAllLines(monitorLog);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = (lines[i] ?? "").Trim();
                if (line.Length == 0) continue;
                return line.Length > 118 ? line.Substring(0, 118) + "…" : line;
            }
            return "（空）";
        }
        catch (Exception ex) { return "无法读取监控记录：" + ex.Message; }
    }

    private void ApplySnapshot(Snapshot s)
    {
        Color ok = Color.FromArgb(120, 210, 150);
        Color warn = Color.FromArgb(214, 150, 60);
        Color bad = Color.FromArgb(200, 84, 96);
        Color idle = Color.FromArgb(107, 111, 120);

        if (s.Disabled) { monitorCard.Value = "已关闭"; monitorCard.Accent = bad; }
        else if (s.Paused) { monitorCard.Value = "已暂停"; monitorCard.Accent = warn; }
        else if (s.Watchdog) { monitorCard.Value = "运行中"; monitorCard.Accent = ok; }
        else { monitorCard.Value = "未运行"; monitorCard.Accent = bad; }

        gpuCard.Value = ShortGpu(s.Gpu);
        gpuCard.Accent = s.Gpu.StartsWith("NVIDIA GeForce", StringComparison.OrdinalIgnoreCase) ||
                         s.Gpu.StartsWith("GPU ", StringComparison.OrdinalIgnoreCase) ? ok : bad;

        bool comfyUp = s.Comfy.Contains("正常响应") || s.Comfy.Contains("正在响应");
        comfyCard.Value = comfyUp ? "在线" : (s.Comfy.Contains("监听") ? "端口监听" : "未运行");
        comfyCard.Accent = comfyUp ? ok : (s.Comfy.Contains("监听") ? warn : idle);

        logLine.Text = "最近日志：" + s.Log;
        refreshStatus.Text = "本机刷新：" + s.Time;
    }

    private sealed class Snapshot
    {
        public bool Watchdog;
        public bool Paused;
        public bool Disabled;
        public string Gpu;
        public string Comfy;
        public string Log;
        public string Time;
    }

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MonitorPanel());
    }
}

// ================= dark "console" widgets ==============================

internal sealed class StatCard : Panel
{
    private string caption = "";
    private string value = "—";
    private Color accent = Color.FromArgb(207, 210, 216);
    private readonly Font capFont = new Font("Microsoft YaHei UI", 8F);
    private readonly Font valFont = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold);

    public string Caption { get { return caption; } set { caption = value ?? ""; Invalidate(); } }
    public string Value { get { return value; } set { this.value = value ?? ""; Invalidate(); } }
    public Color Accent { get { return accent; } set { accent = value; Invalidate(); } }

    public StatCard()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(14, 15, 18);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        using (Pen p = new Pen(Color.FromArgb(37, 40, 46)))
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

        TextRenderer.DrawText(g, caption, capFont, new Rectangle(10, 7, Width - 20, 14),
            Color.FromArgb(105, 110, 120),
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, value, valFont, new Rectangle(10, 25, Width - 20, 24),
            Color.FromArgb(244, 245, 247),
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        using (SolidBrush b = new SolidBrush(accent))
            g.FillRectangle(b, 10, Height - 4, Math.Max(0, Width - 20), 2);
    }
}

internal sealed class TechButton : Button
{
    private bool hot;
    private readonly Color accent = Color.FromArgb(152, 224, 207);

    public TechButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.FromArgb(17, 18, 22);
        ForeColor = Color.FromArgb(207, 210, 216);
        Font = new Font("Microsoft YaHei UI", 9F);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hot = true; Invalidate(); base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hot = false; Invalidate(); base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color bg = hot ? Color.FromArgb(26, 28, 34) : BackColor;
        using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
        using (Pen p = new Pen(hot ? accent : Color.FromArgb(58, 62, 70)))
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        if (hot)
        {
            using (SolidBrush b = new SolidBrush(accent))
                g.FillRectangle(b, 0, Height - 2, Width, 2);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle,
            hot ? Color.FromArgb(244, 245, 247) : ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}