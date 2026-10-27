using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GPTBotManager;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            try { ApplicationConfiguration.Initialize(); LayoutSmokeTest.Run(); SmokeTest.Run().GetAwaiter().GetResult(); Environment.ExitCode = 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "manager-test-result.txt"), ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\GPTBotManager-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory)))[..16], out bool first);
        if (!first) { MessageBox.Show("管理器已经在运行，请查看任务栏。", "GPTBot 管理器"); return; }
        try { using var form = new ManagerForm(); if (args.Contains("--start-all")) form.Shown += async (_, _) => await form.StartFromLaunch(); Application.Run(form); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "管理器启动失败"); }
    }
}

// One Windows job per service. Closing the job ends the complete descendant tree,
// including DSH's wrapper and worker processes, even if the manager crashes.
internal sealed class ProcessJob : IDisposable
{
    IntPtr handle;
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit { public long PerProcess, PerJob; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit { public BasicLimit Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimit data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public ProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var data = new ExtendedLimit(); data.Basic.Flags = 0x2000; // KILL_ON_JOB_CLOSE
        if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref data, (uint)Marshal.SizeOf<ExtendedLimit>()))
        { Dispose(); throw new System.ComponentModel.Win32Exception(); }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle) && !process.HasExited) throw new System.ComponentModel.Win32Exception();
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}

internal sealed class ManagedProcess : IDisposable
{
    public Process Process { get; }
    readonly ProcessJob job;
    public bool Running => !Process.HasExited;
    public ManagedProcess(string file, string args, string cwd, Action<string> output, IReadOnlyDictionary<string, string?>? environment = null)
    {
        job = new ProcessJob();
        Process = new Process { StartInfo = new ProcessStartInfo(file, args) {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        }};
        Process.StartInfo.Environment["NO_COLOR"] = "1";
        if (environment != null) foreach (var (key, value) in environment) {
            if (value == null) Process.StartInfo.Environment.Remove(key);
            else Process.StartInfo.Environment[key] = value;
        }
        Process.OutputDataReceived += (_, e) => { if (e.Data != null) output(e.Data); };
        Process.ErrorDataReceived += (_, e) => { if (e.Data != null) output("[stderr] " + e.Data); };
        try {
            Process.Start(); Process.StartInfo.Environment.Remove("CONTROL_PLANE_API_KEY"); job.Assign(Process);
            Process.BeginOutputReadLine(); Process.BeginErrorReadLine();
        } catch {
            try { if (!Process.HasExited) Process.Kill(true); } catch { }
            job.Dispose(); Process.Dispose(); throw;
        }
    }
    public async Task Stop()
    {
        job.Dispose();
        if (!Process.HasExited) await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // Drain redirected output before disposing or starting another generation.
        Process.WaitForExit();
    }
    public void Dispose() { job.Dispose(); Process.Dispose(); }
}

internal sealed class Service(string id, string name, params int[] ports)
{
    public string Id = id, Name = name;
    public int[] Ports = ports;
    public ManagedProcess? Child;
    public bool Desired, Busy;
    public CancellationTokenSource? StartupCancellation;
    public DateTime RetryAt;
    public int? ExitCode;
    public int RestartAttempts;
    public readonly SemaphoreSlim Gate = new(1);
    public readonly ConcurrentQueue<string> Pending = new();
    public readonly Queue<string> History = new();
    public Label Status = new();
    public Button Start = new(), Stop = new(), Restart = new();
    public bool Running => Child?.Running == true;
    public bool Listening(HashSet<int> ports) => Ports.Any(ports.Contains);
    public void Log(string line)
    {
        line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        if (line.Length > 8000) line = line[..8000];
        Pending.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (Pending.Count > 2000) Pending.TryDequeue(out _);
    }
}

