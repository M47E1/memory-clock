using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
                var clientStart = view.PointToScreen(new Point(0, 0));
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
                CheckDialTiers();
                CheckUpdater(Path.GetFullPath(args[0]));
                CheckWindow(Path.GetFullPath(args[0]));
                application.Shutdown();
                Console.WriteLine("PASS: login startup, dual auto-clean, fullscreen defer, load colors, rocket dial, top-3 tooltip, compact menu/position/opacity, auto-updater, and page-file settings. Cleaning was simulated.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void CheckDialTiers()
        {
            Need(DialArt.LevelFor(0.625) == LoadLevel.Normal &&
                 DialArt.LevelFor(0.80) == LoadLevel.Elevated &&
                 DialArt.LevelFor(0.899) == LoadLevel.Elevated &&
                 DialArt.LevelFor(0.90) == LoadLevel.High, "Dial load thresholds are wrong.");
            Need(!object.ReferenceEquals(DialArt.AccentBrush(0.6), DialArt.AccentBrush(0.85)) &&
                 !object.ReferenceEquals(DialArt.AccentBrush(0.85), DialArt.AccentBrush(0.95)) &&
                 !object.ReferenceEquals(DialArt.MeterBrush(0.6), DialArt.MeterBrush(0.85)) &&
                 !object.ReferenceEquals(DialArt.MeterBrush(0.85), DialArt.MeterBrush(0.95)),
                "Load-tier brushes must be distinct.");
        }

        private static void CheckUpdater(string output)
        {
            Need(Updater.CurrentDisplayVersion == "v1.6.0", "Unexpected assembly version: " + Updater.CurrentDisplayVersion);
            Need(Updater.IsNewer(Updater.ParseReleaseVersion("v1.6.1"), new Version(1, 6, 0, 0)), "Newer release tag was not detected.");
            Need(!Updater.IsNewer(Updater.ParseReleaseVersion("v1.6.0"), new Version(1, 6, 0, 0)), "Same release tag triggered update.");
            Need(!Updater.IsNewer(Updater.ParseReleaseVersion("v1.5.9"), new Version(1, 6, 0, 0)), "Older release tag triggered downgrade.");

            Need(Updater.IsOfficialAssetUrl("https://github.com/M47E1/memory-clock/releases/download/v1.7.0/MemoryClock-portable.zip"),
                "Official GitHub portable asset URL was rejected.");
            Need(Updater.IsAllowedRedirectUri(new Uri("https://objects.githubusercontent.com/github-production-release-asset/123")) &&
                 Updater.IsAllowedRedirectUri(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/123")) &&
                 !Updater.IsAllowedRedirectUri(new Uri("http://objects.githubusercontent.com/x")) &&
                 !Updater.IsAllowedRedirectUri(new Uri("https://evil.example/file.zip")),
                "Redirect allowlist validation failed.");
            foreach (string bad in new[]
            {
                "http://github.com/M47E1/memory-clock/releases/download/v1.7.0/MemoryClock-portable.zip",
                "https://evil.example/M47E1/memory-clock/releases/download/v1.7.0/MemoryClock-portable.zip",
                "https://github.com/other/memory-clock/releases/download/v1.7.0/MemoryClock-portable.zip",
                "https://github.com/M47E1/memory-clock/releases/download/v1.7.0/MemoryClock-source.zip"
            })
            {
                Need(!Updater.IsOfficialAssetUrl(bad), "Untrusted update URL was accepted: " + bad);
            }

            string shaGood = new string('a', 64);
            string validJson = "{\"tag_name\":\"v1.7.0\",\"html_url\":\"https://github.com/M47E1/memory-clock/releases/tag/v1.7.0\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"MemoryClock-portable.zip\",\"browser_download_url\":\"https://github.com/M47E1/memory-clock/releases/download/v1.7.0/MemoryClock-portable.zip\",\"size\":250000,\"digest\":\"sha256:" + shaGood + "\"}]}";
            UpdateInfo parsed = Updater.ParseReleaseJson(validJson, new Version(1, 6, 0, 0));
            Need(parsed != null && parsed.DisplayVersion == "v1.7.0" && parsed.ExpectedSha256 == shaGood, "Valid release JSON was not parsed.");
            Need(Updater.ParseReleaseJson(validJson, new Version(1, 7, 0, 0)) == null, "Already-latest release returned an update.");
            Need(Updater.ParseReleaseJson(validJson.Replace("\"draft\":false", "\"draft\":true"), new Version(1, 6, 0, 0)) == null, "Draft release was accepted.");
            Need(Updater.ParseReleaseJson(validJson.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(1, 6, 0, 0)) == null, "Prerelease was accepted.");

            string sandbox = Path.Combine(output, "updater-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            try
            {
                // Zip-slip rejection
                string evilZip = Path.Combine(sandbox, "evil.zip");
                using (var archive = ZipFile.Open(evilZip, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("../escape.txt");
                    using (var w = new StreamWriter(entry.Open())) w.Write("bad");
                }
                bool blocked = false;
                try { Updater.ExtractVerifiedPackage(evilZip, Path.Combine(sandbox, "s1"), new Version(1, 5, 0, 0), new Version(1, 6, 0, 0)); }
                catch (InvalidDataException) { blocked = true; }
                Need(blocked, "Zip-slip entry was not rejected.");

                // Valid portable zip containing current VerifyClock.exe (1.6.0.0)
                string goodZip = Path.Combine(sandbox, "good.zip");
                string selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var archive = ZipFile.Open(goodZip, ZipArchiveMode.Create))
                    archive.CreateEntryFromFile(selfExe, "MemoryClock.exe");
                string actualSha = Updater.ComputeSha256Hex(goodZip);

                // Bad digest must be rejected
                bool badHashBlocked = false;
                try { Updater.ExtractVerifiedPackage(goodZip, Path.Combine(sandbox, "sBadHash"), new Version(1, 5, 0, 0), new Version(1, 6, 0, 0), new string('0', 64)); }
                catch (InvalidDataException) { badHashBlocked = true; }
                Need(badHashBlocked, "Mismatched SHA-256 digest was accepted.");

                // Same version must be rejected to prevent update loops
                bool sameBlocked = false;
                try { Updater.ExtractVerifiedPackage(goodZip, Path.Combine(sandbox, "s2"), new Version(1, 6, 0, 0), new Version(1, 6, 0, 0), actualSha); }
                catch (InvalidOperationException) { sameBlocked = true; }
                Need(sameBlocked, "Same-version package was allowed to overwrite.");

                // Mismatched expected tag version (1.7.0.0 tag vs 1.6.0.0 binary) must be rejected
                bool mismatchBlocked = false;
                try { Updater.ExtractVerifiedPackage(goodZip, Path.Combine(sandbox, "sMis"), new Version(1, 5, 0, 0), new Version(1, 7, 0, 0), actualSha); }
                catch (InvalidOperationException) { mismatchBlocked = true; }
                Need(mismatchBlocked, "Binary version mismatching release tag was allowed.");

                // Older baseline (1.5.0.0) accepts verified 1.6.0.0 package matching tag and SHA-256
                string stageOk = Path.Combine(sandbox, "s3");
                Updater.ExtractVerifiedPackage(goodZip, stageOk, new Version(1, 5, 0, 0), new Version(1, 6, 0, 0), actualSha);
                Need(File.Exists(Path.Combine(stageOk, "MemoryClock.exe")), "Verified package did not extract MemoryClock.exe.");
            }
            finally { Directory.Delete(sandbox, true); }
        }

        private static void CheckSchedule()
        {
            Settings settings = Settings.Parse(" 0500 ", CleanMode.Strong);
            Need(settings.Display == "500" && Settings.Parse("86400", CleanMode.Gentle).Seconds == 86400, "Interval was not normalized.");
            Need(Settings.ParseThreshold(" 85 ") == 85 && Settings.ParseThreshold("50") == 50 && Settings.ParseThreshold("95") == 95,
                "Threshold was not normalized.");
            foreach (string input in new[] { "", "9", "0", "24:00", "10.5", "86401", "+500", "-40" })
            {
                try { Settings.Parse(input, CleanMode.Gentle); throw new Exception("Invalid schedule accepted: " + input); }
                catch (FormatException) { }
            }
            foreach (string input in new[] { "", "49", "96", "100", "85.5", "-80" })
            {
                try { Settings.ParseThreshold(input); throw new Exception("Invalid threshold accepted: " + input); }
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

            // Threshold trigger + 60s cooldown (including pause/re-arm & float precision) check
            var watch = new IntervalTimer();
            Settings highWater = Settings.Default.WithTrigger(AutoTrigger.Threshold).WithThreshold(85).WithMode(CleanMode.Strong);
            watch.Arm(highWater, 100 * s);
            Need(!watch.IsDue(100 * s, 84.9) && watch.IsDue(100 * s, 85.0), "Threshold did not fire at configured percent.");
            watch.Started();
            Need(!watch.IsDue(105 * s, 95.0), "Threshold allowed overlap while in flight.");
            watch.Finished(105 * s);
            Need(!watch.IsDue(164 * s, 95.0) && watch.SecondsLeft(164 * s) == 1, "Threshold ignored cooldown.");
            // Pause and immediately re-arm must still respect remaining 60s cooldown
            watch.Pause();
            watch.Arm(highWater, 130 * s);
            Need(!watch.IsDue(130 * s, 95.0) && watch.SecondsLeft(130 * s) == 35, "Pause/re-arm bypassed threshold cooldown.");
            Need(watch.IsDue(165 * s, 86.0) && watch.SecondsLeft(165 * s) == null, "Threshold did not recover after cooldown.");
            watch.Pause();
            // Floating-point exact boundary (e.g. 66% represented as 65.99999999999999)
            Settings sixtySix = Settings.Default.WithTrigger(AutoTrigger.Threshold).WithThreshold(66);
            watch.Arm(sixtySix, 200 * s);
            Need(watch.IsDue(200 * s, (1.0 - 0.34) * 100.0), "Threshold missed floating-point boundary.");
            watch.Pause();

            var reqInterval = new LaunchRequest(true, true, settings);
            LaunchRequest resumed = LaunchRequest.Parse(reqInterval.Arguments.Split(' '));
            Need(resumed != null && resumed.Enable && resumed.Compact && resumed.Choice.Trigger == AutoTrigger.Interval &&
                resumed.Choice.Seconds == 500 && resumed.Choice.Mode == CleanMode.Strong,
                "Elevation did not carry interval request.");
            var reqThreshold = new LaunchRequest(true, false, highWater);
            LaunchRequest resumedTh = LaunchRequest.Parse(reqThreshold.Arguments.Split(' '));
            Need(resumedTh != null && resumedTh.Enable && !resumedTh.Compact && resumedTh.Choice.Trigger == AutoTrigger.Threshold &&
                resumedTh.Choice.Threshold == 85 && resumedTh.Choice.Mode == CleanMode.Strong,
                "Elevation did not carry threshold request.");
            Need(LaunchRequest.Parse("--elevated enable Gentle Interval 9 full".Split(' ')) == null &&
                LaunchRequest.Parse("--elevated enable Gentle Threshold 40 full".Split(' ')) == null &&
                LaunchRequest.Parse("--elevated other Strong Interval 500 compact".Split(' ')) == null,
                "Invalid elevation requests were accepted.");

            // Compact location clamping: L-shaped dual monitors (dead corner must fall back!) & offscreen fallback
            var work = new Rect(0, 0, 1920, 1040);
            var screens = new[] { new Rect(0, 0, 1920, 1040), new Rect(-1080, 0, 1080, 1920) };
            Point savedLeftScreen = Dashboard.ResolveCompactLocation(Settings.Default.WithCompactLocation(-400, 120), 104, 124, work, screens);
            Need(savedLeftScreen.X == -400 && savedLeftScreen.Y == 120, "Multi-monitor saved compact location was rejected.");
            Point deadCorner = Dashboard.ResolveCompactLocation(Settings.Default.WithCompactLocation(400, 1400), 104, 124, work, screens);
            Need(deadCorner.X == 1920 - 104 - 22 && deadCorner.Y == 1040 - 124 - 22, "L-shaped dead-zone compact location did not fall back safely.");
            Point disconnected = Dashboard.ResolveCompactLocation(Settings.Default.WithCompactLocation(4500, 200), 104, 124, work, screens);
            Need(disconnected.X == 1920 - 104 - 22 && disconnected.Y == 1040 - 124 - 22, "Offscreen compact location did not fall back safely.");
        }

        private static void CheckEngine()
        {
            var actions = new List<Area>();
            var samples = new Queue<Reading?>(new Reading?[] { new Reading { Available = 6UL << 30 }, new Reading { Available = 8UL << 30 } });
            CleanResult strong = Cleaner.Run(CleanMode.Strong, () => samples.Dequeue(), actions.Add);
            Need(strong.Success && strong.Before.Value.Available == (6UL << 30) && strong.After.Value.Available == (8UL << 30) &&
                strong.FreedBytes == (2L << 30) && strong.SignedDeltaBytes == (2L << 30) &&
                actions.SequenceEqual(new[] { Area.WorkingSets, Area.FileCache, Area.LowStandby }), "Strong mode actions or FreedBytes were mismatched.");
            var neg = new CleanResult("已完成", true, new Reading { Available = 6UL << 30 }, new Reading { Available = 5UL << 30 });
            Need(neg.SignedDeltaBytes == -(1L << 30) && neg.FreedBytes == 0, "Negative delta was not preserved accurately.");
            actions.Clear();
            Cleaner.Run(CleanMode.Gentle, () => null, actions.Add);
            Need(actions.SequenceEqual(new[] { Area.FileCache, Area.LowStandby }), "Gentle mode touched application worksets.");
            actions.Clear();
            CleanResult failed = Cleaner.Run(CleanMode.Strong, () => null, area => { actions.Add(area); throw new IOException("拒绝执行"); });
            Need(!failed.Success && actions.SequenceEqual(new[] { Area.WorkingSets }), "Engine did not respect denial.");
            Reading? current = WindowsMemory.Read();
            Need(current.HasValue && current.Value.Total > 0 && current.Value.Available <= current.Value.Total &&
                current.Value.Committed > 0 && current.Value.CommitLimit > 0, "Windows memory metrics unavailable or out of range.");
            string top = WindowsMemory.ReadTopProcesses(3);
            Need(top.StartsWith("当前占用内存前三") && top.Contains("1. "), "Top-3 process summary failed: " + top);
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
            bool fullscreen = false;
            ulong availableGb = 6;
            long elapsed = 0;
            int settingsOpened = 0;
            UpdateInfo mockRemoteUpdate = null;
            int updateApplies = 0;
            string lastUpdateRelaunchArg = null;
            string key = @"Software\MemoryClock.Test." + Guid.NewGuid().ToString("N");
            string exe = Path.Combine(temp, "test app", "MemoryClock.exe");
            var startup = new StartupEntry(exe, key);
            using (var page = new Dashboard(settingsPath, true,
                () => new Reading { Total = 16UL << 30, Available = availableGb << 30, Committed = 18UL << 30, CommitLimit = 48UL << 30 },
                mode =>
                {
                    actual = mode;
                    bool denied = fail;
                    Interlocked.Increment(ref executed);
                    return new CleanResult(denied ? "模拟拒绝" : "已完成", !denied,
                        new Reading { Total = 16UL << 30, Available = 4UL << 30 },
                        new Reading { Total = 16UL << 30, Available = 6UL << 30 });
                },
                null, false, () => elapsed, () => "C: · 已分配 32 GB · 设定上限 64 GB", () => settingsOpened++, startup,
                () => fullscreen, () => "当前占用内存前三\n1. chrome · 4.20 GB（12 个进程）\n2. Code · 1.85 GB（6 个进程）\n3. WeChat · 420 MB",
                () => mockRemoteUpdate, (info, relaunchArg) => { Interlocked.Increment(ref updateApplies); lastUpdateRelaunchArg = relaunchArg; }))
            {
                Window view = page.View;
                view.WindowStartupLocation = WindowStartupLocation.Manual;
                view.Left = -10000; view.Top = -10000; view.Topmost = false; view.ShowActivated = false;
                view.ShowInTaskbar = false;
                Need(Program.IsTrayLaunch(new[] { "--tray" }) && !Program.IsTrayLaunch(new[] { "--tray", "clean" }) &&
                    Program.IsCompactLaunch(new[] { "--compact" }) && !Program.IsCompactLaunch(new[] { "--compact", "extra" }),
                    "Unexpected tray or compact arguments.");
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

                    // Top-3 tooltip refresh on demand
                    page.RefreshTopProcesses();
                    WaitFor(() => ((string)page.Find<Border>("MemoryBlock").ToolTip).Contains("chrome"));

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
                    Need(saved.Mode == CleanMode.Gentle && saved.Trigger == AutoTrigger.Interval && saved.Seconds == 60, "Enabling did not save interval and mode.");
                    WaitFor(() => page.Find<TextBlock>("After").Text == "6.00 GB");
                    Need(executed == 1 && actual == CleanMode.Gentle && !seconds.IsEnabled &&
                        page.Find<TextBlock>("Next").Text.Contains("60 秒后") &&
                        page.Find<TextBlock>("Before").Text == "4.00 GB" && page.Find<TextBlock>("Later").Text == "等待读数" &&
                        page.Find<TextBlock>("Delta").Text == "本次释放 +2.00 GB" &&
                        page.Find<TextBlock>("Result").Text.Contains("今日累计 +2.00 GB"),
                        "Enabling did not clean immediately, display net/daily freed, and start countdown.");

                    minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Window mini = page.CompactView;
                    Refresh(mini);
                    var clean = (Button)mini.FindName("Clean");
                    Need(!view.IsVisible && mini.IsVisible && mini.ActualWidth <= 108 && mini.ActualHeight <= 128 &&
                        mini.Left == -10000 && mini.Top == -10000 && !mini.IsActive && !mini.Topmost,
                        "Compact window did not replace the dashboard offscreen at compact size.");
                    Need((string)clean.Content == "Clean" && clean.IsTabStop &&
                        mini.FindName("RocketGroup") is Canvas && mini.FindName("RocketShift") is TranslateTransform &&
                        mini.FindName("RocketFlame") is System.Windows.Shapes.Path &&
                        page.Find<Canvas>("MiniRocketGroup") != null &&
                        ((TextBlock)mini.FindName("Used")).Text == "10.0" &&
                        ((TextBlock)mini.FindName("Free")).Text == "6.0" &&
                        ((TextBlock)mini.FindName("DialPercent")).Text == (.625).ToString("0%", System.Globalization.CultureInfo.InvariantCulture) &&
                        object.ReferenceEquals(((Image)mini.FindName("Dial")).Source, ring.Source) &&
                        ((TextBlock)mini.FindName("Status")).Text == "+2.0 GB" &&
                        ((Border)mini.FindName("CardRoot")).ToolTip.ToString().Contains("chrome"),
                        "Compact view did not show shared reading, +2.0 GB freed badge, and top-3 tooltip.");
                    Capture((FrameworkElement)mini.Content, Path.Combine(output, "compact.png"), ((SolidColorBrush)view.Background).Color);

                    // Verify compact context menu toggles subtle opacity
                    var menuSubtle = (MenuItem)mini.FindName("MenuSubtle");
                    Need(menuSubtle.IsChecked, "Subtle compact opacity should default to enabled.");
                    menuSubtle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Need(!Settings.Load(settingsPath).SubtleOpacity, "MenuSubtle did not persist toggle off.");
                    menuSubtle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Need(Settings.Load(settingsPath).SubtleOpacity, "MenuSubtle did not persist toggle on.");

                    availableGb = 5;
                    elapsed = Stopwatch.Frequency * 29;
                    page.Tick();
                    Need(page.Find<TextBlock>("Later").Text == "等待读数" && ((TextBlock)mini.FindName("Status")).Text == "定时中",
                        "Follow-up came too early or recent status did not expire.");
                    elapsed = Stopwatch.Frequency * 30;
                    page.Tick();
                    Need(page.Find<TextBlock>("Later").Text == "5.00 GB", "Follow-up was not measured independently.");

                    // Verify fullscreen auto-defer holds scheduled clean until fullscreen exits, while manual clean still runs!
                    fullscreen = true;
                    elapsed = Stopwatch.Frequency * 60; page.Tick();
                    Need(executed == 1 && page.Find<TextBlock>("Next").Text == "全屏避让中" &&
                        ((TextBlock)mini.FindName("Status")).Text == "全屏避让",
                        "Fullscreen did not defer automatic cleaning.");
                    fullscreen = false;
                    elapsed = Stopwatch.Frequency * 61; page.Tick();
                    WaitFor(() => executed == 2 && clean.IsEnabled);

                    elapsed = Stopwatch.Frequency * 100; page.Tick();
                    fullscreen = true;
                    clean.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Need(((TextBlock)mini.FindName("Status")).Text == "正在清理" && !clean.IsEnabled, "Manual Compact Clean was blocked by fullscreen.");
                    WaitFor(() => executed == 3 && clean.IsEnabled);
                    fullscreen = false;
                    Need(page.Find<TextBlock>("Result").Text.Contains("手动") && page.Find<TextBlock>("Next").Text.Contains("60 秒后") &&
                        ((TextBlock)mini.FindName("Status")).Text == "+2.0 GB",
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

                    // Legacy v1, v2, and v3 migrations
                    File.WriteAllLines(settingsPath, new[] { "1", "Strong", "12:30, 22:30" });
                    Settings migratedV1 = Settings.Load(settingsPath);
                    Need(migratedV1.Mode == CleanMode.Strong && migratedV1.Seconds == 500 && migratedV1.Trigger == AutoTrigger.Interval && migratedV1.AutoUpdate,
                        "v1 settings migration failed.");
                    File.WriteAllLines(settingsPath, new[] { "2", "Strong", "180" });
                    Settings migratedV2 = Settings.Load(settingsPath);
                    Need(migratedV2.Mode == CleanMode.Strong && migratedV2.Seconds == 180 && migratedV2.SubtleOpacity && migratedV2.PauseOnFullscreen && migratedV2.AutoUpdate,
                        "v2 settings migration failed.");
                    File.WriteAllLines(settingsPath, new[] { "3", "Strong", "180", "Threshold", "82", "1", "0", "100,200" });
                    Settings migratedV3 = Settings.Load(settingsPath);
                    Need(migratedV3.Trigger == AutoTrigger.Threshold && migratedV3.Threshold == 82 && !migratedV3.PauseOnFullscreen && migratedV3.AutoUpdate,
                        "v3 settings migration failed.");

                    // Strong mode direct execution (no popup) + Threshold trigger + high-load color preview
                    fail = false;
                    page.Find<RadioButton>("Strong").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    page.Find<RadioButton>("TriggerThreshold").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Need(page.Find<TextBlock>("TriggerUnit").Text == "%" && seconds.Text == "85", "Switching to threshold tab did not update unit/value.");
                    availableGb = 6; // 62.5% < 85%
                    page.Tick();
                    arm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Settings thSaved = Settings.Load(settingsPath);
                    Need(thSaved.Mode == CleanMode.Strong && thSaved.Trigger == AutoTrigger.Threshold && thSaved.Threshold == 85 &&
                        File.ReadAllLines(settingsPath)[0] == "4", "Threshold settings were not saved in v4 format.");
                    Need(executed == 4 && page.Find<TextBlock>("ScheduleState").Text == "监控已启用" &&
                        page.Find<TextBlock>("Next").Text.Contains("85%"), "Threshold armed below limit should wait without cleaning.");
                    CheckPanelLayout(page);

                    // Spike RAM to 93.75% (1 GB free of 16 GB) -> triggers threshold clean + High coral load color!
                    availableGb = 1;
                    elapsed = Stopwatch.Frequency * 260;
                    page.Tick();
                    WaitFor(() => executed == 5 && actual == CleanMode.Strong && page.Find<Button>("Clean").IsEnabled);
                    // Re-sample high load for visual inspection preview (without firing due to 60s cooldown!)
                    elapsed = Stopwatch.Frequency * 270;
                    page.Tick();
                    Need(executed == 5 && page.Find<TextBlock>("Next").Text.Contains("间隔") &&
                        object.ReferenceEquals(page.Find<ProgressBar>("Meter").Foreground, DialArt.MeterBrush(0.9375)),
                        "High load color or threshold cooldown did not apply.");
                    view.Width = 764; view.Height = 552; Refresh(view);
                    Capture((FrameworkElement)view.Content, Path.Combine(output, "dashboard-high.png"), ((SolidColorBrush)view.Background).Color);
                    minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Refresh(mini);
                    Capture((FrameworkElement)mini.Content, Path.Combine(output, "compact-high.png"), ((SolidColorBrush)view.Background).Color);
                    ((Button)mini.FindName("Expand")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    // Manual Strong clean persists mode without popup; also check first-arm Interval honors fullscreen defer
                    page.Pause();
                    page.Find<RadioButton>("TriggerInterval").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    fullscreen = true;
                    elapsed = Stopwatch.Frequency * 350;
                    arm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Need(executed == 5 && page.Find<TextBlock>("Next").Text == "全屏避让中",
                        "First interval arm bypassed fullscreen defer.");
                    fullscreen = false;
                    page.Pause();
                    page.Find<Button>("Clean").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitFor(() => executed == 6 && actual == CleanMode.Strong && page.Find<Button>("Clean").IsEnabled);
                    Need(Settings.Load(settingsPath).Mode == CleanMode.Strong, "Manual clean did not persist selected mode.");

                    // One-click update & startup auto-update verification (offline mocked)
                    var updateBtn = page.Find<Button>("UpdateButton");
                    mockRemoteUpdate = null;
                    updateBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitFor(() => ((string)updateBtn.Content).Contains("已是最新"));
                    mockRemoteUpdate = new UpdateInfo(new Version(1, 7, 0, 0), "v1.7.0",
                        "https://github.com/M47E1/memory-clock/releases/download/v1.7.0/MemoryClock-portable.zip",
                        "https://github.com/M47E1/memory-clock/releases/tag/v1.7.0", 250000);
                    page.CheckForUpdateAsync(true, false);
                    WaitFor(() => ((string)updateBtn.Content) == "一键更新 v1.7.0");
                    updateBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitFor(() => updateApplies == 1 && lastUpdateRelaunchArg == "");
                    // Startup auto-install path applies immediately without needing a click
                    page.CheckForUpdateAsync(false, true);
                    WaitFor(() => updateApplies == 2);
                    // Compact context menu AutoUpdate toggle persists
                    var menuAutoUpdate = (MenuItem)mini.FindName("MenuAutoUpdate");
                    Need(menuAutoUpdate.IsChecked, "AutoUpdate should default to enabled.");
                    menuAutoUpdate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Need(!Settings.Load(settingsPath).AutoUpdate, "MenuAutoUpdate did not persist toggle off.");
                    menuAutoUpdate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Need(Settings.Load(settingsPath).AutoUpdate, "MenuAutoUpdate did not persist toggle on.");
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
