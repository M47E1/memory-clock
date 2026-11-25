using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MemoryClock
{
    internal static class Verification
    {
        private static void Need(bool condition, string message)
        { if (!condition) throw new Exception(message); }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowBounds { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WindowBounds bounds);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr target, uint flags);

        private static void CheckCaption(Window view, string output)
        {
            if (Environment.OSVersion.Version.Build < 22000 || SystemParameters.HighContrast) return;
            IntPtr hwnd = new WindowInteropHelper(view).Handle;
            WindowBounds bounds;
            Need(GetWindowRect(hwnd, out bounds) && view.WindowStyle == WindowStyle.SingleBorderWindow,
                "Expected a standard native window.");
            Color expected = ((SolidColorBrush)view.Background).Color;
            using (var frame = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
            {
                using (var g = System.Drawing.Graphics.FromImage(frame))
                {
                    var dc = g.GetHdc();
                    try { Need(PrintWindow(hwnd, dc, 2), "Could not render the offscreen test window."); }
                    finally { g.ReleaseHdc(dc); }
                }
                frame.Save(Path.Combine(output, "window.png"), System.Drawing.Imaging.ImageFormat.Png);
                var clientStart = view.PointToScreen(new Point(0,0));
                int y = (int)(clientStart.Y - bounds.Top) / 2;
                var pixel = frame.GetPixel(frame.Width / 2, y);
                Need(pixel.R == expected.R && pixel.G == expected.G && pixel.B == expected.B,
                    "Native caption pixel: " + pixel + ", expected " + expected);
                Console.WriteLine("Native caption pixel matches dashboard: " + expected);
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                CheckSchedule();
                CheckEngine();
                CheckPageFile();
                CheckWindow(Path.GetFullPath(args[0]));
                application.Shutdown();
                Console.WriteLine("PASS: login startup switch and tray launch, interval control, compact dial, and page-file settings. Cleaning was simulated.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void CheckSchedule()
        {
            Settings settings = Settings.Parse(" 0500 ", CleanMode.Strong);
            Need(settings.Display == "500" && Settings.Parse("86400", CleanMode.Gentle).Seconds == 86400, "Interval was not normalized.");
            foreach (string input in new[] { "", "9", "0", "24:00", "10.5", "86401", "+500", "-40" })
            {
                try { Settings.Parse(input, CleanMode.Gentle); throw new Exception("Invalid schedule accepted: " + input); }
                catch (FormatException) { }
            }
            long s = Stopwatch.Frequency;
            var clock = new IntervalTimer();
            Need(!clock.IsDue(0), "Disarmed timer fired.");
            clock.Arm(settings, 7 * s);
            Need(clock.IsDue(7 * s), "Enabling did not make the first run immediately due.");
            clock.Started();
            Need(!clock.IsDue(5000 * s) && clock.SecondsLeft(10 * s) == null, "In-flight run allowed overlap.");
            clock.Finished(10 * s);
            Need(!clock.IsDue(510 * s - 1) && clock.IsDue(510 * s) && clock.SecondsLeft(509 * s) == 1, "Wrong next interval.");
            clock.Started(); clock.Finished(9000 * s);
            Need(!clock.IsDue(9000 * s) && clock.SecondsLeft(9000 * s) == 500, "Missed intervals were replayed in a burst.");
            clock.Pause();
            clock.Finished(9000 * s);
            Need(!clock.IsDue(9500 * s) && clock.SecondsLeft(9500 * s) == null, "Finishing a paused run restarted the timer.");
            var request = new LaunchRequest(true, true, settings);
            LaunchRequest resumed = LaunchRequest.Parse(request.Arguments.Split(' '));
            Need(resumed != null && resumed.Enable && resumed.Compact && resumed.Choice.Seconds == 500 &&
                resumed.Choice.Mode == CleanMode.Strong, "Elevation did not carry the requested action, view, and mode.");
            Need(LaunchRequest.Parse("--elevated enable Gentle 9 full".Split(' ')) == null &&
                LaunchRequest.Parse("--elevated other Strong 500 compact".Split(' ')) == null, "Invalid elevation requests were accepted.");
        }

        private static void CheckEngine()
        {
            var actions = new List<Area>();
            var samples = new Queue<Reading?>(new Reading?[] { new Reading { Available = 6 }, new Reading { Available = 8 } });
            CleanResult strong = Cleaner.Run(CleanMode.Strong, () => samples.Dequeue(), actions.Add);
            Need(strong.Success && strong.Before.Value.Available == 6 && strong.After.Value.Available == 8
                && actions.SequenceEqual(new[] { Area.WorkingSets, Area.FileCache, Area.LowStandby }), "Strong mode actions were mismatched.");
            actions.Clear();
            Cleaner.Run(CleanMode.Gentle, () => null, actions.Add);
            Need(actions.SequenceEqual(new[] { Area.FileCache, Area.LowStandby }), "Gentle mode touched application worksets.");
            actions.Clear();
            CleanResult failed = Cleaner.Run(CleanMode.Strong, () => null, area => { actions.Add(area); throw new IOException("拒绝执行"); });
            Need(!failed.Success && actions.SequenceEqual(new[] { Area.WorkingSets }), "Engine did not respect denial.");
            Reading? current = WindowsMemory.Read();
            Need(current.HasValue && current.Value.Total > 0 && current.Value.Available <= current.Value.Total &&
                current.Value.Committed > 0 && current.Value.CommitLimit > 0, "Windows memory metrics unavailable or out of range.");
            if (!WindowsMemory.IsAdmin()) Need(!WindowsMemory.Clean(CleanMode.Strong).Success, "Non-admin cleaning was allowed.");
        }

        private static void CheckPageFile()
        {
            var active = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { { @"C:\pagefile.sys", 32768 } };
            var configured = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { { @"c:\PAGEFILE.SYS", 65536 } };
            Need(PageFile.FormatSummary(active, configured) == "C: · 已分配 32 GB · 设定上限 64 GB", "Active and configured page-file sizes were mixed up.");
            active[@"D:\pagefile.sys"] = 4096;
            Need(PageFile.FormatSummary(active, configured) == "C: / D: · 已分配 36 GB", "Multi-disk page-file allocation was not shown correctly.");
            Need(File.Exists(PageFile.PerformanceSettingsPath), "Windows Performance Options entry point is missing.");
            string observed = PageFile.ReadSummary();
            Need(observed.Contains("已分配") || observed == "当前未分配分页文件", "Windows page-file allocation did not read back.");
            Console.WriteLine("Windows paging: " + observed);
        }

        private static void CheckPanelLayout(Dashboard page)
        {
            Window view = page.View;
            Refresh(view);
            var memory = page.Find<Border>("MemoryBlock");
            var result = page.Find<Border>("ResultBlock");
            var controls = page.Find<Border>("ControlsBlock");
            var disk = page.Find<Border>("DiskBlock");
            double top = memory.TranslatePoint(new Point(), view).Y;
            double bottom = result.TranslatePoint(new Point(0, result.ActualHeight), view).Y;
            double rightTop = controls.TranslatePoint(new Point(), view).Y;
            double rightBottom = controls.TranslatePoint(new Point(0, controls.ActualHeight), view).Y;
            Need(Math.Abs(top - rightTop) < .51 && Math.Abs(bottom - rightBottom) < .51,
                "Panel edges are not aligned at " + view.ActualWidth + "x" + view.ActualHeight + ".");
            Need(disk.TranslatePoint(new Point(), view).Y - bottom >= 11, "Missing gutter above disk panel.");
            foreach (Border block in new[] { memory, result, controls, disk, page.Find<Border>("TimerBlock") })
                Need(block.Background is SolidColorBrush && block.BorderThickness == new Thickness(0),
                    "Panels must have solid fills without decorative strokes.");
            Console.WriteLine("Aligned at {0}x{1}: top {2}, bottom {3} DIP.", view.ActualWidth, view.ActualHeight, top, bottom);
        }

        private static void CheckStartup(CheckBox toggle, StartupEntry startup, string key, string exe)
        {
            Need(toggle.IsTabStop && toggle.IsEnabled && toggle.IsChecked == false &&
                AutomationProperties.GetName(toggle) == "开机自启" && startup.Read() == StartupState.Off, "Startup is not opt-in.");
            try
            {
                toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using (var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                    Need(startup.Read() == StartupState.Registered && toggle.IsChecked == true && reg != null &&
                        (string)reg.GetValue("MemoryClock") == "\"" + exe + "\" --tray", "Bad login command.");
                toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Need(startup.Read() == StartupState.Off, "Disabling did not remove the login command.");
                using (var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true)) reg.SetValue("MemoryClock", "other");
                foreach (bool on in new[] { true, false })
                {
                    try { startup.Set(on); throw new Exception("Replaced a conflicting startup entry."); }
                    catch (IOException) { }
                }
                using (var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                    Need((string)reg.GetValue("MemoryClock") == "other", "Conflicting startup entry was modified.");
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(key, false); }
        }

        private static void CheckWindow(string output)
        {
            string temp = Path.Combine(output, Guid.NewGuid().ToString("N"));
            string settingsPath = Path.Combine(temp, "schedule.txt");
            int executed = 0;
            CleanMode actual = CleanMode.Gentle;
            bool fail = false;
            long elapsed = 0;
            bool followup = false;
            int settingsOpened = 0;
            string key = @"Software\MemoryClock.Test." + Guid.NewGuid().ToString("N");
            string exe = Path.Combine(temp, "test app", "MemoryClock.exe");
            var startup = new StartupEntry(exe, key);
            using (var page = new Dashboard(settingsPath, true, () => new Reading { Total = 16UL << 30,
                Available = (followup ? 5UL : 6UL) << 30, Committed = 18UL << 30, CommitLimit = 48UL << 30 }, mode =>
            {
                actual = mode;
                bool denied = fail;
                Interlocked.Increment(ref executed);
                return new CleanResult(denied ? "模拟拒绝" : "已完成", !denied,
                    new Reading { Total = 16UL << 30, Available = 4UL << 30 },
                    new Reading { Total = 16UL << 30, Available = 6UL << 30 });
            }, null, false, () => elapsed, () => "C: · 已分配 32 GB · 设定上限 64 GB", () => settingsOpened++, startup))
            {
                Window view = page.View;
                view.WindowStartupLocation = WindowStartupLocation.Manual;
                view.Left = -10000; view.Top = -10000; view.Topmost = false; view.ShowActivated = false;
                view.ShowInTaskbar = false;
                Need(Program.IsTrayLaunch(new[] { "--tray" }) && !Program.IsTrayLaunch(new[] { "--tray", "clean" }), "Unexpected tray arguments.");
                Program.Present(page, true, null);
                page.Tick();
                Need(!view.IsVisible && page.CompactView == null && executed == 0, "Tray startup opened a window or cleaned.");
                Program.Present(page, false, null);
                try
                {
                    Refresh(view);
                    CheckStartup(page.Find<CheckBox>("Startup"), startup, key, exe);
                    Need(view.Left == -10000 && view.Top == -10000 && !view.IsActive, "UI verification became visible or active.");
                    Need(page.Find<TextBlock>("Usage").Text.EndsWith("%") && page.Find<ProgressBar>("Meter").ActualWidth > 200, "Live dashboard did not render.");
                    TextBox seconds = page.Find<TextBox>("Seconds");
                    Button arm = page.Find<Button>("Arm");
                    Button minimize = page.Find<Button>("Compact");
                    Need(AutomationProperties.GetName(seconds) == "清理间隔（秒）" && seconds.IsTabStop && arm.IsTabStop, "Schedule inputs are not keyboard-addressable.");
                    Need(AutomationProperties.GetName(minimize) == "最小化为环" && page.Find<Image>("MiniDial").Source != null, "Minimize button does not preview the live dial.");
                    var caption = page.Find<TextBlock>("CompactLabel");
                    var ring = page.Find<Image>("MiniDial");
                    Need(minimize.IsTabStop && caption.IsVisible && caption.Text == "最小化" &&
                        caption.TranslatePoint(new Point(), minimize).Y >= ring.TranslatePoint(new Point(0, ring.ActualHeight), minimize).Y,
                        "Minimize must show its label below the ring in one keyboard-accessible button.");
                    Need(((string)page.Find<RadioButton>("Gentle").Content).Contains("只清") &&
                        ((string)page.Find<RadioButton>("Strong").Content).Contains("额外腾出应用内存"), "Cleaning modes were not distinguished in plain language.");
                    WaitFor(() => page.Find<TextBlock>("PageFileSummary").Text.Contains("32 GB"));
                    page.Find<Button>("PageFileSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var pageHint = page.Find<TextBlock>("PageFileHint");
                    Need(settingsOpened == 1 && pageHint.Text.Contains("高级"), "Page-file settings action was not routed.");
                    Need(object.ReferenceEquals(pageHint.Foreground, view.Resources["Muted"]),
                        "PageFileHint must keep the Muted style foreground after ClearValue (set Style=Caption, not a local Foreground).");
                    foreach (string wrapping in new[] { "Result", "TimeHint", "PageFileHint", "PageFileSummary" })
                        Need(page.Find<TextBlock>(wrapping).TextWrapping == TextWrapping.Wrap,
                            wrapping + " must wrap so long runtime messages do not clip in the compact layout.");
                    Refresh(view);
                    Need(view.ActualWidth < 800 && view.ActualHeight < 580, "Default dashboard must stay compact.");
                    Need(((ScrollViewer)view.Content).ScrollableHeight == 0, "Dashboard needs scrolling at its default size.");
                    CheckPanelLayout(page);
                    Capture((FrameworkElement)view.Content, Path.Combine(output, "dashboard.png"), ((SolidColorBrush)view.Background).Color);
                    CheckCaption(view, output);
                    view.Width = 1120; view.Height = 800; CheckPanelLayout(page);
                    view.Width = view.MinWidth; view.Height = view.MinHeight; Refresh(view);
                    CheckPanelLayout(page);
                    Need(((ScrollViewer)view.Content).ScrollableWidth == 0 &&
                        page.Find<Button>("Clean").ActualWidth > 250, "Dashboard clips at minimum width.");
                    seconds.Text = "9"; arm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    CheckPanelLayout(page);
                    Need(executed == 0 && !File.Exists(settingsPath) && page.Find<TextBlock>("TimeHint").Text.Contains("整数秒数"), "Invalid interval was accepted.");
                    seconds.Text = "60";
                    arm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Settings saved = Settings.Load(settingsPath);
                    Need(saved.Mode == CleanMode.Gentle && saved.Seconds == 60, "Enabling did not save interval and mode.");
                    WaitFor(() => page.Find<TextBlock>("After").Text == "6.00 GB");
                    Need(executed == 1 && actual == CleanMode.Gentle && !seconds.IsEnabled &&
                        page.Find<TextBlock>("Next").Text.Contains("60 秒后") &&
                        page.Find<TextBlock>("Before").Text == "4.00 GB" && page.Find<TextBlock>("Later").Text == "等待读数",
                        "Enabling did not clean immediately and start the countdown.");
                    minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Window mini = page.CompactView;
                    Refresh(mini);
                    var clean = (Button)mini.FindName("Clean");
                    Need(!view.IsVisible && mini.IsVisible && mini.ActualWidth <= 108 && mini.ActualHeight <= 128 &&
                        mini.Left == -10000 && mini.Top == -10000 && !mini.IsActive && !mini.Topmost,
                        "Compact window did not replace the dashboard offscreen at compact size.");
                    Need((string)clean.Content == "Clean" && clean.IsTabStop &&
                        ((TextBlock)mini.FindName("Used")).Text == "10.0" &&
                        ((TextBlock)mini.FindName("Free")).Text == "6.0" &&
                        ((TextBlock)mini.FindName("DialPercent")).Text == (.625).ToString("0%", System.Globalization.CultureInfo.InvariantCulture) &&
                        object.ReferenceEquals(((Image)mini.FindName("Dial")).Source, ring.Source) &&
                        ((TextBlock)mini.FindName("Status")).Text == "刚清理完",
                        "Compact view did not show the shared memory reading and Clean control.");
                    Capture((FrameworkElement)mini.Content, Path.Combine(output, "compact.png"), ((SolidColorBrush)view.Background).Color);
                    followup = true;
                    elapsed = Stopwatch.Frequency * 29;
                    page.Tick();
                    Need(page.Find<TextBlock>("Later").Text == "等待读数" && ((TextBlock)mini.FindName("Status")).Text == "定时中",
                        "Follow-up came too early or the recent status did not expire.");
                    elapsed = Stopwatch.Frequency * 30;
                    page.Tick();
                    Need(page.Find<TextBlock>("Later").Text == "5.00 GB", "Follow-up was not measured independently.");
                    elapsed = Stopwatch.Frequency * 59; page.Tick();
                    Need(executed == 1, "Repeated run began before the configured interval.");
                    elapsed = Stopwatch.Frequency * 60; page.Tick();
                    WaitFor(() => executed == 2 && clean.IsEnabled);
                    elapsed = Stopwatch.Frequency * 100; page.Tick();
                    clean.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Need(((TextBlock)mini.FindName("Status")).Text == "正在清理" && !clean.IsEnabled, "Compact did not show the in-flight state.");
                    WaitFor(() => executed == 3 && clean.IsEnabled);
                    Need(page.Find<TextBlock>("Result").Text.Contains("手动") && page.Find<TextBlock>("Next").Text.Contains("60 秒后") &&
                        ((TextBlock)mini.FindName("Status")).Text == "刚清理完",
                        "Compact Clean did not run once and restart the interval.");
                    fail = true;
                    elapsed = Stopwatch.Frequency * 159; page.Tick();
                    Need(executed == 3, "Manual run did not postpone the next automatic run.");
                    elapsed = Stopwatch.Frequency * 160; page.Tick();
                    WaitFor(() => page.Find<TextBlock>("Result").Text.Contains("定时已暂停"));
                    elapsed = Stopwatch.Frequency * 250; page.Tick();
                    Need(executed == 4 && seconds.IsEnabled && ((TextBlock)mini.FindName("Status")).Text == "未完成",
                        "A failed operation did not disarm automatic cleaning or surface an error in compact.");
                    ((Button)mini.FindName("Expand")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Need(view.IsVisible && !mini.IsVisible, "Expand did not restore the dashboard.");
                    File.WriteAllLines(settingsPath, new[] { "1", "Strong", "12:30, 22:30" });
                    Settings migrated = Settings.Load(settingsPath);
                    Need(migrated.Mode == CleanMode.Strong && migrated.Seconds == 500, "Old settings did not preserve mode with the new default interval.");
                    fail = false;
                    page.Find<RadioButton>("Strong").IsChecked = true;
                    seconds.Text = "120"; arm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Settings strongSaved = Settings.Load(settingsPath);
                    Need(strongSaved.Mode == CleanMode.Strong && strongSaved.Seconds == 120 && File.ReadAllLines(settingsPath)[0] == "2",
                        "Strong schedule could not be saved without a confirmation popup.");
                    WaitFor(() => executed == 5 && actual == CleanMode.Strong && page.Find<TextBlock>("After").Text == "6.00 GB" &&
                        page.Find<TextBlock>("Next").Text.Contains("120 秒后"));
                    page.Find<Button>("Clean").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitFor(() => executed == 6 && actual == CleanMode.Strong);
                }
                finally { view.Close(); }
            }
            File.Delete(settingsPath); Directory.Delete(temp);
        }

        private static void Refresh(Window window)
        { window.Dispatcher.Invoke(new Action(window.UpdateLayout), DispatcherPriority.ApplicationIdle); }

        private static void WaitFor(Func<bool> done)
        {
            var wait = Stopwatch.StartNew();
            while (!done() && wait.ElapsedMilliseconds < 5000)
            {
                Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Thread.Sleep(15);
            }
            Need(done(), "Dashboard did not receive worker result.");
        }

        private static void Capture(FrameworkElement root, string path, Color backdrop)
        {
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (DrawingContext d = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                d.DrawRectangle(new SolidColorBrush(backdrop), null, rect);
                d.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect }, null, rect);
            }
            bmp.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream file = File.Create(path)) png.Save(file);
        }
    }
}
