using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MonitorPanel : Form
{
    private readonly string appRoot = AppDomain.CurrentDomain.BaseDirectory;
    private readonly string comfyRoot = Environment.GetEnvironmentVariable("COMFYUI_ROOT") ?? @"D:\ComfyUI\ComfyUI";
    private readonly string monitorScript;
    private readonly string monitorLog;
    private readonly string controlRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComfyUiGpuWatch");
    private readonly Label monitorStatus = new Label();
    private readonly Label gpuStatus = new Label();
    private readonly Label comfyStatus = new Label();
    private readonly Label refreshStatus = new Label();
    private readonly TextBox recentLog = new TextBox();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private bool refreshBusy;

    private string PauseFile { get { return Path.Combine(controlRoot, "pause.flag"); } }
    private string StopFile { get { return Path.Combine(controlRoot, "stop.flag"); } }
    private string DisabledFile { get { return Path.Combine(controlRoot, "disabled.flag"); } }

    public MonitorPanel()
    {
        monitorScript = Path.Combine(appRoot, "gpu_comfy_watch.ps1");
        monitorLog = Path.Combine(comfyRoot, "logs", "gpu_comfy_watch.log");
        Text = "ComfyUI / NVIDIA 本地监管";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(780, 510);
        MinimumSize = new Size(700, 460);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.WhiteSmoke;
        Icon = SystemIcons.Information;

        Label title = new Label();
        title.Text = "ComfyUI 与 NVIDIA GPU";
        title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
        title.Location = new Point(20, 16);
        title.Size = new Size(600, 34);
        Controls.Add(title);

        monitorStatus.Location = new Point(22, 62);
        monitorStatus.Size = new Size(710, 26);
        monitorStatus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        Controls.Add(monitorStatus);

        gpuStatus.Location = new Point(22, 96);
        gpuStatus.Size = new Size(710, 24);
        Controls.Add(gpuStatus);

        comfyStatus.Location = new Point(22, 125);
        comfyStatus.Size = new Size(710, 24);
        Controls.Add(comfyStatus);

        AddButton("启用 / 继续", 22, 116, StartMonitoring);
        AddButton("暂停", 148, 90, PauseMonitoring);
        AddButton("停止并禁用自动启动", 248, 168, StopMonitoring);
        AddButton("取消待执行的重启", 426, 164, CancelRestart);
        AddButton("打开监控日志", 600, 130, OpenLog);

        Label policy = new Label();
        policy.Text = "自动重启需有新的 GPU/显示故障证据，并确认 ComfyUI 故障前运行过；先等待恢复。";
        policy.Location = new Point(22, 207);
        policy.Size = new Size(720, 22);
        policy.ForeColor = Color.SlateGray;
        Controls.Add(policy);

        recentLog.Location = new Point(22, 238);
        recentLog.Size = new Size(716, 188);
        recentLog.Multiline = true;
        recentLog.ReadOnly = true;
        recentLog.ScrollBars = ScrollBars.Vertical;
        recentLog.WordWrap = false;
        recentLog.Font = new Font("Consolas", 8.5F);
        Controls.Add(recentLog);

        refreshStatus.Location = new Point(22, 435);
        refreshStatus.Size = new Size(300, 22);
        refreshStatus.ForeColor = Color.Gray;
        Controls.Add(refreshStatus);

        timer.Interval = 5000;
        timer.Tick += delegate { RefreshStatus(); };
        Shown += delegate { RefreshStatus(); timer.Start(); };
        FormClosed += delegate { timer.Stop(); };
    }

    private void AddButton(string text, int x, int width, Action action)
    {
        Button button = new Button();
        button.Text = text;
        button.Location = new Point(x, 165);
        button.Size = new Size(width, 32);
        button.Click += delegate { action(); RefreshStatus(); };
        Controls.Add(button);
    }

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
            if (!File.Exists(monitorLog)) return "监控日志尚未生成。后台健康轮询保持安静；故障或状态变化时会在此记录。";
            string[] lines = File.ReadAllLines(monitorLog);
            return String.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 18)).ToArray());
        }
        catch (Exception ex) { return "无法读取监控记录：" + ex.Message; }
    }

    private void ApplySnapshot(Snapshot s)
    {
        if (s.Disabled)
        {
            monitorStatus.Text = "监控：已关闭";
            monitorStatus.ForeColor = Color.Firebrick;
        }
        else if (s.Paused)
        {
            monitorStatus.Text = "监控：已暂停";
            monitorStatus.ForeColor = Color.DarkOrange;
        }
        else if (s.Watchdog)
        {
            monitorStatus.Text = "监控：运行中";
            monitorStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            monitorStatus.Text = "监控：未运行";
            monitorStatus.ForeColor = Color.Firebrick;
        }

        gpuStatus.Text = "NVIDIA GPU：" + s.Gpu;
        gpuStatus.ForeColor = s.Gpu.StartsWith("NVIDIA GeForce", StringComparison.OrdinalIgnoreCase) ||
                              s.Gpu.StartsWith("GPU ", StringComparison.OrdinalIgnoreCase)
            ? Color.ForestGreen : Color.Firebrick;

        comfyStatus.Text = "ComfyUI：" + s.Comfy;
        comfyStatus.ForeColor = s.Comfy.Contains("正常响应") || s.Comfy.Contains("正在响应")
            ? Color.ForestGreen : (s.Comfy.Contains("监听") ? Color.DarkOrange : Color.DimGray);

        if (recentLog.Text != s.Log)
        {
            recentLog.Text = s.Log;
            recentLog.SelectionStart = recentLog.TextLength;
            recentLog.ScrollToCaret();
        }
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