internal sealed class ManagerForm : Form
{
    readonly string root;
    readonly string dotDirectory;
    readonly bool dotOnly;
    readonly string node = Environment.GetEnvironmentVariable("QQB_NODE") ?? "node.exe";
    string snowDir = "", dshCli = "";
    readonly int dshPort = 3080, consolePort = 3100;
    readonly int snowWebPort = int.TryParse(Environment.GetEnvironmentVariable("QQB_SNOWLUMA_WEB_PORT"), out int sp) && sp > 0 && sp <= 65535 ? sp : 5099;
    readonly int dotPort = 3112;
    readonly Service snow, dsh, bridge, dot, tunnel;
    readonly TunnelRuntime tunnelRuntime;
    readonly Service[] services;
    readonly RichTextBox log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(23, 30, 43), ForeColor = Color.FromArgb(218, 228, 242), Font = new Font("Consolas", 10), BorderStyle = BorderStyle.None, WordWrap = false };
    readonly ComboBox filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly CheckBox autoRestart = new() { Text = "服务异常退出后自动重启", Checked = true, AutoSize = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 700 };
    CancellationTokenSource? batch;
    bool closing, allowClose, ticking;

    public ManagerForm()
    {
        root = FindRoot();
        dotDirectory = Environment.GetEnvironmentVariable("QQB_DOT_BACKEND_DIR") ?? FindDotDirectory(root);
        var dotConfig = Path.Combine(dotDirectory, "state", "mcp-config.json");
        if (File.Exists(dotConfig)) {
            using var config = JsonDocument.Parse(File.ReadAllText(dotConfig));
            if (config.RootElement.TryGetProperty("port", out var portValue) && portValue.TryGetInt32(out int p) && p > 0 && p <= 65535) dotPort = p;
        }
        using (var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config.json"))))
        {
            var c = cfg.RootElement;
            dotOnly = c.TryGetProperty("dot", out var backend) && backend.TryGetProperty("only", out var only) && only.ValueKind == JsonValueKind.True;
            if (c.TryGetProperty("snowluma", out var s) && s.TryGetProperty("homeDir", out var h)) snowDir = h.GetString() ?? "";
            if (c.TryGetProperty("dsh", out var d) && d.TryGetProperty("baseUrl", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)) dshPort = uri.Port;
            if (c.TryGetProperty("consolePort", out var p) && p.TryGetInt32(out int port)) consolePort = port;
        }
        snowDir = Environment.GetEnvironmentVariable("QQB_SNOWLUMA_DIR") ?? snowDir;
        if (!File.Exists(Path.Combine(snowDir, "index.mjs"))) {
            var sibling = Directory.GetDirectories(Directory.GetParent(root)!.FullName, "SnowLuma*").FirstOrDefault(p => File.Exists(Path.Combine(p, "index.mjs")));
            if (sibling != null) snowDir = sibling;
        }
        dshCli = Environment.GetEnvironmentVariable("QQB_DSH_CLI") ?? "";
        if (int.TryParse(Environment.GetEnvironmentVariable("QQB_DSH_PORT"), out int dp) && dp > 0 && dp <= 65535) dshPort = dp;
        snow = new("snowluma", "SnowLuma", 3000, 3001, snowWebPort);
        dsh = new("dsh", "DSH", dshPort);
        bridge = new("bridge", "QQ Bridge", consolePort);
        dot = new("dot", "Dot MCP", dotPort);
        tunnel = new("tunnel", "Dot Tunnel");
        tunnelRuntime = new(dotDirectory);
        services = dotOnly ? [snow, bridge, dot, tunnel] : [snow, dsh, bridge, dot, tunnel];
        SuspendLayout();
        // All sizes below are authored at 96 DPI; scale the complete control tree together.
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "GPTBot 管理器"; ClientSize = new Size(1000, 680); MinimumSize = new Size(800, 480);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(243, 246, 251);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        tools.Controls.Add(new Label { Text = "GPTBot  /  运行管理", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(27, 46, 76), Margin = new Padding(3, 0, 16, 0) });
        var all = MakeButton("一键启动", async () => await StartAll());
        var stopAll = MakeButton("全部停止", async () => { batch?.Cancel(); await StopAll(); });
        tools.Controls.AddRange([all, stopAll, autoRestart]);
        tools.Controls.Add(MakeButton("隧道密钥", () => { if (tunnelRuntime.RequestKey(this)) tunnel.Log("已设置隧道密钥（仅当前管理器内存）；下次连接或重连时使用。"); return Task.CompletedTask; }));
        layout.Controls.Add(tools);
        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 6, RowCount = services.Length, BackColor = Color.White, Padding = new Padding(8) };
        rows.ColumnStyles.Add(new(SizeType.AutoSize)); rows.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) rows.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < services.Length; i++) {
            var svc = services[i]; rows.RowStyles.Add(new(SizeType.AutoSize));
            rows.Controls.Add(new Label { Text = svc.Name, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 0, i);
            svc.Status = new Label { AutoSize = true, Anchor = AnchorStyles.Left }; rows.Controls.Add(svc.Status, 1, i);
            svc.Start = MakeButton("启动", async () => await StartService(svc));
            svc.Stop = MakeButton("停止", async () => await StopService(svc));
            svc.Restart = MakeButton(svc == tunnel ? "重连" : "重启", async () => { await StopService(svc); await StartService(svc); });
            rows.Controls.Add(svc.Start, 2, i); rows.Controls.Add(svc.Stop, 3, i); rows.Controls.Add(svc.Restart, 4, i);
            var backend = MakeButton(svc == dot ? "接入状态" : svc == tunnel ? "连接状态" : "管理后台", async () => {
                try { await OpenBackend(svc); }
                catch (Exception e) { svc.Log("打开管理后台失败：" + e.Message); }
            });
            rows.Controls.Add(backend, 5, i);
        }
        layout.Controls.Add(rows);
        var logTools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        filter.Items.Add("全部日志"); filter.Items.AddRange(services.Select(s => (object)s.Name).ToArray()); filter.SelectedIndex = 0;
        filter.SelectedIndexChanged += (_, _) => RepaintLog();
        logTools.Controls.Add(filter);
        logTools.Controls.Add(MakeButton("清空显示", () => { foreach (var s in services) s.History.Clear(); log.Clear(); return Task.CompletedTask; }));
        logTools.Controls.Add(MakeButton("打开日志目录", () => { Process.Start(new ProcessStartInfo("explorer.exe", Quote(Path.Combine(root, "state"))) { UseShellExecute = true }); return Task.CompletedTask; }));
        layout.Controls.Add(logTools); layout.Controls.Add(log);
        layout.Controls.Add(new Label { Text = "关闭程序会停止由本程序启动的服务。外部实例仅显示状态；启动失败原因见下方日志。", AutoSize = true, ForeColor = Color.DimGray });
        ResumeLayout(true);
        snow.Log("使用目录：" + snowDir);
        if (!dotOnly) dsh.Log("使用启动器：" + dshCli);
        else bridge.Log("全部白名单会话使用 Dot 后端；DSH 已从启动流程撤下。");
        dot.Log("本地服务目录：" + dotDirectory + "；连接账号和切换桥接需另行配置。");
        tunnel.Log("由管理器直接运行官方 tunnel-client；密钥仅存内存。关闭管理器也会停止隧道。");
        FormClosing += OnClosing;
        timer.Tick += async (_, _) => await Tick(); timer.Start();
    }
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToWorkingArea(Screen.FromControl(this).WorkingArea);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!e.Cancel) FitToWorkingArea(Screen.FromControl(this).WorkingArea);
    }
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        FitToWorkingArea(Screen.FromControl(this).WorkingArea);
    }
    internal void FitToWorkingArea(Rectangle area)
    {
        var minimum = LogicalToDeviceUnits(new Size(800, 480));
        MinimumSize = new Size(Math.Min(minimum.Width, area.Width), Math.Min(minimum.Height, area.Height));
        if (WindowState != FormWindowState.Normal) return;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
    }
    static string FindRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("QQB_BRIDGE_DIR");
        if (!string.IsNullOrWhiteSpace(explicitRoot)) {
            var resolved = Path.GetFullPath(explicitRoot);
            if (!File.Exists(Path.Combine(resolved, "src", "bridge.js"))) throw new Exception("QQB_BRIDGE_DIR 未指向 QQBridge 源码目录。");
            return resolved;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, "src", "bridge.js"))) return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "qq-bridge", "src", "bridge.js"))) return Path.Combine(dir.FullName, "qq-bridge");
        }
        throw new Exception("找不到 qq-bridge 目录，请将管理器放在 GPTBot 或 qq-bridge 目录中。");
    }
    static string FindDotDirectory(string bridgeRoot)
    {
        bool IsBackend(string directory) {
            try {
                if (!File.Exists(Path.Combine(directory, "scripts", "start-mcp.mjs"))) return false;
                using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
                return package.RootElement.GetProperty("name").GetString() == "qqbridge-dot-backend";
            } catch { return false; }
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (IsBackend(directory.FullName)) return directory.FullName;
        var parent = Directory.GetParent(bridgeRoot)!.FullName;
        var candidates = Directory.GetDirectories(parent).Where(IsBackend).ToArray();
        if (candidates.Length == 1) return candidates[0];
        throw new Exception("请通过 QQB_DOT_BACKEND_DIR 指定 qqbridge-dot-backend 目录。");
    }
    static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
    Button MakeButton(string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(60, 28), Padding = new Padding(6, 0, 6, 0), FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(3, 2, 3, 2), Anchor = AnchorStyles.Left };
        b.Click += async (_, _) => { b.Enabled = false; try { await action(); } catch (Exception e) { bridge.Log("操作失败：" + e.Message); } finally { if (!closing) b.Enabled = true; } };
        return b;
    }
    static HashSet<int> ListeningPorts() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
    async Task StartService(Service s)
    {
        if (closing) return;
        await s.Gate.WaitAsync(); s.Busy = true;
        s.StartupCancellation = new CancellationTokenSource(s == tunnel ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(60));
        try {
            if (closing || s.Running) return;
            if (s == tunnel) {
                var health = await tunnelRuntime.Probe(true);
                if (health.BaseUri != null) { s.Log("检测到外部隧道实例，请在原启动位置停止后再由本管理器接管。"); return; }
                if (!tunnelRuntime.Available) throw new FileNotFoundException("找不到官方 tunnel-client 或 qq-dot 隧道配置，请先按接入指南完成隧道配置。");
                if (!ListeningPorts().Contains(dotPort)) throw new Exception("请先启动 Dot MCP，再连接隧道。");
                if (tunnelRuntime.Key == null && !tunnelRuntime.RequestKey(this)) { s.Desired = false; return; }
            }
            if (s.Listening(ListeningPorts())) { s.Log("端口已被外部实例占用；请先在原启动位置停止该实例。"); return; }
            if (s.Child != null) { await s.Child.Stop(); s.Child.Dispose(); s.Child = null; }
            s.Desired = true; s.ExitCode = null;
            if (s == tunnel) {
                s.Child = new ManagedProcess(tunnelRuntime.Executable, tunnelRuntime.Arguments, dotDirectory,
                    line => tunnelRuntime.RecordOutput(line, tunnel.Log), tunnelRuntime.EnvironmentForChild());
            } else if (s == dot) {
                if (!File.Exists(Path.Combine(dotDirectory, "scripts", "start-mcp.mjs")) || !File.Exists(Path.Combine(dotDirectory, "state", "mcp-config.json"))) throw new FileNotFoundException("Dot MCP 本地服务或配置不存在，请先完成 setup:mcp。");
                s.Child = new ManagedProcess(node, "scripts/start-mcp.mjs", dotDirectory, s.Log);
            } else if (s == snow) {
                if (!File.Exists(Path.Combine(snowDir, "index.mjs"))) throw new FileNotFoundException("找不到 SnowLuma：" + snowDir);
                using var check = new ManagedProcess(node, "check-node-version.cjs", snowDir, s.Log);
                await check.Process.WaitForExitAsync(s.StartupCancellation.Token); check.Process.WaitForExit();
                if (check.Process.ExitCode != 0) throw new Exception("SnowLuma 的 Node 版本检查失败");
                if (closing) return;
                s.Child = new ManagedProcess(node, "index.mjs", snowDir, s.Log);
            } else if (s == dsh) {
                if (!File.Exists(dshCli)) throw new FileNotFoundException("混合模式需要通过 QQB_DSH_CLI 指定 DSH 启动器。");
                Directory.CreateDirectory(Path.Combine(root, "state"));
                File.WriteAllText(Path.Combine(root, "state", "dsh-web.log"), "", Encoding.UTF8);
                s.Child = new ManagedProcess(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /s /c \"" + Quote(dshCli) + " web --no-open --port " + dshPort + "\"", root, line => {
                    s.Log(line);
                    try { lock (dsh) File.AppendAllText(Path.Combine(root, "state", "dsh-web.log"), line + Environment.NewLine, Encoding.UTF8); } catch { }
                });
            } else {
                using var harden = new ManagedProcess(node, "scripts/harden-state-acl.mjs --quiet", root, s.Log);
                await harden.Process.WaitForExitAsync(s.StartupCancellation.Token); harden.Process.WaitForExit();
                if (closing) return;
                s.Child = new ManagedProcess(node, "src/bridge.js", root, s.Log);
            }
            s.Log("已启动，PID " + s.Child.Process.Id);
        } catch (OperationCanceledException) { s.Desired = false; s.Log("启动已取消或检查超时。"); }
        catch (Exception e) { s.Desired = false; s.Log("启动失败：" + e.Message); }
        finally { s.StartupCancellation.Dispose(); s.StartupCancellation = null; s.Busy = false; s.Gate.Release(); }
    }
    async Task StopService(Service s)
    {
        s.Desired = false;
        s.StartupCancellation?.Cancel();
        await s.Gate.WaitAsync(); s.Busy = true;
        try {
            s.Desired = false;
            if (s.Child != null) { await s.Child.Stop(); s.Child.Dispose(); s.Child = null; s.Log("已停止（包含子进程）"); }
        } finally { s.Busy = false; s.Gate.Release(); }
    }
    async Task StopAll() { foreach (var s in services.Reverse()) await StopService(s); }
    public Task StartFromLaunch() => StartAll();
    async Task StartAll()
    {
        if (batch != null) return;
        batch = new CancellationTokenSource(); var token = batch.Token;
        try {
            token.ThrowIfCancellationRequested(); await StartService(snow);
            if (!dotOnly) { token.ThrowIfCancellationRequested(); await StartService(dsh); }
            token.ThrowIfCancellationRequested();
            bridge.Log(dotOnly ? "等待 SnowLuma 端口就绪，再启动 Dot 桥接……" : "等待 SnowLuma 与 DSH 端口就绪，再启动桥接……");
            var until = DateTime.UtcNow.AddSeconds(60);
            while (true) {
                token.ThrowIfCancellationRequested();
                var ports = ListeningPorts(); if (snow.Listening(ports) && (dotOnly || dsh.Listening(ports))) break;
                if (DateTime.UtcNow > until) throw new Exception(dotOnly ? "等待 SnowLuma 超时；请检查日志及 QQ 登录。" : "等待依赖超时；请查看 SnowLuma / DSH 日志，登录 QQ 后再试。");
                await Task.Delay(500, token);
            }
            token.ThrowIfCancellationRequested(); await StartService(bridge);
            token.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(dotDirectory, "state", "mcp-config.json"))) await StartService(dot);
            else if (dotOnly) throw new Exception("Dot MCP 配置缺失，无法启动唯一 AI 后端。");
            if (tunnelRuntime.Available) {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!ListeningPorts().Contains(dotPort)) {
                    token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > deadline) throw new Exception("等待 Dot MCP 启动超时。");
                    await Task.Delay(300, token);
                }
                token.ThrowIfCancellationRequested(); await StartService(tunnel);
            } else tunnel.Log("隧道尚未配置；本地服务已启动，请按接入指南准备官方客户端及 qq-dot 配置。");
        } catch (OperationCanceledException) { bridge.Log("一键启动已取消。"); }
        catch (Exception e) { bridge.Log(e.Message); }
        finally { batch.Dispose(); batch = null; }
    }
    async Task Tick()
    {
        if (closing || ticking) return;
        ticking = true;
        try {
        var tunnelHealth = await tunnelRuntime.Probe();
        HashSet<int> ports; try { ports = ListeningPorts(); } catch { ports = []; }
        foreach (var s in services) {
            if (s.Child != null && !s.Running && s.ExitCode == null && !s.Busy) {
                s.ExitCode = s.Child.Process.ExitCode; s.RetryAt = DateTime.UtcNow.AddSeconds(s == tunnel ? Math.Min(60, 5 * Math.Pow(2, Math.Min(s.RestartAttempts++, 4))) : 5);
                s.Log("进程退出，退出码 " + s.ExitCode);
                if (s.ExitCode == 2) s.Desired = false;
            }
            var ready = s == tunnel ? tunnelHealth.BaseUri != null : s.Listening(ports);
            s.Status.Text = s.Running ? (ready ? "运行中" : "启动中 / 等待端口") + " · PID " + s.Child!.Process.Id : ready ? "外部实例 · 本程序未接管" : s.ExitCode.HasValue ? "已退出 · 代码 " + s.ExitCode : "未运行";
            if (s == tunnel) {
                s.Status.Text = s.Running ? (tunnelHealth.Ready ? "已连接" : "运行中 / 等待连接") + " · PID " + s.Child!.Process.Id : tunnelHealth.BaseUri != null ? (tunnelHealth.Ready ? "已连接" : "连接异常") + " · 外部实例" : s.ExitCode.HasValue ? "已退出 · 代码 " + s.ExitCode : "未连接";
                if (tunnelHealth.Ready) s.RestartAttempts = 0;
            }
            s.Status.ForeColor = s.Running && ready && (s != tunnel || tunnelHealth.Ready) ? Color.FromArgb(17, 126, 85) : ready ? Color.DarkOrange : Color.DimGray;
            s.Start.Enabled = !s.Busy && !s.Running && !ready;
            s.Stop.Enabled = !s.Busy && s.Child != null;
            s.Restart.Enabled = !s.Busy && s.Child != null;
            int drained = 0;
            while (drained++ < 200 && s.Pending.TryDequeue(out var line)) {
                s.History.Enqueue(line); while (s.History.Count > 1500) s.History.Dequeue();
                if (filter.SelectedIndex == 0 || filter.SelectedIndex == Array.IndexOf(services, s) + 1) log.AppendText("[" + s.Name + "] " + line + "\n");
            }
        }
        if (log.TextLength > 400000) RepaintLog();
        if (bridge.Desired && !bridge.Running && bridge.Child != null && !bridge.Busy && autoRestart.Checked && bridge.ExitCode is not null and not 2 && DateTime.UtcNow >= bridge.RetryAt) {
            bridge.Log("5 秒后自动重启桥接。"); await StartService(bridge);
        }
        if (dot.Desired && !dot.Running && dot.Child != null && !dot.Busy && autoRestart.Checked && dot.ExitCode is not null and not 2 && DateTime.UtcNow >= dot.RetryAt) {
            dot.Log("自动重启 Dot MCP。"); await StartService(dot);
        }
        if (tunnelRuntime.AuthenticationFailed && !tunnel.Busy) {
            await StopService(tunnel); tunnelRuntime.ForgetKey();
            tunnel.Log("隧道密钥认证失败，已停止重试。请点“隧道密钥”更新后再启动。");
        } else if (tunnel.Desired && !tunnel.Running && tunnel.Child != null && !tunnel.Busy && autoRestart.Checked && tunnel.ExitCode != null && DateTime.UtcNow >= tunnel.RetryAt && tunnelHealth.BaseUri == null) {
            tunnel.Log("正在自动重连隧道。"); await StartService(tunnel);
        }
        } finally { ticking = false; }
    }
    void RepaintLog()
    {
        var text = string.Join("\n", services.Where((_, i) => filter.SelectedIndex == 0 || filter.SelectedIndex == i + 1).SelectMany(s => s.History.Select(l => "[" + s.Name + "] " + l))) + "\n";
        log.Text = text.Length > 300000 ? text[^300000..] : text;
        log.SelectionStart = log.TextLength; log.ScrollToCaret();
    }
    async Task OpenBackend(Service service)
    {
        if (service == tunnel) {
            var health = await tunnelRuntime.Probe(true);
            string detail = "官方客户端：" + (tunnel.Running ? "由管理器运行" : health.Live ? "外部实例" : "未运行") +
                "\n隧道连接：" + health.Summary + "\n本地密钥：" + (tunnelRuntime.Key != null ? "已设置（仅内存）" : "未设置") +
                "\n\n启动、停止和重连均在此管理器内完成；自动重连需要管理器保持运行。";
            if (health.BaseUri != null) {
                if (MessageBox.Show(this, detail + "\n\n打开官方客户端本地管理后台？", "Dot Tunnel 连接状态", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(new Uri(health.BaseUri, "ui").AbsoluteUri) { UseShellExecute = true });
            } else MessageBox.Show(this, detail, "Dot Tunnel 连接状态");
            return;
        }
        if (service == dot) {
            if (!ListeningPorts().Contains(dotPort)) { dot.Log("Dot MCP 尚未启动，请先点击启动。"); return; }
            var configFile = Path.Combine(dotDirectory, "state", "mcp-config.json");
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configFile));
            var tokenFile = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configFile)!, config.RootElement.GetProperty("transportTokenFile").GetString()!));
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile)).Trim());
            using var response = await client.GetAsync($"http://127.0.0.1:{dotPort}/healthz");
            response.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            bool bridgeReady = status.RootElement.GetProperty("bridgeReachable").GetBoolean();
            int subscriptions = status.RootElement.GetProperty("subscriptions").GetInt32();
            string coverage = status.RootElement.TryGetProperty("subscriptionCoverage", out var scope) ? string.Join("\n", scope.EnumerateArray().Select(s => s.GetProperty("conversationKey").GetString() + "：" + (s.GetProperty("subscribed").GetBoolean() ? "已订阅" : "待在 Dot 中订阅"))) : "";
            string message = "本地 MCP 服务：已运行\n桥接适配：" + (bridgeReady ? "已连接" : "未连接，请检查桥接日志") + "\n有效事件订阅：" + subscriptions + "\n" + coverage + "\n\nDot 的自动处理需要对应会话已订阅，并在 ChatGPT 中启用领取、读取、回复与收尾。";
            if (!closing) MessageBox.Show(this, message, "Dot MCP 接入状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int port = service == snow ? snowWebPort : service == dsh ? dshPort : consolePort;
        if (!ListeningPorts().Contains(port)) {
            service.Log("管理后台尚未就绪，请先启动服务并等待端口 " + port + " 就绪。");
            return;
        }
        string url = $"http://127.0.0.1:{port}/";
        if (service == bridge) {
            var tokenFile = Path.Combine(root, "state", "console-token");
            string token = File.Exists(tokenFile) ? (await File.ReadAllTextAsync(tokenFile)).Trim() : "";
            if (token.Length == 0) {
                using var cfg = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config.json")));
                if (cfg.RootElement.TryGetProperty("consoleToken", out var value)) token = value.ToString();
            }
            if (token.Length > 0) url += "?token=" + Uri.EscapeDataString(token);
            else service.Log("暂未读到控制台令牌，打开后台登录页。");
        } else if (service == dsh) {
            string logFile = Environment.GetEnvironmentVariable("QQB_DSH_LOG") ?? Path.Combine(root, "state", "dsh-web.log");
            string tail = "";
            if (File.Exists(logFile)) {
                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(Math.Max(0, stream.Length - 1024 * 1024), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                tail = await reader.ReadToEndAsync();
            }
            var tokens = Regex.Matches(tail, Regex.Escape(url) + @"\?token=([A-Za-z0-9_-]{8,})")
                .Select(m => m.Groups[1].Value).Reverse().Distinct().Take(2);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            bool authenticated = false;
            foreach (string token in tokens) {
                try {
                    string candidate = url + "?token=" + Uri.EscapeDataString(token);
                    using var response = await client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead);
                    if (response.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.SeeOther) {
                        url = candidate; authenticated = true; break;
                    }
                } catch (HttpRequestException) { } catch (TaskCanceledException) { }
            }
            if (!authenticated) service.Log("暂未找到有效的 DSH 启动令牌，打开后台首页；可从 DSH 启动日志获取登录链接。");
        }
        if (closing) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        service.Log("已在默认浏览器打开管理后台。");
    }
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closing) return;
        closing = true; timer.Stop(); Enabled = false; batch?.Cancel();
        try { await StopAll(); }
        catch (Exception ex) { MessageBox.Show("停止服务时出错：" + ex.Message); }
        finally { foreach (var s in services) s.Child?.Dispose(); tunnelRuntime.ForgetKey(); allowClose = true; Close(); }
    }
}

