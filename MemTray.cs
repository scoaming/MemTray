// MemTray - 极轻量 Windows 托盘内存监控
// 编译：见 build.ps1（系统自带 csc.exe，无需安装任何 SDK）
//
// 设计要点：
//   1. 汇总数据走 GlobalMemoryStatusEx（P/Invoke，微秒级），绝不用 WMI 轮询；
//   2. 进程榜只在面板打开或越线告警时枚举，平时零开销；
//   3. 越线瞬间自动抓 Top10 落盘，解决"点开看已经错过现场"的问题。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MemTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\MemTray.SingleInstance", out created))
            {
                if (!created)
                {
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting("Local\\MemTray.ShowPanel"))
                            ev.Set();
                    }
                    catch (Exception) { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Write(e.ExceptionObject as Exception); };

                using (TrayApp app = new TrayApp())
                {
                    Application.Run();
                }
            }
        }
    }

    internal static class Log
    {
        internal static void Write(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(Path.Combine(Config.DataDir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch (Exception) { }
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal class MEMORYSTATUSEX
        {
            internal uint dwLength;
            internal uint dwMemoryLoad;
            internal ulong ullTotalPhys;
            internal ulong ullAvailPhys;
            internal ulong ullTotalPageFile;
            internal ulong ullAvailPageFile;
            internal ulong ullTotalVirtual;
            internal ulong ullAvailVirtual;
            internal ulong ullAvailExtendedVirtual;

            internal MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_MEMORY_COUNTERS_EX
        {
            internal uint cb;
            internal uint PageFaultCount;
            internal UIntPtr PeakWorkingSetSize;
            internal UIntPtr WorkingSetSize;
            internal UIntPtr QuotaPeakPagedPoolUsage;
            internal UIntPtr QuotaPagedPoolUsage;
            internal UIntPtr QuotaPeakNonPagedPoolUsage;
            internal UIntPtr QuotaNonPagedPoolUsage;
            internal UIntPtr PagefileUsage;
            internal UIntPtr PeakPagefileUsage;
            internal UIntPtr PrivateUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PERFORMANCE_INFORMATION
        {
            internal uint cb;
            internal IntPtr CommitTotal;
            internal IntPtr CommitLimit;
            internal IntPtr CommitPeak;
            internal IntPtr PhysicalTotal;
            internal IntPtr PhysicalAvailable;
            internal IntPtr SystemCache;
            internal IntPtr KernelTotal;
            internal IntPtr KernelPaged;
            internal IntPtr KernelNonpaged;
            internal IntPtr PageSize;
            internal uint HandleCount;
            internal uint ProcessCount;
            internal uint ThreadCount;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetForegroundWindow(IntPtr hWnd);

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
    }

    internal sealed class MemInfo
    {
        internal DateTime Time;
        internal ulong Total;
        internal ulong Avail;
        internal ulong Used;
        internal double Percent;
        internal ulong CommitUsed;
        internal ulong CommitLimit;
        internal double CpuPercent;

        internal double TotalGB { get { return Total / 1073741824.0; } }
        internal double AvailGB { get { return Avail / 1073741824.0; } }
        internal double UsedGB { get { return Used / 1073741824.0; } }
        internal double CommitUsedGB { get { return CommitUsed / 1073741824.0; } }
        internal double CommitLimitGB { get { return CommitLimit / 1073741824.0; } }
    }

    internal sealed class MemSample
    {
        internal DateTime Time;
        internal double Percent;
        internal double UsedGB;
        internal double AvailGB;
    }

    internal sealed class ProcRow
    {
        internal string Name;
        internal int Count;
        internal long WsBytes;
        internal long PrivateBytes;
        internal double CpuPercent;
        internal double CpuSeconds;
        internal long CpuTicks;

        internal long Metric(bool usePrivate)
        {
            return usePrivate ? PrivateBytes : WsBytes;
        }
    }

    internal sealed class AlertRecord
    {
        internal DateTime Time;
        internal MemInfo Mem;
        internal List<ProcRow> Top;
        internal string Reason;
        internal bool MetricPrivate;
    }

    internal static class Fmt
    {
        internal static string Size(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        internal static string Gb(double gb)
        {
            return gb.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }
    }

    internal sealed class Config
    {
        internal double AlertPercent = 90.0;
        internal double AlertAvailGB = 1.5;
        internal int AlertSustainSeconds = 10;
        internal int CooldownSeconds = 300;
        internal bool UsePrivateMetric = false;

        internal static string DataDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemTray");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        internal static string ConfigPath { get { return Path.Combine(DataDir, "config.ini"); } }
        internal static string LogDir { get { return Path.Combine(DataDir, "logs"); } }

        internal static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(ConfigPath)) return c;
                string[] lines = File.ReadAllLines(ConfigPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    double d;
                    int n;
                    switch (k)
                    {
                        case "alert_percent":
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) c.AlertPercent = d;
                            break;
                        case "alert_avail_gb":
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) c.AlertAvailGB = d;
                            break;
                        case "sustain_seconds":
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) c.AlertSustainSeconds = n;
                            break;
                        case "cooldown_seconds":
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) c.CooldownSeconds = n;
                            break;
                        case "metric":
                            c.UsePrivateMetric = v.Equals("private", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                }
            }
            catch (Exception) { }
            return c;
        }

        internal void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# MemTray 配置。修改后重开程序生效；菜单里的开关会自动写回这里。");
                sb.AppendLine("# alert_percent  : 使用率超过该值即视为越线");
                sb.AppendLine("# alert_avail_gb : 可用物理内存低于该值即视为越线");
                sb.AppendLine("# sustain_seconds: 越线持续多少秒才告警");
                sb.AppendLine("# cooldown_seconds: 两次告警的最小间隔");
                sb.AppendLine("# metric         : ws=工作集(默认) / private=专用内存");
                sb.AppendLine("alert_percent=" + AlertPercent.ToString("0.0", CultureInfo.InvariantCulture));
                sb.AppendLine("alert_avail_gb=" + AlertAvailGB.ToString("0.0", CultureInfo.InvariantCulture));
                sb.AppendLine("sustain_seconds=" + AlertSustainSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("cooldown_seconds=" + CooldownSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("metric=" + (UsePrivateMetric ? "private" : "ws"));
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception) { }
        }
    }

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MemTray";

        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(ValueName) != null;
                }
            }
            catch (Exception) { return false; }
        }

        internal static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled)
                        k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else
                        k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception) { }
        }
    }

    internal sealed class TrayApp : IDisposable
    {
        internal const int TopCount = 10;         // 面板/菜单里显示的进程条数
        private const int HistoryCapacity = 1800; // 1s 一个采样点 = 30 分钟

        private readonly Config _cfg;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly MonitorPanel _panel;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly List<MemSample> _history = new List<MemSample>();
        private readonly List<AlertRecord> _alerts = new List<AlertRecord>();
        private readonly Dictionary<string, long> _prevCpuTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Process _self;
        private readonly EventWaitHandle _showEvent;
        private readonly RegisteredWaitHandle _registeredWait;
        private readonly int _sustainTicks;

        private List<ProcRow> _top = new List<ProcRow>();
        private MemInfo _mem;
        private long _prevProcStamp;
        private int _overCount;
        private DateTime _lastAlert = DateTime.MinValue;
        private int _lastIconPercent = -1;
        private string _lastTip;
        private int _ticks;
        private Icon _currentIcon;

        internal TrayApp()
        {
            _cfg = Config.Load();
            _sustainTicks = Math.Max(1, _cfg.AlertSustainSeconds);
            _self = Process.GetCurrentProcess();

            _panel = new MonitorPanel();
            IntPtr forced = _panel.Handle; // 建立窗口句柄，供单实例信号 BeginInvoke 使用
            Native.SetWindowPos(forced, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            _menu = new ContextMenuStrip();
            _menu.Opening += MenuOpening;

            _tray = new NotifyIcon();
            _currentIcon = BuildIcon(0);
            _tray.Icon = _currentIcon;
            _tray.Text = "内存监控";
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += TrayMouseClick;
            _tray.DoubleClick += delegate { ShowPanel(); };
            _tray.Visible = true;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += TimerTick;
            _timer.Start();



            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MemTray.ShowPanel");
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, ShowSignal, null, Timeout.Infinite, false);

            Tick();
        }

        private void ShowSignal(object state, bool timedOut)
        {
            try
            {
                if (_panel.IsHandleCreated)
                    _panel.BeginInvoke(new MethodInvoker(ShowPanel));
            }
            catch (Exception) { }
        }

        private void TimerTick(object sender, EventArgs e)
        {
            Tick();
        }

        private void Tick()
        {
            try
            {
                _ticks++;
                _mem = SampleMemory();
                AddHistory(_mem);
                UpdateTrayVisuals();
                CheckAlert();

                if (_panel.Visible)
                {
                    if ((_ticks % 2) == 0) _top = TakeSnapshot(TopCount);
                    _panel.SetData(_mem, _top, _alerts, _history, _cfg, _self.PrivateMemorySize64 / 1048576.0);
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        // ---------- 采集 ----------

        private MemInfo SampleMemory()
        {
            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            Native.GlobalMemoryStatusEx(m);

            MemInfo info = new MemInfo();
            info.Time = DateTime.Now;
            info.Total = m.ullTotalPhys;
            info.Avail = m.ullAvailPhys;
            info.Used = m.ullTotalPhys - m.ullAvailPhys;
            info.Percent = info.Total == 0 ? 0 : info.Used * 100.0 / info.Total;
            info.CommitLimit = m.ullTotalPageFile;
            info.CommitUsed = m.ullTotalPageFile - m.ullAvailPageFile;
            info.CpuPercent = SampleCpu();
            return info;
        }

        private ulong _prevIdle, _prevKernel, _prevUser;
        private bool _hasCpu;

        private double SampleCpu()
        {
            ulong idle, kern, user;
            if (!Native.GetSystemTimes(out idle, out kern, out user)) return 0;
            if (!_hasCpu)
            {
                _prevIdle = idle; _prevKernel = kern; _prevUser = user;
                _hasCpu = true;
                return 0;
            }
            ulong dIdle = idle - _prevIdle;
            ulong dKern = kern - _prevKernel;
            ulong dUser = user - _prevUser;
            ulong dTotal = dKern + dUser;
            _prevIdle = idle; _prevKernel = kern; _prevUser = user;
            if (dTotal == 0) return 0;
            double busy = (double)(dTotal - dIdle) * 100.0 / dTotal;
            if (busy < 0) busy = 0;
            if (busy > 100) busy = 100;
            return busy;
        }

        private void AddHistory(MemInfo m)
        {
            MemSample s = new MemSample();
            s.Time = m.Time;
            s.Percent = m.Percent;
            s.UsedGB = m.UsedGB;
            s.AvailGB = m.AvailGB;
            _history.Add(s);
            while (_history.Count > HistoryCapacity) _history.RemoveAt(0);
        }

        private sealed class Agg
        {
            internal string Name;
            internal int Count;
            internal long Ws;
            internal long Private;
            internal long CpuTicks;
        }

        private List<ProcRow> TakeSnapshot(int topN)
        {
            long nowStamp = Stopwatch.GetTimestamp();
            double elapsed = 0;
            if (_prevProcStamp != 0) elapsed = (nowStamp - _prevProcStamp) / (double)Stopwatch.Frequency;
            _prevProcStamp = nowStamp;

            Dictionary<string, Agg> map = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
            Process[] procs = null;
            try { procs = Process.GetProcesses(); }
            catch (Exception) { return new List<ProcRow>(); }

            for (int i = 0; i < procs.Length; i++)
            {
                Process p = procs[i];
                try
                {
                    string name = p.ProcessName;
                    if (string.IsNullOrEmpty(name)) continue;

                    Agg a;
                    if (!map.TryGetValue(name, out a))
                    {
                        a = new Agg();
                        a.Name = name;
                        map[name] = a;
                    }
                    a.Count++;

                    long ws, priv;
                    if (!TryGetMemory(p, out ws, out priv))
                    {
                        try { ws = p.WorkingSet64; priv = ws; }
                        catch (Exception) { ws = 0; priv = 0; }
                    }
                    a.Ws += ws;
                    a.Private += priv;

                    try { a.CpuTicks += p.TotalProcessorTime.Ticks; }
                    catch (Exception) { }
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }

            List<ProcRow> rows = new List<ProcRow>(map.Count);
            foreach (Agg a in map.Values)
            {
                ProcRow r = new ProcRow();
                r.Name = a.Name;
                r.Count = a.Count;
                r.WsBytes = a.Ws;
                r.PrivateBytes = a.Private;
                r.CpuTicks = a.CpuTicks;
                r.CpuSeconds = a.CpuTicks / 10000000.0;

                long prev;
                if (elapsed > 0.2 && _prevCpuTicks.TryGetValue(a.Name, out prev))
                {
                    double d = (a.CpuTicks - prev) / 10000000.0;
                    r.CpuPercent = d / (elapsed * Environment.ProcessorCount) * 100.0;
                    if (r.CpuPercent < 0) r.CpuPercent = 0;
                }
                rows.Add(r);
            }

            _prevCpuTicks.Clear();
            foreach (Agg a in map.Values) _prevCpuTicks[a.Name] = a.CpuTicks;

            bool usePriv = _cfg.UsePrivateMetric;
            rows.Sort(delegate(ProcRow x, ProcRow y)
            {
                return y.Metric(usePriv).CompareTo(x.Metric(usePriv));
            });
            if (rows.Count > topN) rows.RemoveRange(topN, rows.Count - topN);
            return rows;
        }

        private static bool TryGetMemory(Process p, out long ws, out long priv)
        {
            ws = 0; priv = 0;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(0x0410, false, p.Id); // QUERY_INFORMATION | VM_READ
                if (h == IntPtr.Zero) h = Native.OpenProcess(0x1000, false, p.Id); // QUERY_LIMITED_INFORMATION
                if (h == IntPtr.Zero) return false;

                Native.PROCESS_MEMORY_COUNTERS_EX c = new Native.PROCESS_MEMORY_COUNTERS_EX();
                c.cb = (uint)Marshal.SizeOf(typeof(Native.PROCESS_MEMORY_COUNTERS_EX));
                if (!Native.GetProcessMemoryInfo(h, out c, c.cb)) return false;

                ws = (long)c.WorkingSetSize.ToUInt64();
                priv = (long)c.PrivateUsage.ToUInt64();
                return ws > 0;
            }
            catch (Exception) { return false; }
            finally
            {
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            }
        }

        // ---------- 托盘显示 ----------

        internal static Color StatusColorPublic(double pct)
        {
            if (pct >= 90) return Color.FromArgb(231, 76, 60);
            if (pct >= 70) return Color.FromArgb(241, 196, 15);
            return Color.FromArgb(46, 204, 113);
        }

        private void UpdateTrayVisuals()
        {
            int ip = (int)Math.Round(_mem.Percent);
            if (ip != _lastIconPercent)
            {
                _lastIconPercent = ip;
                SetIcon(BuildIcon(_mem.Percent));
            }

            string tip = string.Format(CultureInfo.InvariantCulture,
                "内存 {0:0.0}% · 可用 {1:0.00}/{2:0.00} GB", _mem.Percent, _mem.AvailGB, _mem.TotalGB);
            if (tip != _lastTip)
            {
                _lastTip = tip;
                _tray.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
            }
        }

        private void SetIcon(Icon icon)
        {
            Icon old = _currentIcon;
            _currentIcon = icon;
            _tray.Icon = icon;
            if (old != null) old.Dispose();
        }

        internal static GraphicsPath RoundRectPublic(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Icon BuildIcon(double percent)
        {
            int size = 32;
            using (Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    RectangleF box = new RectangleF(7, 3, 18, 26);
                    using (GraphicsPath path = RoundRectPublic(box, 3f))
                    {
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(70, 130, 140, 155)))
                            g.FillPath(bg, path);

                        if (percent > 0.5)
                        {
                            double p = Math.Min(100.0, percent);
                            float fh = (float)(box.Height * p / 100.0);
                            RectangleF fill = new RectangleF(box.X, box.Bottom - fh, box.Width, fh);
                            using (GraphicsPath fp = RoundRectPublic(fill, 3f))
                            using (SolidBrush fb = new SolidBrush(StatusColorPublic(percent)))
                                g.FillPath(fb, fp);
                        }

                        using (Pen pen = new Pen(Color.FromArgb(190, 250, 250, 250), 1.2f))
                            g.DrawPath(pen, path);
                    }
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(h);
                }
            }
        }

        private void TrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_panel.Visible)
            {
                _panel.HidePanel();
                return;
            }
            // 点托盘图标本身也会让面板失焦隐藏，300ms 内不重复弹回，实现"再点一次收起"
            if ((DateTime.UtcNow - _panel.LastHiddenUtc).TotalMilliseconds < 300) return;
            ShowPanel();
        }

        private void ShowPanel()
        {
            if (_mem == null) _mem = SampleMemory();
            _top = TakeSnapshot(TopCount);
            _panel.SetData(_mem, _top, _alerts, _history, _cfg, _self.PrivateMemorySize64 / 1048576.0);
            PositionPanel();
            _panel.Show();
            _panel.TopMost = false;
            _panel.TopMost = true;
            Native.SetForegroundWindow(_panel.Handle);
            _panel.BringToFront();
            _panel.Activate();
        }

        private void PositionPanel()
        {
            Screen scr = Screen.FromPoint(Cursor.Position);
            Rectangle wa = scr.WorkingArea;
            int margin = (int)(12 * _panel.ScaleFactor);
            _panel.Left = wa.Right - _panel.Width - margin;
            _panel.Top = wa.Bottom - _panel.Height - margin;
        }

        // ---------- 告警 ----------

        private void CheckAlert()
        {
            bool over = _mem.Percent >= _cfg.AlertPercent || _mem.AvailGB <= _cfg.AlertAvailGB;
            if (over) _overCount++;
            else _overCount = 0;

            if (_overCount >= _sustainTicks &&
                (DateTime.Now - _lastAlert).TotalSeconds >= _cfg.CooldownSeconds)
            {
                _lastAlert = DateTime.Now;
                _overCount = 0;
                RaiseAlert("自动", true);
            }
        }

        private void RaiseAlert(string reason, bool notify)
        {
            List<ProcRow> top = TakeSnapshot(10);
            AlertRecord rec = new AlertRecord();
            rec.Time = DateTime.Now;
            rec.Mem = _mem;
            rec.Top = top;
            rec.Reason = reason;
            rec.MetricPrivate = _cfg.UsePrivateMetric;

            _alerts.Insert(0, rec);
            while (_alerts.Count > 20) _alerts.RemoveAt(_alerts.Count - 1);

            AppendCsv(rec);

            string text;
            if (top.Count > 0)
                text = string.Format(CultureInfo.InvariantCulture,
                    "内存 {0:0.0}% · 可用 {1:0.00} GB\n最大占用：{2} {3}",
                    rec.Mem.Percent, rec.Mem.AvailGB, top[0].Name, Fmt.Size(top[0].Metric(rec.MetricPrivate)));
            else
                text = string.Format(CultureInfo.InvariantCulture,
                    "内存 {0:0.0}% · 可用 {1:0.00} GB", rec.Mem.Percent, rec.Mem.AvailGB);

            if (!notify) return;
            try { _tray.ShowBalloonTip(6000, "内存告警 " + rec.Time.ToString("HH:mm:ss"), text, ToolTipIcon.Warning); }
            catch (Exception) { }
        }

        private void ManualSnapshot()
        {
            if (_mem == null) _mem = SampleMemory();
            RaiseAlert("手动", false);
            try { _tray.ShowBalloonTip(4000, "已记录快照", "已写入 " + Path.Combine(Config.LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".csv"), ToolTipIcon.Info); }
            catch (Exception) { }
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private void AppendCsv(AlertRecord rec)
        {
            try
            {
                Directory.CreateDirectory(Config.LogDir);
                string file = Path.Combine(Config.LogDir, rec.Time.ToString("yyyy-MM-dd") + ".csv");
                StringBuilder sb = new StringBuilder();
                if (!File.Exists(file))
                    sb.AppendLine("时间,原因,内存使用率%,已用GB,可用GB,提交已用GB,提交上限GB,排名,程序,实例数,工作集MB,专用MB,累计CPU秒");

                for (int i = 0; i < rec.Top.Count; i++)
                {
                    ProcRow r = rec.Top[i];
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2:0.0},{3:0.00},{4:0.00},{5:0.00},{6:0.00},{7},{8},{9},{10:0.0},{11:0.0},{12:0}",
                        rec.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                        rec.Reason,
                        rec.Mem.Percent,
                        rec.Mem.UsedGB,
                        rec.Mem.AvailGB,
                        rec.Mem.CommitUsedGB,
                        rec.Mem.CommitLimitGB,
                        i + 1,
                        CsvEscape(r.Name),
                        r.Count,
                        r.WsBytes / 1048576.0,
                        r.PrivateBytes / 1048576.0,
                        r.CpuSeconds));
                }
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        private string BuildAlertText()
        {
            if (_alerts.Count == 0) return "暂无告警记录";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _alerts.Count && i < 3; i++)
            {
                AlertRecord a = _alerts[i];
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "[{0}] 内存 {1:0.0}%  可用 {2:0.00} GB  提交 {3:0.00}/{4:0.00} GB{5}",
                    a.Time.ToString("HH:mm:ss"), a.Mem.Percent, a.Mem.AvailGB,
                    a.Mem.CommitUsedGB, a.Mem.CommitLimitGB, Environment.NewLine);
                for (int j = 0; j < a.Top.Count && j < 5; j++)
                {
                    ProcRow r = a.Top[j];
                    sb.AppendFormat(CultureInfo.InvariantCulture, "  {0}. {1}{2}  {3}  ({4:0.0}%){5}",
                        j + 1, r.Name, r.Count > 1 ? " x" + r.Count : "",
                        Fmt.Size(r.Metric(a.MetricPrivate)),
                        r.Metric(a.MetricPrivate) * 100.0 / Math.Max(1.0, (double)a.Mem.Total),
                        Environment.NewLine);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ---------- 右键菜单 ----------

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                BuildMenu();
            }
            catch (Exception ex)
            {
                // A failing menu builder must never leave the user without a menu.
                Log.Write(ex);
                try
                {
                    while (_menu.Items.Count > 0)
                    {
                        ToolStripItem it = _menu.Items[0];
                        _menu.Items.RemoveAt(0);
                        it.Dispose();
                    }
                    ToolStripMenuItem exit = new ToolStripMenuItem("退出");
                    exit.Click += delegate { Application.Exit(); };
                    _menu.Items.Add(exit);
                }
                catch (Exception inner) { Log.Write(inner); }
            }
        }

        private void BuildMenu()
        {
            while (_menu.Items.Count > 0)
            {
                ToolStripItem it = _menu.Items[0];
                _menu.Items.RemoveAt(0);
                it.Dispose();
            }

            if (_mem != null)
            {
                ToolStripMenuItem head = new ToolStripMenuItem(string.Format(CultureInfo.InvariantCulture,
                    "内存 {0:0.0}%   {1:0.00} / {2:0.00} GB", _mem.Percent, _mem.UsedGB, _mem.TotalGB));
                head.Enabled = false;
                _menu.Items.Add(head);

                ToolStripMenuItem head2 = new ToolStripMenuItem(string.Format(CultureInfo.InvariantCulture,
                    "CPU {0:0.0}%   提交 {1:0.00}/{2:0.00} GB", _mem.CpuPercent, _mem.CommitUsedGB, _mem.CommitLimitGB));
                head2.Enabled = false;
                _menu.Items.Add(head2);

                if (_top.Count > 0)
                {
                    _menu.Items.Add(new ToolStripSeparator());
                    for (int i = 0; i < _top.Count && i < TopCount; i++)
                    {
                        ProcRow r = _top[i];
                        long b = r.Metric(_cfg.UsePrivateMetric);
                        string label = string.Format(CultureInfo.InvariantCulture,
                            "{0}. {1}{2}   {3}", i + 1, r.Name, r.Count > 1 ? " x" + r.Count : "", Fmt.Size(b));
                        ToolStripMenuItem item = new ToolStripMenuItem(label);
                        item.Click += delegate { OpenTaskManager(); };
                        _menu.Items.Add(item);
                    }
                }
            }

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem panelItem = new ToolStripMenuItem("打开监控面板");
            panelItem.Click += delegate { ShowPanel(); };
            _menu.Items.Add(panelItem);

            ToolStripMenuItem breakdownItem = new ToolStripMenuItem("查看内存构成…");
            breakdownItem.Click += delegate { ShowBreakdown(); };
            _menu.Items.Add(breakdownItem);

            ToolStripMenuItem snap = new ToolStripMenuItem("立即快照并记录");
            snap.Click += delegate { ManualSnapshot(); };
            _menu.Items.Add(snap);

            ToolStripMenuItem copy = new ToolStripMenuItem("复制最近告警");
            copy.Enabled = _alerts.Count > 0;
            copy.Click += delegate
            {
                try { Clipboard.SetText(BuildAlertText()); }
                catch (Exception) { }
            };
            _menu.Items.Add(copy);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem metric = new ToolStripMenuItem("按专用内存统计（否则工作集）");
            metric.Checked = _cfg.UsePrivateMetric;
            metric.Click += delegate
            {
                _cfg.UsePrivateMetric = !_cfg.UsePrivateMetric;
                _cfg.Save();
                _top = TakeSnapshot(TopCount);
                if (_panel.Visible)
                    _panel.SetData(_mem, _top, _alerts, _history, _cfg, _self.PrivateMemorySize64 / 1048576.0);
            };
            _menu.Items.Add(metric);

            ToolStripMenuItem auto = new ToolStripMenuItem("开机自启");
            auto.Checked = Autostart.IsEnabled();
            auto.Click += delegate { Autostart.SetEnabled(!Autostart.IsEnabled()); };
            _menu.Items.Add(auto);

            ToolStripMenuItem logs = new ToolStripMenuItem("打开日志目录");
            logs.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(Config.LogDir);
                    Process.Start("explorer.exe", "\"" + Config.LogDir + "\"");
                }
                catch (Exception) { }
            };
            _menu.Items.Add(logs);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { Application.Exit(); };
            _menu.Items.Add(exit);
        }

        private static void OpenTaskManager()
        {
            try { Process.Start("taskmgr.exe"); }
            catch (Exception) { }
        }

        private void ShowBreakdown()
        {
            try
            {
                MemBreakdown b = MemBreakdown.Collect();
                BreakdownForm f = new BreakdownForm(b);
                f.Show();
                f.TopMost = false;
                f.TopMost = true;
                Native.SetForegroundWindow(f.Handle);
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        // ---------- 清理 ----------

        public void Dispose()
        {
            try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); } }
            catch (Exception) { }
            try { if (_registeredWait != null) _registeredWait.Unregister(null); }
            catch (Exception) { }
            try { if (_showEvent != null) _showEvent.Close(); }
            catch (Exception) { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } }
            catch (Exception) { }
            try { if (_currentIcon != null) { _currentIcon.Dispose(); _currentIcon = null; } }
            catch (Exception) { }
            try { if (_panel != null) _panel.Dispose(); }
            catch (Exception) { }
            try { if (_menu != null) _menu.Dispose(); }
            catch (Exception) { }
            try { if (_self != null) _self.Dispose(); }
            catch (Exception) { }
        }
    }

    internal sealed class MemBreakdown
    {
        internal DateTime Time;
        internal double TotalGB;
        internal double AvailGB;
        internal double UsedGB;
        internal double Percent;
        internal double ProcessGB;
        internal double CompressionGB;
        internal double KernelGB;
        internal double KernelPagedGB;
        internal double KernelNonpagedGB;
        internal double OtherGB;
        internal double CommitUsedGB;
        internal double CommitLimitGB;
        internal int ProcessCount;
        internal int ThreadCount;
        internal int HandleCount;

        internal static MemBreakdown Collect()
        {
            MemBreakdown b = new MemBreakdown();
            b.Time = DateTime.Now;

            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(m))
            {
                b.TotalGB = m.ullTotalPhys / 1073741824.0;
                b.AvailGB = m.ullAvailPhys / 1073741824.0;
                b.UsedGB = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
                if (m.ullTotalPhys > 0)
                    b.Percent = (m.ullTotalPhys - m.ullAvailPhys) * 100.0 / m.ullTotalPhys;
                if (m.ullTotalPageFile > m.ullAvailPageFile)
                {
                    b.CommitUsedGB = (m.ullTotalPageFile - m.ullAvailPageFile) / 1073741824.0;
                    b.CommitLimitGB = m.ullTotalPageFile / 1073741824.0;
                }
            }

            Native.PERFORMANCE_INFORMATION pi;
            if (Native.GetPerformanceInfo(out pi, (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION))))
            {
                long page = pi.PageSize.ToInt64();
                if (page <= 0) page = 4096;
                b.KernelNonpagedGB = pi.KernelNonpaged.ToInt64() * (double)page / 1073741824.0;
                b.KernelPagedGB = pi.KernelPaged.ToInt64() * (double)page / 1073741824.0;
                b.KernelGB = b.KernelPagedGB + b.KernelNonpagedGB;
                b.ProcessCount = (int)pi.ProcessCount;
                b.ThreadCount = (int)pi.ThreadCount;
                b.HandleCount = (int)pi.HandleCount;
            }

            Process[] ps = Process.GetProcesses();
            long sum = 0;
            long comp = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                Process p = ps[i];
                try
                {
                    long ws = p.WorkingSet64;
                    sum += ws;
                    if (comp == 0 && p.ProcessName == "Memory Compression") comp = ws;
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            b.ProcessGB = sum / 1073741824.0;
            b.CompressionGB = comp / 1073741824.0;
            if (b.ProcessCount <= 0) b.ProcessCount = ps.Length;

            b.OtherGB = b.UsedGB - b.ProcessGB - b.KernelGB;
            if (b.OtherGB < 0) b.OtherGB = 0;
            return b;
        }
    }

    internal sealed class BreakdownForm : Form
    {
        private static readonly Color CText = Color.FromArgb(232, 234, 237);
        private static readonly Color CDim = Color.FromArgb(154, 160, 166);
        private static readonly Color CLine = Color.FromArgb(48, 55, 68);

        private readonly MemBreakdown _b;
        private readonly float _scale;
        private readonly Font _fTitle;
        private readonly Font _fBig;
        private readonly Font _fRow;
        private readonly Font _fMeta;
        private DateTime _shownUtc = DateTime.MinValue;

        internal BreakdownForm(MemBreakdown b)
        {
            _b = b;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(26, 30, 38);
            Text = "内存构成";

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                _scale = g.DpiX / 96f;
            if (_scale < 1f) _scale = 1f;

            ClientSize = new Size((int)(380 * _scale), (int)(322 * _scale));
            _fTitle = new Font("Microsoft YaHei UI", 17f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _fBig = new Font("Microsoft YaHei UI", 18f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _fRow = new Font("Microsoft YaHei UI", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fMeta = new Font("Microsoft YaHei UI", 10.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);

            using (GraphicsPath p = TrayApp.RoundRectPublic(new RectangleF(0, 0, Width, Height), 12f * _scale))
                Region = new Region(p);

            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // 从右键菜单打开时，菜单收起会先触发一次 Deactivate，这里跳过最初 0.5 秒
            if ((DateTime.UtcNow - _shownUtc).TotalMilliseconds < 500) return;
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _shownUtc = DateTime.UtcNow;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float s = _scale;
            float w = ClientSize.Width;
            float h = ClientSize.Height;
            float pad = 16f * s;

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(26, 30, 38)))
                g.FillRectangle(bg, ClientRectangle);
            using (Pen border = new Pen(Color.FromArgb(62, 70, 88), 1f))
                g.DrawRectangle(border, 0.5f, 0.5f, w - 1f, h - 1f);

            Txt(g, "内存构成", _fTitle, CText, pad, 13 * s);
            TxtRight(g, _b.Time.ToString("HH:mm:ss"), _fMeta, CDim, w - pad, 17 * s);

            string used = "已用 " + Fmt.Gb(_b.UsedGB);
            Txt(g, used, _fBig, CText, pad, 40 * s);
            float uw = g.MeasureString(used, _fBig).Width;
            Txt(g, "/ " + Fmt.Gb(_b.TotalGB) + "   " +
                _b.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                _fRow, CDim, pad + uw, 50 * s);

            Line(g, pad, 76 * s, w - pad, 76 * s);

            float y = 86 * s;
            float rowH = 26 * s;
            Row(g, "进程（工作集）", Fmt.Gb(_b.ProcessGB), CText, pad, w, y, s);
            Row(g, "　其中 内存压缩", Fmt.Gb(_b.CompressionGB), CDim, pad, w, y + rowH, s);
            Row(g, "内核池", Fmt.Gb(_b.KernelGB), CText, pad, w, y + rowH * 2, s);
            Row(g, "　非分页 / 分页", _b.KernelNonpagedGB.ToString("0.00", CultureInfo.InvariantCulture) +
                " / " + _b.KernelPagedGB.ToString("0.00", CultureInfo.InvariantCulture) + " GB",
                CDim, pad, w, y + rowH * 3, s);
            Row(g, "其他（共享页 / 缓存 / 驱动）", Fmt.Gb(_b.OtherGB), CText, pad, w, y + rowH * 4, s);
            Row(g, "可用", Fmt.Gb(_b.AvailGB), CText, pad, w, y + rowH * 5, s);

            Line(g, pad, 248 * s, w - pad, 248 * s);
            Txt(g, string.Format(CultureInfo.InvariantCulture, "{0} 进程 · {1} 线程 · {2} 句柄",
                _b.ProcessCount, _b.ThreadCount, _b.HandleCount), _fMeta, CDim, pad, 256 * s);
            Txt(g, "提交 " + Fmt.Gb(_b.CommitUsedGB) + " / " + Fmt.Gb(_b.CommitLimitGB),
                _fMeta, CDim, pad, 272 * s);
            Txt(g, "工作集合计含共享页；系统修剪工作集时，进程行变小、其他行变大", _fMeta,
                Color.FromArgb(150, CDim), pad, 292 * s);
        }

        private void Row(Graphics g, string label, string value, Color color, float pad, float w, float y, float s)
        {
            Txt(g, label, _fRow, color, pad, y);
            TxtRight(g, value, _fRow, color, w - pad, y);
        }

        private static void Txt(Graphics g, string text, Font f, Color c, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(text, f, b, x, y);
        }

        private static void TxtRight(Graphics g, string text, Font f, Color c, float right, float y)
        {
            SizeF sz = g.MeasureString(text, f);
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(text, f, b, right - sz.Width, y);
        }

        private static void Line(Graphics g, float x1, float y1, float x2, float y2)
        {
            using (Pen p = new Pen(CLine, 1f))
                g.DrawLine(p, x1, y1, x2, y2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_fTitle != null) _fTitle.Dispose();
                if (_fBig != null) _fBig.Dispose();
                if (_fRow != null) _fRow.Dispose();
                if (_fMeta != null) _fMeta.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class MonitorPanel : Form
    {
        private static readonly Color CText = Color.FromArgb(232, 234, 237);
        private static readonly Color CDim = Color.FromArgb(154, 160, 166);

        private MemInfo _mem;
        private List<ProcRow> _rows = new List<ProcRow>();
        private List<AlertRecord> _alerts = new List<AlertRecord>();
        private List<MemSample> _history = new List<MemSample>();
        private Config _cfg;
        private double _selfMb;
        private float _scale = 1f;

        private Font _fTitle, _fSub, _fLabel, _fValue, _fSection, _fRow, _fMeta, _fFooter;

        internal float ScaleFactor { get { return _scale; } }

        internal DateTime LastHiddenUtc;

        internal MonitorPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(26, 30, 38);
            AutoScaleMode = AutoScaleMode.None;
            Text = "MemTray";

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                _scale = g.DpiX / 96f;
            if (_scale < 1f) _scale = 1f;

            ClientSize = new Size((int)(400 * _scale), (int)((372 + (TrayApp.TopCount - 5) * 24) * _scale));
            CreateFonts();
            ApplyRegion();
        }

        private void CreateFonts()
        {
            _fTitle = new Font("Microsoft YaHei UI", 17f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _fSub = new Font("Microsoft YaHei UI", 11f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fLabel = new Font("Microsoft YaHei UI", 10.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fValue = new Font("Microsoft YaHei UI", 18f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _fSection = new Font("Microsoft YaHei UI", 12f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _fRow = new Font("Microsoft YaHei UI", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fMeta = new Font("Microsoft YaHei UI", 10.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fFooter = new Font("Microsoft YaHei UI", 10.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void ApplyRegion()
        {
            using (GraphicsPath p = TrayApp.RoundRectPublic(new RectangleF(0, 0, Width, Height), 12f * _scale))
                Region = new Region(p);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW：不占 Alt+Tab
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_scale > 0) ApplyRegion();
        }

        internal void SetData(MemInfo mem, List<ProcRow> rows, List<AlertRecord> alerts,
                              List<MemSample> history, Config cfg, double selfMb)
        {
            _mem = mem;
            _rows = rows ?? new List<ProcRow>();
            _alerts = alerts ?? new List<AlertRecord>();
            _history = history ?? new List<MemSample>();
            _cfg = cfg;
            _selfMb = selfMb;
            Invalidate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HidePanel();
        }

        internal void HidePanel()
        {
            LastHiddenUtc = DateTime.UtcNow;
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) HidePanel();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 背景在 OnPaint 中统一绘制，避免闪烁
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float s = _scale;
            float w = ClientSize.Width;
            float pad = 16f * s;

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(26, 30, 38)))
                g.FillRectangle(bg, ClientRectangle);
            using (Pen border = new Pen(Color.FromArgb(62, 70, 88), 1f))
                g.DrawRectangle(border, 0.5f, 0.5f, w - 1f, ClientSize.Height - 1f);

            if (_mem == null)
            {
                DrawText(g, "正在采集…", _fSub, CDim, pad, pad);
                return;
            }

            DrawText(g, "内存监控", _fTitle, CText, pad, 12f * s);
            DrawTextRight(g, _mem.Time.ToString("HH:mm:ss"), _fSub, CDim, w - pad, 16f * s);
            DrawText(g, string.Format(CultureInfo.InvariantCulture,
                "CPU {0:0.0}%  ·  提交 {1:0.00}/{2:0.00} GB  ·  本工具 {3:0.0} MB",
                _mem.CpuPercent, _mem.CommitUsedGB, _mem.CommitLimitGB, _selfMb),
                _fSub, CDim, pad, 38f * s);

            DrawStats(g, s, pad, w);
            DrawChart(g, s, pad, w);

            DrawText(g, string.Format(CultureInfo.InvariantCulture, "占用最大 {0} 个程序", TrayApp.TopCount), _fSection, CText, pad, 206f * s);
            DrawTextRight(g, _cfg != null && _cfg.UsePrivateMetric ? "口径：专用内存" : "口径：工作集",
                _fMeta, CDim, w - pad, 209f * s);

            DrawRows(g, s, pad, w);
            DrawFooter(g, s, pad, w);
        }

        private void DrawStats(Graphics g, float s, float pad, float w)
        {
            float cellW = (w - 2 * pad) / 4f;
            string[] labels = new string[] { "总内存 (GB)", "可用 (GB)", "已用 (GB)", "使用率" };
            string[] values = new string[]
            {
                _mem.TotalGB.ToString("0.00", CultureInfo.InvariantCulture),
                _mem.AvailGB.ToString("0.00", CultureInfo.InvariantCulture),
                _mem.UsedGB.ToString("0.00", CultureInfo.InvariantCulture),
                _mem.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            };

            for (int i = 0; i < 4; i++)
            {
                float x = pad + i * cellW;
                DrawText(g, labels[i], _fLabel, CDim, x, 62f * s);
                Color c = (i >= 2) ? TrayApp.StatusColorPublic(_mem.Percent) : CText;
                DrawText(g, values[i], _fValue, c, x, 78f * s);
            }
        }

        private void DrawChart(Graphics g, float s, float pad, float w)
        {
            RectangleF rect = new RectangleF(pad, 110f * s, w - 2 * pad, 84f * s);

            using (GraphicsPath bgp = TrayApp.RoundRectPublic(rect, 6f * s))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(28, 34, 44)))
                g.FillPath(b, bgp);

            using (Pen grid = new Pen(Color.FromArgb(34, 44, 56), 1f))
            {
                for (int q = 1; q <= 3; q++)
                {
                    float yy = rect.Bottom - rect.Height * (q * 25f / 100f);
                    g.DrawLine(grid, rect.Left + 1, yy, rect.Right - 1, yy);
                    DrawTextRight(g, (q * 25).ToString(CultureInfo.InvariantCulture) + "%", _fMeta,
                        Color.FromArgb(72, CDim), rect.Right - 4 * s, yy - 12 * s);
                }
            }

            double th = _cfg != null ? _cfg.AlertPercent : 90;
            if (th > 0 && th <= 100)
            {
                float yt = rect.Bottom - (float)(th / 100.0) * rect.Height;
                using (Pen pen = new Pen(Color.FromArgb(110, 231, 76, 60), 1f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawLine(pen, rect.Left + 1, yt, rect.Right - 1, yt);
                }
            }

            DateTime now = DateTime.Now;
            double span = 60.0;
            if (_history.Count > 1)
            {
                double age = (now - _history[0].Time).TotalSeconds;
                if (age > span) span = age;
            }
            if (span > 1800.0) span = 1800.0;
            DateTime t0 = now.AddSeconds(-span);
            List<PointF> pts = new List<PointF>(_history.Count);
            double peak = double.MinValue, valley = double.MaxValue;
            for (int i = 0; i < _history.Count; i++)
            {
                MemSample smp = _history[i];
                double sec = (smp.Time - t0).TotalSeconds;
                if (sec < 0) continue;
                if (smp.Percent > peak) peak = smp.Percent;
                if (smp.Percent < valley) valley = smp.Percent;
                float x = rect.Left + (float)(sec / span) * rect.Width;
                float y = rect.Bottom - (float)(Math.Min(100.0, smp.Percent) / 100.0) * rect.Height;
                pts.Add(new PointF(x, y));
            }

            if (pts.Count >= 2)
            {
                using (GraphicsPath fill = new GraphicsPath())
                {
                    fill.AddLines(pts.ToArray());
                    fill.AddLine(pts[pts.Count - 1].X, rect.Bottom - 1, pts[0].X, rect.Bottom - 1);
                    fill.CloseFigure();
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new PointF(0, rect.Top), new PointF(0, rect.Bottom),
                        Color.FromArgb(95, 76, 141, 246), Color.FromArgb(6, 76, 141, 246)))
                        g.FillPath(lg, fill);
                }
                using (Pen pen = new Pen(Color.FromArgb(235, 96, 165, 250), 1.6f * s))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, pts.ToArray());
                }
            }
            else
            {
                DrawText(g, "正在采集…", _fMeta, CDim, rect.Left + 10 * s, rect.Top + 30 * s);
            }

            string spanText = "近 " + (span >= 1790 ? "30 分钟" : Math.Max(1, (int)Math.Round(span / 60.0)) + " 分钟");
            if (pts.Count >= 2)
                spanText += string.Format(CultureInfo.InvariantCulture, " · 峰 {0:0.0}% · 谷 {1:0.0}%", peak, valley);
            if (th > 0 && th <= 100)
                spanText += " · 阈值 " + th.ToString("0", CultureInfo.InvariantCulture) + "%";
            SizeF ts = g.MeasureString(spanText, _fMeta);
            RectangleF chip = new RectangleF(rect.Left + 4 * s, rect.Top + 3 * s, ts.Width + 6 * s, ts.Height);
            using (GraphicsPath cp = TrayApp.RoundRectPublic(chip, 4f * s))
            using (SolidBrush cb = new SolidBrush(Color.FromArgb(190, 18, 23, 30)))
                g.FillPath(cb, cp);
            DrawText(g, spanText, _fMeta, Color.FromArgb(190, CDim), chip.X + 3 * s, chip.Y);
        }

        private void DrawRows(Graphics g, float s, float pad, float w)
        {
            float rowH = 24f * s;
            float y0 = 226f * s;
            double total = _mem == null || _mem.Total == 0 ? 1 : (double)_mem.Total;
            bool priv = _cfg != null && _cfg.UsePrivateMetric;

            for (int i = 0; i < TrayApp.TopCount; i++)
            {
                float y = y0 + i * rowH;
                using (Pen sep = new Pen(Color.FromArgb(20, 255, 255, 255), 1f))
                    g.DrawLine(sep, pad, y + rowH - 1, w - pad, y + rowH - 1);

                if (i >= _rows.Count)
                {
                    if (i == 0) DrawText(g, "正在采集…", _fRow, CDim, pad, y + 3 * s);
                    continue;
                }

                ProcRow r = _rows[i];
                long bytes = r.Metric(priv);
                double pct = bytes * 100.0 / total;

                DrawText(g, (i + 1).ToString(CultureInfo.InvariantCulture), _fMeta, CDim, pad, y + 5 * s);

                float nameX = pad + 16 * s;
                float nameW = 134 * s;
                string name = r.Count > 1 ? r.Name + " (" + r.Count + ")" : r.Name;
                DrawText(g, Fit(g, name, _fRow, nameW), _fRow, CText, nameX, y + 2 * s);

                float bx = pad + 160 * s;
                float bw = 92 * s;
                float bh = 5 * s;
                float by = y + (rowH - bh) / 2f - 1 * s;
                using (GraphicsPath track = TrayApp.RoundRectPublic(new RectangleF(bx, by, bw, bh), bh / 2f))
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(45, 255, 255, 255)))
                    g.FillPath(tb, track);

                float fillW = (float)(Math.Min(100.0, pct) / 100.0 * bw);
                if (fillW > bh)
                {
                    using (GraphicsPath fp = TrayApp.RoundRectPublic(new RectangleF(bx, by, fillW, bh), bh / 2f))
                    using (SolidBrush fb = new SolidBrush(BarColor(i)))
                        g.FillPath(fb, fp);
                }

                DrawTextRight(g, Fmt.Size(bytes), _fRow, CText, pad + 318 * s, y + 2 * s);
                DrawTextRight(g, pct.ToString("0.0", CultureInfo.InvariantCulture) + "%", _fMeta, CDim, w - pad, y + 5 * s);
            }
        }

        private static Color BarColor(int index)
        {
            switch (index)
            {
                case 0: return Color.FromArgb(235, 231, 76, 60);
                case 1: return Color.FromArgb(235, 241, 196, 15);
                default: return Color.FromArgb(235, 76, 141, 246);
            }
        }

        private void DrawFooter(Graphics g, float s, float pad, float w)
        {
            string text;
            if (_alerts.Count > 0)
            {
                AlertRecord a = _alerts[0];
                string who = "";
                if (a.Top.Count > 0)
                    who = "  ·  " + a.Top[0].Name + " " + Fmt.Size(a.Top[0].Metric(a.MetricPrivate));
                text = string.Format(CultureInfo.InvariantCulture,
                    "最近告警 {0}  ·  内存 {1:0.0}%{2}", a.Time.ToString("HH:mm:ss"), a.Mem.Percent, who);
            }
            else
            {
                double pct = _cfg != null ? _cfg.AlertPercent : 90;
                double avail = _cfg != null ? _cfg.AlertAvailGB : 1.5;
                int sustain = _cfg != null ? _cfg.AlertSustainSeconds : 10;
                text = string.Format(CultureInfo.InvariantCulture,
                    "暂无告警（使用率 ≥{0:0}% 或 可用 ≤{1:0.0} GB，持续 {2}s）", pct, avail, sustain);
            }

            DrawText(g, Fit(g, text, _fFooter, w - 2 * pad), _fFooter,
                Color.FromArgb(140, CDim), pad, (350f + (TrayApp.TopCount - 5) * 24) * s);
        }

        // ---------- 绘制辅助 ----------

        private void DrawText(Graphics g, string text, Font f, Color color, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(color))
                g.DrawString(text, f, b, x, y);
        }

        private void DrawTextRight(Graphics g, string text, Font f, Color color, float right, float y)
        {
            SizeF sz = g.MeasureString(text, f);
            DrawText(g, text, f, color, right - sz.Width, y);
        }

        private string Fit(Graphics g, string text, Font f, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (g.MeasureString(text, f).Width <= maxWidth) return text;
            string t = text;
            while (t.Length > 0 && g.MeasureString(t + "…", f).Width > maxWidth)
                t = t.Substring(0, t.Length - 1);
            return t + "…";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Font[] fonts = new Font[] { _fTitle, _fSub, _fLabel, _fValue, _fSection, _fRow, _fMeta, _fFooter };
                for (int i = 0; i < fonts.Length; i++)
                {
                    if (fonts[i] != null) fonts[i].Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
