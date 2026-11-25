using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Memory Clock")]
[assembly: AssemblyDescription("Windows memory cleaner based on Mem Reduct · GNU GPLv3")]
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

namespace MemoryClock
{
    internal static class WindowCaption
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal static void Apply(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            bool contrast = SystemParameters.HighContrast;
            int dark = contrast ? 0 : 1;
            // Keep the native frame, caption buttons, snapping and system menu.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            Color color = ((SolidColorBrush)window.Background).Color;
            int background = contrast ? -1 : color.R | (color.G << 8) | (color.B << 16);
            int foreground = contrast ? -1 : 0xE9F1EC;
            DwmSetWindowAttribute(handle, 35, ref background, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref foreground, sizeof(int));
        }
    }

    internal sealed class LaunchRequest
    {
        internal readonly bool Enable, Compact;
        internal readonly Settings Choice;
        internal LaunchRequest(bool enable, bool compact, Settings choice)
        { Enable = enable; Compact = compact; Choice = choice; }

        internal string Arguments
        { get { return "--elevated " + (Enable ? "enable" : "clean") + " " + Choice.Mode + " " + Choice.Display + " " + (Compact ? "compact" : "full"); } }

        internal static LaunchRequest Parse(string[] args)
        {
            if (args.Length != 5 || args[0] != "--elevated" ||
                (args[1] != "enable" && args[1] != "clean") || (args[2] != "Gentle" && args[2] != "Strong") ||
                (args[4] != "compact" && args[4] != "full")) return null;
            try
            {
                return new LaunchRequest(args[1] == "enable", args[4] == "compact",
                    Settings.Parse(args[3], args[2] == "Strong" ? CleanMode.Strong : CleanMode.Gentle));
            }
            catch (FormatException) { return null; }
        }
    }

    internal static class Program
    {
        internal static bool IsTrayLaunch(string[] args)
        { return args.Length == 1 && args[0] == StartupEntry.TrayOption; }

        internal static void Present(Dashboard dashboard, bool trayOnly, LaunchRequest request)
        {
            if (trayOnly) return;
            if (request != null && request.Compact) dashboard.ShowCompact(); else dashboard.View.Show();
            if (request != null) dashboard.View.Dispatcher.BeginInvoke(new Action(() => dashboard.ApplyRequest(request)));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            LaunchRequest request = args.Length == 0 ? null : LaunchRequest.Parse(args);
            bool trayOnly = IsTrayLaunch(args);
            bool admin = WindowsMemory.IsAdmin();
            if (args.Length != 0 && !trayOnly && (request == null || !admin)) return 2;
            string sid;
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User.Value;
            string scope = @"Local\MemoryClock-1-" + sid;
            try
            {
                using (Mutex mutex = new Mutex(false, scope))
                using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, scope + "-Show"))
                {
                    bool owns;
                    try { owns = mutex.WaitOne(request != null ? 10000 : 0); }
                    catch (AbandonedMutexException) { owns = true; }
                    if (!owns) { if (!trayOnly) show.Set(); return 0; }
                    try
                    {
                        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryClock", "schedule.txt");
                        using (var dashboard = new Dashboard(path, admin, WindowsMemory.Read, WindowsMemory.Clean, show, true))
                        {
                            application.SessionEnding += (sender, evt) => dashboard.AllowShutdown();
                            application.MainWindow = dashboard.View;
                            Present(dashboard, trayOnly, request);
                            return application.Run();
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                MessageBox.Show("内存时钟无法启动：" + error.Message, "内存时钟", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    internal sealed class Dashboard : IDisposable
    {
        internal readonly Window View;
        private const string TimeHelp = "10–86400 秒 · 开启先清一次，完后重计";
        private readonly string settingsPath;
        private readonly bool admin;
        private readonly Func<Reading?> read;
        private readonly Func<CleanMode, CleanResult> clean;
        private readonly Func<string> readPageFile;
        private readonly Action openPageFileSettings;
        private readonly Func<long> monotonic;
        private readonly EventWaitHandle show;
        private readonly IntervalTimer schedule = new IntervalTimer();
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripMenuItem trayPause;
        private readonly System.Drawing.Icon icon;
        private readonly TextBox seconds;
        private readonly RadioButton gentle, strong;
        private readonly Button arm, run;
        private readonly StartupEntry startupEntry;
        private readonly bool withTray;
        private bool closing, busy, failed;
        private Settings settings;
        private long? nextReading, cleanedUntil, lastPageFileRead;
        private bool readingPageFile;
        private Window compact;
        private Reading? currentMemory;
        private string resultMessage;

        internal Dashboard(string path, bool admin, Func<Reading?> read, Func<CleanMode, CleanResult> clean, EventWaitHandle show, bool withTray,
            Func<long> monotonic = null, Func<string> readPageFile = null, Action openPageFileSettings = null, StartupEntry startupEntry = null)
        {
            this.admin = admin; settingsPath = path; this.read = read; this.clean = clean; this.show = show; this.withTray = withTray;
            this.monotonic = monotonic ?? Stopwatch.GetTimestamp;
            this.readPageFile = readPageFile ?? PageFile.ReadSummary;
            this.openPageFileSettings = openPageFileSettings ?? PageFile.OpenSettings;
            this.startupEntry = startupEntry ?? new StartupEntry(Assembly.GetExecutingAssembly().Location);
            using (Stream layout = typeof(Dashboard).Assembly.GetManifestResourceStream("MemoryClock.Dashboard"))
                View = (Window)XamlReader.Load(layout);
            View.SourceInitialized += (sender, evt) => WindowCaption.Apply(View);
            PropertyChangedEventHandler contrastChanged = (sender, evt) =>
            {
                if (evt.PropertyName == "HighContrast") WindowCaption.Apply(View);
            };
            SystemParameters.StaticPropertyChanged += contrastChanged;
            View.Closed += (sender, evt) => SystemParameters.StaticPropertyChanged -= contrastChanged;
            seconds = Find<TextBox>("Seconds"); gentle = Find<RadioButton>("Gentle"); strong = Find<RadioButton>("Strong");
            arm = Find<Button>("Arm"); run = Find<Button>("Clean");
            string error = null;
            try { settings = Settings.Load(path); }
            catch (Exception issue) { settings = Settings.Default; error = issue.Message; }
            seconds.Text = settings.Display;
            if (settings.Mode == CleanMode.Strong) strong.IsChecked = true; else gentle.IsChecked = true;
            seconds.TextChanged += (sender, evt) => SetHint(TimeHelp, false);
            seconds.KeyDown += (sender, evt) =>
            {
                if (evt.Key == Key.Enter && !schedule.Active) { evt.Handled = true; ToggleSchedule(); }
            };
            arm.Click += (sender, evt) => ToggleSchedule();
            run.Click += (sender, evt) => ManualClean();
            Find<Button>("Compact").Click += (sender, evt) => ShowCompact();
            Find<Button>("PageFileSettings").Click += (sender, evt) => OpenPageFileSettings();
            var startup = Find<CheckBox>("Startup");
            startup.Click += (sender, evt) =>
            {
                try { this.startupEntry.Set(startup.IsChecked == true); }
                catch (Exception e) { MessageBox.Show(View, e.Message, "开机自启", MessageBoxButton.OK, MessageBoxImage.Warning); }
                RefreshStartup();
            };
            View.Loaded += (sender, evt) => RefreshPageFile();
            View.Activated += (sender, evt) => { RefreshPageFile(); RefreshStartup(); };
            RefreshStartup();
            if (withTray)
            {
                icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                tray = new Forms.NotifyIcon { Icon = icon, Text = "内存时钟", ContextMenuStrip = new Forms.ContextMenuStrip() };
                tray.ContextMenuStrip.Items.Add("打开内存时钟", null, (sender, evt) => ShowWindow());
                trayPause = (Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items.Add("暂停定时", null, (sender, evt) => Pause());
                tray.ContextMenuStrip.Items.Add("退出", null, (sender, evt) => Exit());
                tray.DoubleClick += (sender, evt) => ShowWindow();
                tray.Visible = true;
                View.Closing += (sender, evt) => { if (!closing) { evt.Cancel = true; View.Hide(); } };
            }
            if (error != null) SetHint(error, true);
            Text("Permission", admin ? "已授权 · 关闭窗口后留在托盘" : "首次清理或启用定时时，需要管理员授权。");
            if (!admin) run.Content = "授权清理";
            timer = new DispatcherTimer(DispatcherPriority.Background, View.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (sender, evt) => Tick();
            UpdateUI();
            PaintMemory(read());
            if (withTray) timer.Start();
        }

        internal T Find<T>(string name) where T : class { return (T)View.FindName(name); }
        private void RefreshStartup()
        {
            var control = Find<CheckBox>("Startup");
            try
            {
                StartupState state = startupEntry.Read();
                control.IsChecked = state == StartupState.Registered;
                control.IsEnabled = state != StartupState.Conflict;
                control.ToolTip = state == StartupState.Conflict ? "已有其他版本的启动项，请先从原版本关闭。" : "登录 Windows 时启动到托盘；不会自动清理。";
            }
            catch (Exception)
            {
                control.IsChecked = false; control.IsEnabled = false; control.ToolTip = "无法读取当前用户的 Windows 启动项。";
            }
        }
        private void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        private CleanMode Selected { get { return strong.IsChecked == true ? CleanMode.Strong : CleanMode.Gentle; } }
        private void SetHint(string message, bool error)
        {
            TextBlock hint = Find<TextBlock>("TimeHint");
            hint.Text = message;
            hint.Foreground = error ? Brushes.LightSalmon : (Brush)View.Resources["Muted"];
            seconds.BorderBrush = error ? Brushes.LightSalmon : Brushes.Transparent;
            if (error) Announce(hint);
        }
        private static void Announce(TextBlock block)
        {
            AutomationPeer peer = UIElementAutomationPeer.FromElement(block);
            if (peer != null) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        private void SetResult(string message)
        {
            resultMessage = message;
            Text("Result", message);
            Announce(Find<TextBlock>("Result"));
        }

        private bool SaveInterval()
        {
            if (schedule.Active) return false;
            try
            {
                Settings chosen = Settings.Parse(seconds.Text, Selected);
                chosen.Save(settingsPath); settings = chosen; seconds.Text = chosen.Display;
                SetHint(TimeHelp, false);
                return true;
            }
            catch (Exception error) { SetHint(error.Message, true); seconds.Focus(); return false; }
        }
        private void ToggleSchedule()
        {
            if (schedule.Active) { Pause(); return; }
            if (busy || !SaveInterval()) return;
            RequestClean(true, settings);
        }
        private void Arm()
        {
            if (!admin || busy || closing || schedule.Active) return;
            schedule.Arm(settings, monotonic());
            SetHint(TimeHelp, false);
            StartClean(settings.Mode, "定时");
        }
        internal void Pause()
        {
            schedule.Pause(); UpdateUI();
        }
        private void ManualClean()
        {
            if (!busy) RequestClean(false, settings.WithMode(Selected));
        }

        private void RefreshPageFile()
        {
            long now = monotonic();
            if (closing || readingPageFile || (lastPageFileRead.HasValue && now - lastPageFileRead.Value < Stopwatch.Frequency)) return;
            readingPageFile = true;
            Task.Factory.StartNew(() =>
            {
                try { return readPageFile(); }
                catch { return "暂时无法读取 Windows 分页文件"; }
            }).ContinueWith(task =>
            {
                if (!View.Dispatcher.HasShutdownStarted)
                    View.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        readingPageFile = false;
                        lastPageFileRead = monotonic();
                        Text("PageFileSummary", task.Result);
                    }));
            });
        }

        private void OpenPageFileSettings()
        {
            var hint = Find<TextBlock>("PageFileHint");
            try
            {
                openPageFileSettings();
                lastPageFileRead = null;
                hint.ClearValue(TextBlock.ForegroundProperty);
                hint.Text = "在 Windows 窗口里选「高级 → 虚拟内存 → 更改」。";
            }
            catch (Win32Exception error)
            {
                hint.Foreground = Brushes.LightSalmon;
                hint.Text = error.NativeErrorCode == 1223 ? "已取消打开 Windows 设置。" : "无法打开性能选项，请从 Windows 设置打开。";
            }
            Announce(hint);
        }
        private void RequestClean(bool enable, Settings choice)
        {
            var request = new LaunchRequest(enable, compact != null && compact.IsVisible, choice);
            if (admin) ApplyRequest(request); else Elevate(request);
        }
        internal void ApplyRequest(LaunchRequest request)
        {
            if (!admin || busy || closing || (request.Enable && schedule.Active)) return;
            if (request.Enable)
            {
                settings = request.Choice;
                seconds.Text = settings.Display;
                GentleOrStrong(settings.Mode);
                Arm();
            }
            else
            {
                GentleOrStrong(request.Choice.Mode);
                StartClean(request.Choice.Mode, "手动");
            }
        }
        private void GentleOrStrong(CleanMode mode)
        {
            if (mode == CleanMode.Strong) strong.IsChecked = true; else gentle.IsChecked = true;
        }
        private void Elevate(LaunchRequest request)
        {
            try
            {
                using (Process next = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, request.Arguments) { UseShellExecute = true, Verb = "runas" }))
                {
                    if (next == null) throw new InvalidOperationException("未能打开管理员窗口。");
                }
                Exit();
            }
            catch (Win32Exception issue)
            {
                failed = true;
                SetResult(issue.NativeErrorCode == 1223 ? "已取消管理员授权。" : "管理员授权未完成：" + issue.Message);
                UpdateUI();
            }
            catch (InvalidOperationException issue) { failed = true; SetResult(issue.Message); UpdateUI(); }
        }

        internal void Tick()
        {
            if (show != null && show.WaitOne(0)) ShowWindow();
            Reading? measurement = read();
            PaintMemory(measurement);
            if (nextReading.HasValue && monotonic() >= nextReading.Value)
            {
                Text("Later", measurement.HasValue ? Gb(measurement.Value.Available) : "读取失败");
                nextReading = null;
            }
            if (!busy && schedule.IsDue(monotonic())) StartClean(settings.Mode, "定时");
            UpdateUI();
        }
        private void StartClean(CleanMode chosen, string source)
        {
            if (!admin || closing || busy) return;
            schedule.Started();
            busy = true; failed = false; nextReading = null; cleanedUntil = null;
            Text("Before", "—"); Text("After", "—"); Text("Later", "—");
            SetResult(source + "清理中…");
            UpdateUI();
            Task.Factory.StartNew(() =>
            {
                try { return clean(chosen); }
                catch (Exception issue) { return new CleanResult("清理未完成：" + issue.Message, false, null, null); }
            }).ContinueWith(task =>
            {
                if (!View.Dispatcher.HasShutdownStarted)
                    View.Dispatcher.BeginInvoke(new Action(() => FinishClean(task.Result, source)));
            });
        }
        private void FinishClean(CleanResult outcome, string source)
        {
            busy = false; failed = !outcome.Success;
            bool stopped = !outcome.Success && schedule.Active;
            if (stopped) schedule.Pause();
            if (outcome.Success)
            {
                long now = monotonic();
                schedule.Finished(now);
                cleanedUntil = now + 8 * Stopwatch.Frequency;
            }
            Text("Before", outcome.Before.HasValue ? Gb(outcome.Before.Value.Available) : "—");
            Text("After", outcome.After.HasValue ? Gb(outcome.After.Value.Available) : "—");
            if (outcome.After.HasValue)
            {
                nextReading = monotonic() + 30 * Stopwatch.Frequency;
                Text("Later", schedule.Active && settings.Seconds < 30 ? "间隔不足" : "等待读数");
                PaintMemory(outcome.After);
            }
            SetResult(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + source + " · " + outcome.Summary
                + (stopped ? "；定时已暂停。" : ""));
            UpdateUI();
        }

        private static string Gb(ulong bytes) { return (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB"; }
        private void PaintMemory(Reading? measurement)
        {
            currentMemory = measurement;
            double p = measurement.HasValue ? measurement.Value.UsedFraction * 100.0 : 0;
            Text("Usage", measurement.HasValue ? p.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—");
            Find<ProgressBar>("Meter").Value = p;
            Find<Image>("MiniDial").Source = DialArt.Draw(measurement.HasValue ? (double?)measurement.Value.UsedFraction : null);
            Text("Available", measurement.HasValue ? Gb(measurement.Value.Available) : "—");
            Text("Physical", measurement.HasValue ? Gb(measurement.Value.Total) : "—");
            Text("Commit", measurement.HasValue ? Gb(measurement.Value.Committed) + " / " + Gb(measurement.Value.CommitLimit) : "—");
            PaintCompact();
        }
        private void UpdateUI()
        {
            bool enabled = schedule.Active;
            Text("ScheduleState", enabled ? "定时已启用" : "定时未启用");
            Find<Ellipse>("ScheduleDot").Fill = (Brush)View.Resources[enabled ? "Lime" : "Muted"];
            int? next = schedule.SecondsLeft(monotonic());
            TextBlock nextLabel = Find<TextBlock>("Next");
            nextLabel.Text = !enabled ? "下次清理：未启用" : next.HasValue ? "下次清理：" + next.Value + " 秒后" : "本轮正在清理…";
            nextLabel.Foreground = (Brush)View.Resources[enabled ? "Amber" : "Muted"];
            arm.Content = enabled ? "暂停定时" : "启用定时";
            seconds.IsEnabled = gentle.IsEnabled = strong.IsEnabled = !enabled;
            arm.IsEnabled = enabled || !busy;
            run.IsEnabled = !busy;
            if (admin) run.Content = busy ? "正在清理…" : "立即清理";
            if (trayPause != null) trayPause.Enabled = enabled;
            UpdateCompact();
        }

        internal Window CompactView { get { return compact; } }

        internal void ShowCompact()
        {
            if (closing) return;
            if (compact == null)
            {
                using (Stream layout = typeof(Dashboard).Assembly.GetManifestResourceStream("MemoryClock.Compact"))
                    compact = (Window)XamlReader.Load(layout);
                Rect area = SystemParameters.WorkArea;
                compact.Left = withTray ? area.Right - compact.Width - 22 : -10000;
                compact.Top = withTray ? area.Bottom - compact.Height - 22 : -10000;
                compact.ShowActivated = withTray;
                compact.Topmost = withTray;
                ((FrameworkElement)compact.FindName("Handle")).MouseLeftButtonDown += (sender, evt) =>
                {
                    if (evt.LeftButton == MouseButtonState.Pressed) compact.DragMove();
                };
                ((Button)compact.FindName("Clean")).Click += (sender, evt) => ManualClean();
                ((Button)compact.FindName("Expand")).Click += (sender, evt) => ShowWindow();
                compact.Closing += (sender, evt) => { if (!closing) { evt.Cancel = true; compact.Hide(); } };
                compact.Closed += (sender, evt) => compact = null;
                PaintCompact();
                UpdateCompact();
            }
            if (View.IsVisible) View.Hide();
            compact.Show();
        }

        private static string DialGb(ulong bytes)
        { return (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture); }

        private void PaintCompact()
        {
            if (compact == null) return;
            TextBlock used = (TextBlock)compact.FindName("Used"), free = (TextBlock)compact.FindName("Free");
            used.Text = currentMemory.HasValue ? DialGb(currentMemory.Value.Total - Math.Min(currentMemory.Value.Total, currentMemory.Value.Available)) : "—";
            free.Text = currentMemory.HasValue ? DialGb(currentMemory.Value.Available) : "—";
            ((TextBlock)compact.FindName("DialPercent")).Text = currentMemory.HasValue ?
                currentMemory.Value.UsedFraction.ToString("0%", CultureInfo.InvariantCulture) : "—";
            ((Image)compact.FindName("Dial")).Source = Find<Image>("MiniDial").Source;
        }

        private void UpdateCompact()
        {
            if (compact == null) return;
            var button = (Button)compact.FindName("Clean");
            button.IsEnabled = !busy;
            button.ToolTip = admin ? "立即清理内存" : "授权后立即清理内存";
            var status = (TextBlock)compact.FindName("Status");
            bool justCleaned = cleanedUntil.HasValue && monotonic() < cleanedUntil.Value;
            string label = busy ? "正在清理" : failed ? "未完成" : justCleaned ? "刚清理完" : schedule.Active ? "定时中" : "就绪";
            if (status.Text != label)
            {
                status.Text = label;
                Announce(status);
            }
            status.Foreground = busy ? (Brush)View.Resources["Amber"] : failed ? Brushes.LightSalmon
                : justCleaned ? (Brush)View.Resources["Lime"] : (Brush)View.Resources[schedule.Active ? "Azure" : "Muted"];
            status.ToolTip = resultMessage;
        }

        private void ShowWindow()
        {
            if (compact != null && compact.IsVisible) compact.Hide();
            if (!View.IsVisible) View.Show();
            if (View.WindowState == WindowState.Minimized) View.WindowState = WindowState.Normal;
            if (withTray) View.Activate();
        }
        private void Exit()
        {
            if (busy) { SetResult("正在清理，请等本轮结束再退出。"); return; }
            closing = true; View.Close();
        }
        internal void AllowShutdown() { closing = true; }
        public void Dispose()
        {
            timer.Stop();
            closing = true;
            if (compact != null) compact.Close();
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); icon.Dispose(); }
        }
    }
}