internal static class SmokeTest
{
    public static async Task Run()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "gptbot-manager-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var lines = new ConcurrentQueue<string>();
        try {
            await TunnelSmokeTest.Run(dir);
            var script = Path.Combine(dir, "fixture.cjs");
            File.WriteAllText(script, "const {spawn}=require('child_process');const c=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore',windowsHide:true});console.log('child='+c.pid);console.log('中文日志');console.error('stderr-ok');setInterval(()=>{},1000);", new UTF8Encoding(false));
            using var child = new ManagedProcess("node.exe", "\"" + script + "\"", dir, lines.Enqueue);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (lines.Count < 3 && DateTime.UtcNow < until) await Task.Delay(50);
            if (!lines.Any(l => l.Contains("中文日志")) || !lines.Any(l => l.Contains("stderr-ok"))) throw new Exception("Output capture failed");
            int descendant = int.Parse(lines.First(l => l.StartsWith("child="))[6..]);
            await child.Stop(); await Task.Delay(300);
            try { using var p = Process.GetProcessById(descendant); if (!p.HasExited) throw new Exception("Descendant survived stop"); } catch (ArgumentException) { }
            using var exited = new ManagedProcess("node.exe", "-e \"process.exit(7)\"", dir, lines.Enqueue);
            await exited.Process.WaitForExitAsync(); if (exited.Process.ExitCode != 7) throw new Exception("Exit code lost");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "manager-test-result.txt"), "PASS: responsive manager layout (80 work-area/size cases at the process DPI); hidden process startup, UTF-8 stdout/stderr, exit codes, stop and descendant cleanup; tunnel readiness, stale health, secret environment isolation and log redaction.");
        } finally { Directory.Delete(dir, true); }
    }
}
