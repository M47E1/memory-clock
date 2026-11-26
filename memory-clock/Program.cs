using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Memory Clock")]
[assembly: AssemblyDescription("Windows memory cleaner based on Mem Reduct · GNU GPLv3")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

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
        {
            get
            {
                return "--elevated " + (Enable ? "enable" : "clean") + " " + Choice.Mode + " " +
                    Choice.Trigger + " " + Choice.ActiveValueDisplay + " " + (Compact ? "compact" : "full");
            }
        }

        internal static LaunchRequest Parse(string[] args)
        {
            if (args.Length != 6 || args[0] != "--elevated" ||
                (args[1] != "enable" && args[1] != "clean") ||
                (args[2] != "Gentle" && args[2] != "Strong") ||
                (args[3] != "Interval" && args[3] != "Threshold") ||
                (args[5] != "compact" && args[5] != "full")) return null;
            try
            {
                CleanMode mode = args[2] == "Strong" ? CleanMode.Strong : CleanMode.Gentle;
                AutoTrigger trigger = args[3] == "Threshold" ? AutoTrigger.Threshold : AutoTrigger.Interval;
                return new LaunchRequest(args[1] == "enable", args[5] == "compact",
                    Settings.Default.ParseFor(trigger, args[4], mode));
            }
            catch (FormatException) { return null; }
        }
    }

    internal static class Program
    {
        internal static bool IsTrayLaunch(string[] args)
        { return args.Length == 1 && args[0] == StartupEntry.TrayOption; }

        internal static bool IsCompactLaunch(string[] args)
        { return args.Length == 1 && args[0] == "--compact"; }

        internal static void Present(Dashboard dashboard, bool trayOnly, LaunchRequest request, bool compactOnly = false)
        {
            if (trayOnly) return;
            if (compactOnly || (request != null && request.Compact)) dashboard.ShowCompact(); else dashboard.View.Show();
            if (request != null) dashboard.View.Dispatcher.BeginInvoke(new Action(() => dashboard.ApplyRequest(request)));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            LaunchRequest request = args.Length == 0 ? null : LaunchRequest.Parse(args);
            bool trayOnly = IsTrayLaunch(args);
            bool compactOnly = IsCompactLaunch(args);
            bool admin = WindowsMemory.IsAdmin();
            if (args.Length != 0 && !trayOnly && !compactOnly && (request == null || !admin)) return 2;
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
                            Present(dashboard, trayOnly, request, compactOnly);
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

    internal sealed class RocketRig
    {
        private readonly Canvas group, flameGroup;
        private readonly TranslateTransform shift, sparkLShift, sparkRShift;
        private readonly ScaleTransform bodyScale, flameScale;
        private readonly UIElement sparkL, sparkR;
        private readonly System.Windows.Shapes.Path hull, fins;
        private int generation;
        private bool launching, hovered, pressed;

        private RocketRig(Canvas group, TranslateTransform shift, ScaleTransform bodyScale,
            Canvas flameGroup, ScaleTransform flameScale,
            UIElement sparkL, TranslateTransform sparkLShift,
            UIElement sparkR, TranslateTransform sparkRShift,
            System.Windows.Shapes.Path hull, System.Windows.Shapes.Path fins)
        {
            this.group = group; this.shift = shift; this.bodyScale = bodyScale;
            this.flameGroup = flameGroup; this.flameScale = flameScale;
            this.sparkL = sparkL; this.sparkLShift = sparkLShift;
            this.sparkR = sparkR; this.sparkRShift = sparkRShift;
            this.hull = hull; this.fins = fins;
        }

        internal static RocketRig Attach(FrameworkElement root, string prefix)
        {
            return new RocketRig(
                (Canvas)root.FindName(prefix + "Group"),
                (TranslateTransform)root.FindName(prefix + "Shift"),
                (ScaleTransform)root.FindName(prefix + "Scale"),
                (Canvas)root.FindName(prefix + "FlameGroup"),
                (ScaleTransform)root.FindName(prefix + "FlameScale"),
                (UIElement)root.FindName(prefix + "SparkL"),
                (TranslateTransform)root.FindName(prefix + "SparkLShift"),
                (UIElement)root.FindName(prefix + "SparkR"),
                (TranslateTransform)root.FindName(prefix + "SparkRShift"),
                (System.Windows.Shapes.Path)root.FindName(prefix + "Hull"),
                (System.Windows.Shapes.Path)root.FindName(prefix + "Fins"));
        }

        internal bool IsComplete
        {
            get
            {
                return group != null && shift != null && bodyScale != null &&
                    flameGroup != null && flameScale != null &&
                    sparkL != null && sparkR != null && hull != null && fins != null;
            }
        }

        internal void SetAccent(Brush brush)
        {
            if (hull != null) hull.Fill = brush;
            if (fins != null) fins.Fill = brush;
        }

        internal void ApplyStaticHoverPreview()
        {
            if (!IsComplete) return;
            Reset();
            shift.Y = -1.3;
            bodyScale.ScaleX = 1.05;
            bodyScale.ScaleY = 1.05;
            flameGroup.Opacity = 0.85;
            flameScale.ScaleX = 0.82;
            flameScale.ScaleY = 0.78;
        }

        internal void Reset()
        {
            generation++;
            launching = false;
            hovered = false;
            pressed = false;
            if (!IsComplete) return;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            group.BeginAnimation(UIElement.OpacityProperty, null);
            flameGroup.BeginAnimation(UIElement.OpacityProperty, null);
            flameScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            flameScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            sparkL.BeginAnimation(UIElement.OpacityProperty, null);
            sparkR.BeginAnimation(UIElement.OpacityProperty, null);
            sparkLShift.BeginAnimation(TranslateTransform.XProperty, null);
            sparkLShift.BeginAnimation(TranslateTransform.YProperty, null);
            sparkRShift.BeginAnimation(TranslateTransform.XProperty, null);
            sparkRShift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.X = 0; shift.Y = 0;
            bodyScale.ScaleX = 1; bodyScale.ScaleY = 1;
            group.Opacity = 1;
            flameGroup.Opacity = 0;
            flameScale.ScaleX = 0.8; flameScale.ScaleY = 0.6;
            sparkL.Opacity = 0; sparkR.Opacity = 0;
            sparkLShift.X = 0; sparkLShift.Y = 0;
            sparkRShift.X = 0; sparkRShift.Y = 0;
        }

        internal void SetHover(bool isHovered, bool animate)
        {
            hovered = isHovered;
            if (!isHovered) pressed = false;
            if (!IsComplete || launching) return;
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                if (!hovered) Reset();
                return;
            }
            if (pressed) return;
            if (hovered) EnterHoverPilot();
            else EaseToIdle();
        }

        internal void SetPressed(bool isPressed, bool animate)
        {
            pressed = isPressed;
            if (!IsComplete || launching) return;
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                if (!pressed && !hovered) Reset();
                return;
            }
            if (pressed) EnterPressCharge();
            else if (hovered) EnterHoverPilot();
            else EaseToIdle();
        }

        private void EnterHoverPilot()
        {
            generation++;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var xTremor = new DoubleAnimation(-0.35, 0.35, TimeSpan.FromMilliseconds(70))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            var yHover = new DoubleAnimation(-1.1, -1.55, TimeSpan.FromMilliseconds(115))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            shift.BeginAnimation(TranslateTransform.XProperty, xTremor);
            shift.BeginAnimation(TranslateTransform.YProperty, yHover);
            bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            group.BeginAnimation(UIElement.OpacityProperty, null);
            group.Opacity = 1;

            var flamePulse = new DoubleAnimation(0.58, 0.90, TimeSpan.FromMilliseconds(105))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            var flameLen = new DoubleAnimation(0.56, 0.84, TimeSpan.FromMilliseconds(90))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            flameGroup.BeginAnimation(UIElement.OpacityProperty, flamePulse);
            flameScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.78, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            flameScale.BeginAnimation(ScaleTransform.ScaleYProperty, flameLen);
        }

        private void EnterPressCharge()
        {
            generation++;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var xRumble = new DoubleAnimation(-0.62, 0.62, TimeSpan.FromMilliseconds(45))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            shift.BeginAnimation(TranslateTransform.XProperty, xRumble);
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(1.3, TimeSpan.FromMilliseconds(85)) { EasingFunction = ease });
            bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.07, TimeSpan.FromMilliseconds(85)) { EasingFunction = ease });
            bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(85)) { EasingFunction = ease });

            flameGroup.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(70)));
            flameScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(85)) { EasingFunction = ease });
            var flare = new DoubleAnimation(1.12, 1.38, TimeSpan.FromMilliseconds(60))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            flameScale.BeginAnimation(ScaleTransform.ScaleYProperty, flare);
        }

        private void EaseToIdle()
        {
            int myGen = ++generation;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(160);
            var endAnim = new DoubleAnimation(0, dur) { EasingFunction = ease };
            endAnim.Completed += (sender, evt) => { if (generation == myGen && !launching && !hovered && !pressed) Reset(); };
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, endAnim);
            bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur) { EasingFunction = ease });
            bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur) { EasingFunction = ease });
            flameGroup.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130)));
            flameScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.75, dur));
            flameScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, dur));
        }

        internal void Launch(Func<bool> shouldRepeat, Func<bool> isStillHovered)
        {
            if (!IsComplete || launching || !SystemParameters.ClientAreaAnimation) return;
            launching = true;
            pressed = false;
            int myGen = ++generation;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(880);

            var xAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
            xAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35))));
            xAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75))));
            xAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
            xAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), easeOut));
            xAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration)));

            var yAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
            yAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), easeOut));
            yAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-18.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440)), easeIn));
            yAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(11.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(495))));
            yAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration), easeOut));

            var sxAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
            sxAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), easeOut));
            sxAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440)), easeIn));
            sxAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(495))));
            sxAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(duration), easeOut));

            var syAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
            syAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), easeOut));
            syAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), easeOut));
            syAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440)), easeIn));
            syAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(495))));
            syAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(duration), easeOut));

            var opAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
            opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280))));
            opAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), easeIn));
            opAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(505))));
            opAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(duration), easeOut));

            var flameOp = new DoubleAnimationUsingKeyFrames { Duration = duration };
            flameOp.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
            flameOp.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330))));
            flameOp.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), easeIn));
            flameOp.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(630))));
            flameOp.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(730))));
            flameOp.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(duration), easeOut));

            var flameSy = new DoubleAnimationUsingKeyFrames { Duration = duration };
            flameSy.KeyFrames.Add(new EasingDoubleKeyFrame(1.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), easeOut));
            flameSy.KeyFrames.Add(new EasingDoubleKeyFrame(2.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), easeOut));
            flameSy.KeyFrames.Add(new EasingDoubleKeyFrame(0.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), easeIn));
            flameSy.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.65, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(630))));
            flameSy.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(duration), easeOut));

            var flameSx = new DoubleAnimationUsingKeyFrames { Duration = duration };
            flameSx.KeyFrames.Add(new EasingDoubleKeyFrame(1.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), easeOut));
            flameSx.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440)), easeIn));
            flameSx.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.78, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(630))));
            flameSx.KeyFrames.Add(new EasingDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(duration), easeOut));

            // Twin launch exhaust sparks drifting down-outward at ignition
            var sparkDur = TimeSpan.FromMilliseconds(420);
            var sparkOp = new DoubleAnimationUsingKeyFrames { Duration = sparkDur };
            sparkOp.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(95))));
            sparkOp.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(sparkDur), easeOut));
            sparkL.BeginAnimation(UIElement.OpacityProperty, sparkOp);
            sparkR.BeginAnimation(UIElement.OpacityProperty, sparkOp);
            sparkLShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -4.8, sparkDur) { EasingFunction = easeOut });
            sparkLShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6.2, sparkDur) { EasingFunction = easeOut });
            sparkRShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 4.8, sparkDur) { EasingFunction = easeOut });
            sparkRShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6.2, sparkDur) { EasingFunction = easeOut });

            yAnim.Completed += (sender, evt) =>
            {
                if (generation != myGen) return;
                launching = false;
                if (shouldRepeat())
                {
                    Launch(shouldRepeat, isStillHovered);
                    return;
                }
                bool stillHover = isStillHovered();
                Reset();
                if (stillHover) SetHover(true, true);
            };

            shift.BeginAnimation(TranslateTransform.XProperty, xAnim);
            shift.BeginAnimation(TranslateTransform.YProperty, yAnim);
            bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, sxAnim);
            bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, syAnim);
            group.BeginAnimation(UIElement.OpacityProperty, opAnim);
            flameGroup.BeginAnimation(UIElement.OpacityProperty, flameOp);
            flameScale.BeginAnimation(ScaleTransform.ScaleXProperty, flameSx);
            flameScale.BeginAnimation(ScaleTransform.ScaleYProperty, flameSy);
        }
    }

    internal sealed class Dashboard : IDisposable
    {
        internal readonly Window View;
        internal const double SubtleIdleOpacity = 0.88;
        private const string IntervalHelp = "10–86400 秒 · 开启先清一次，完后重计";
        private const string ThresholdHelp = "50–95% · 超过后自动清，间隔 60 秒";
        private readonly string settingsPath;
        private readonly bool admin;
        private readonly Func<Reading?> read;
        private readonly Func<CleanMode, CleanResult> clean;
        private readonly Func<string> readPageFile;
        private readonly Action openPageFileSettings;
        private readonly Func<bool> isFullscreen;
        private readonly Func<string> readTopProcesses;
        private readonly Func<UpdateInfo> checkUpdate;
        private readonly Action<UpdateInfo, string> applyUpdate;
        private readonly Func<long> monotonic;
        private readonly EventWaitHandle show;
        private readonly IntervalTimer schedule = new IntervalTimer();
        private readonly DispatcherTimer timer;
        private readonly DispatcherTimer startupUpdateTimer;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripMenuItem trayClean, trayPause, trayFullscreen, trayAutoUpdate, trayUpdate;
        private readonly System.Drawing.Icon icon;
        private readonly TextBox seconds;
        private readonly TextBlock triggerUnit;
        private readonly RadioButton gentle, strong, triggerInterval, triggerThreshold;
        private readonly Button arm, run, updateButton;
        private readonly StartupEntry startupEntry;
        private readonly bool withTray;
        private bool closing, busy, failed, fullscreenDeferred, compactMenuOpen;
        private bool checkingUpdate, installingUpdate, updateUpToDate, pendingAutoInstall;
        private readonly RocketRig dashboardRocket;
        private RocketRig compactRocket;
        private UpdateInfo availableUpdate;
        private string updateError;
        private Settings settings;
        private long? nextReading, cleanedUntil, lastPageFileRead, lastTopRead;
        private long lastFreedBytes, todayFreedBytes;
        private DateTime todayDate = DateTime.Today;
        private bool readingPageFile, readingTop;
        private string topProcessSummary = "正在读取占用内存前三的程序…";
        private Window compact;
        private Reading? currentMemory;
        private string resultMessage;

        internal Dashboard(string path, bool admin, Func<Reading?> read, Func<CleanMode, CleanResult> clean, EventWaitHandle show, bool withTray,
            Func<long> monotonic = null, Func<string> readPageFile = null, Action openPageFileSettings = null, StartupEntry startupEntry = null,
            Func<bool> isFullscreen = null, Func<string> readTopProcesses = null,
            Func<UpdateInfo> checkUpdate = null, Action<UpdateInfo, string> applyUpdate = null)
        {
            this.admin = admin; settingsPath = path; this.read = read; this.clean = clean; this.show = show; this.withTray = withTray;
            this.monotonic = monotonic ?? Stopwatch.GetTimestamp;
            this.readPageFile = readPageFile ?? PageFile.ReadSummary;
            this.openPageFileSettings = openPageFileSettings ?? PageFile.OpenSettings;
            this.startupEntry = startupEntry ?? new StartupEntry(Assembly.GetExecutingAssembly().Location);
            this.isFullscreen = isFullscreen ?? WindowsMemory.IsFullscreenBusy;
            this.readTopProcesses = readTopProcesses ?? (() => WindowsMemory.ReadTopProcesses(3));
            this.checkUpdate = checkUpdate ?? (() => Updater.CheckLatest());
            this.applyUpdate = applyUpdate ?? ((info, relaunchArg) =>
                Updater.ApplyOnlineUpdate(info, System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    Process.GetCurrentProcess().Id, relaunchArg));

            using (Stream layout = typeof(Dashboard).Assembly.GetManifestResourceStream("MemoryClock.Dashboard"))
                View = (Window)XamlReader.Load(layout);
            View.SourceInitialized += (sender, evt) => WindowCaption.Apply(View);
            PropertyChangedEventHandler systemChanged = (sender, evt) =>
            {
                if (evt.PropertyName == "HighContrast") WindowCaption.Apply(View);
                if (evt.PropertyName == "ClientAreaAnimation" && !SystemParameters.ClientAreaAnimation)
                {
                    if (dashboardRocket != null) dashboardRocket.Reset();
                    if (compactRocket != null) compactRocket.Reset();
                }
            };
            SystemParameters.StaticPropertyChanged += systemChanged;
            View.Closed += (sender, evt) => SystemParameters.StaticPropertyChanged -= systemChanged;

            seconds = Find<TextBox>("Seconds");
            triggerUnit = Find<TextBlock>("TriggerUnit");
            gentle = Find<RadioButton>("Gentle");
            strong = Find<RadioButton>("Strong");
            triggerInterval = Find<RadioButton>("TriggerInterval");
            triggerThreshold = Find<RadioButton>("TriggerThreshold");
            arm = Find<Button>("Arm");
            run = Find<Button>("Clean");
            updateButton = Find<Button>("UpdateButton");

            string error = null;
            try { settings = Settings.Load(path); }
            catch (Exception issue) { settings = Settings.Default; error = issue.Message; }

            if (settings.Mode == CleanMode.Strong) strong.IsChecked = true; else gentle.IsChecked = true;
            if (settings.Trigger == AutoTrigger.Threshold) triggerThreshold.IsChecked = true; else triggerInterval.IsChecked = true;
            SyncTriggerFields();

            gentle.Click += (sender, evt) => OnModeClicked(CleanMode.Gentle);
            strong.Click += (sender, evt) => OnModeClicked(CleanMode.Strong);
            triggerInterval.Click += (sender, evt) => SwitchTrigger(AutoTrigger.Interval);
            triggerThreshold.Click += (sender, evt) => SwitchTrigger(AutoTrigger.Threshold);
            seconds.TextChanged += (sender, evt) => SetHint(ActiveHelp, false);
            seconds.KeyDown += (sender, evt) =>
            {
                if (evt.Key == Key.Enter && !schedule.Active) { evt.Handled = true; ToggleSchedule(); }
            };
            arm.Click += (sender, evt) => ToggleSchedule();
            run.Click += (sender, evt) => ManualClean();
            updateButton.Click += (sender, evt) => TriggerUpdateAction();
            var compactButton = Find<Button>("Compact");
            compactButton.Click += (sender, evt) => ShowCompact();
            dashboardRocket = RocketRig.Attach(View, "MiniRocket");
            Action syncDashHover = () => dashboardRocket.SetHover(
                compactButton.IsMouseOver || (run.IsEnabled && run.IsMouseOver), withTray);
            compactButton.MouseEnter += (sender, evt) => syncDashHover();
            compactButton.MouseLeave += (sender, evt) => syncDashHover();
            compactButton.PreviewMouseLeftButtonDown += (sender, evt) => dashboardRocket.SetPressed(true, withTray);
            compactButton.PreviewMouseLeftButtonUp += (sender, evt) => dashboardRocket.SetPressed(false, withTray);
            compactButton.MouseMove += (sender, evt) => { if (evt.LeftButton == MouseButtonState.Pressed) dashboardRocket.SetPressed(compactButton.IsPressed, withTray); };
            compactButton.LostMouseCapture += (sender, evt) => dashboardRocket.SetPressed(false, withTray);
            run.MouseEnter += (sender, evt) => syncDashHover();
            run.MouseLeave += (sender, evt) => syncDashHover();
            run.PreviewMouseLeftButtonDown += (sender, evt) => { if (run.IsEnabled) dashboardRocket.SetPressed(true, withTray); };
            run.PreviewMouseLeftButtonUp += (sender, evt) => dashboardRocket.SetPressed(false, withTray);
            run.MouseMove += (sender, evt) => { if (evt.LeftButton == MouseButtonState.Pressed) dashboardRocket.SetPressed(run.IsEnabled && run.IsPressed, withTray); };
            run.LostMouseCapture += (sender, evt) => dashboardRocket.SetPressed(false, withTray);
            run.IsEnabledChanged += (sender, evt) => { if (!run.IsEnabled) syncDashHover(); };
            View.StateChanged += (sender, evt) => { if (View.WindowState == WindowState.Minimized) dashboardRocket.Reset(); };
            Find<Button>("PageFileSettings").Click += (sender, evt) => OpenPageFileSettings();

            var memoryBlock = Find<Border>("MemoryBlock");
            memoryBlock.MouseEnter += (sender, evt) => RefreshTopProcesses();
            memoryBlock.ToolTipOpening += (sender, evt) => RefreshTopProcesses();

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
                var openItem = tray.ContextMenuStrip.Items.Add("打开主界面", null, (sender, evt) => ShowWindow());
                openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
                tray.ContextMenuStrip.Items.Add("迷你表盘", null, (sender, evt) => ShowCompact());
                tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
                trayClean = (Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items.Add("立即清理", null, (sender, evt) => ManualClean());
                trayPause = (Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items.Add("暂停自动清理", null, (sender, evt) => Pause());
                trayFullscreen = new Forms.ToolStripMenuItem("全屏时自动避让") { CheckOnClick = true, Checked = settings.PauseOnFullscreen };
                trayFullscreen.Click += (sender, evt) => SetPauseOnFullscreen(trayFullscreen.Checked);
                tray.ContextMenuStrip.Items.Add(trayFullscreen);
                trayAutoUpdate = new Forms.ToolStripMenuItem("启动自动更新") { CheckOnClick = true, Checked = settings.AutoUpdate };
                trayAutoUpdate.Click += (sender, evt) => SetAutoUpdate(trayAutoUpdate.Checked);
                tray.ContextMenuStrip.Items.Add(trayAutoUpdate);
                trayUpdate = (Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items.Add("检查更新", null, (sender, evt) => TriggerUpdateAction());
                tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
                tray.ContextMenuStrip.Items.Add("退出", null, (sender, evt) => Exit());
                tray.DoubleClick += (sender, evt) => ShowWindow();
                tray.Visible = true;
                View.Closing += (sender, evt) => { if (!closing) { evt.Cancel = true; dashboardRocket.Reset(); View.Hide(); } };
            }
            if (error != null) SetHint(error, true);
            Text("Permission", admin ? "已授权 · 关闭窗口后留在托盘" : "首次清理或启用自动清理时，需要管理员授权。");
            if (!admin) run.Content = "授权清理";
            timer = new DispatcherTimer(DispatcherPriority.Background, View.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (sender, evt) => Tick();
            UpdateUI();
            PaintMemory(read());
            if (withTray)
            {
                timer.Start();
                if (settings.AutoUpdate)
                {
                    startupUpdateTimer = new DispatcherTimer(DispatcherPriority.Background, View.Dispatcher) { Interval = TimeSpan.FromSeconds(4) };
                    startupUpdateTimer.Tick += (sender, evt) =>
                    {
                        startupUpdateTimer.Stop();
                        if (settings.AutoUpdate) CheckForUpdateAsync(false, true);
                    };
                    startupUpdateTimer.Start();
                }
            }
        }

        internal T Find<T>(string name) where T : class { return (T)View.FindName(name); }
        private CleanMode Selected { get { return strong.IsChecked == true ? CleanMode.Strong : CleanMode.Gentle; } }
        private AutoTrigger SelectedTrigger { get { return triggerThreshold.IsChecked == true ? AutoTrigger.Threshold : AutoTrigger.Interval; } }
        private string ActiveHelp { get { return SelectedTrigger == AutoTrigger.Threshold ? ThresholdHelp : IntervalHelp; } }

        private void SyncTriggerFields()
        {
            bool threshold = SelectedTrigger == AutoTrigger.Threshold;
            seconds.MaxLength = threshold ? 2 : 5;
            seconds.Text = threshold ? settings.ThresholdDisplay : settings.Display;
            triggerUnit.Text = threshold ? "%" : "秒";
            AutomationProperties.SetName(seconds, threshold ? "自动清理占用百分比" : "清理间隔（秒）");
            AutomationProperties.SetHelpText(seconds, threshold ? "输入 50 至 95 之间的整数百分比" : "输入 10 至 86400 之间的整数");
            SetHint(ActiveHelp, false);
        }

        private void SwitchTrigger(AutoTrigger trigger)
        {
            if (schedule.Active) return;
            if (trigger == AutoTrigger.Threshold) triggerThreshold.IsChecked = true; else triggerInterval.IsChecked = true;
            settings = settings.WithTrigger(trigger);
            SyncTriggerFields();
            PersistQuietly();
            UpdateUI();
        }

        private void OnModeClicked(CleanMode mode)
        {
            if (schedule.Active) return;
            GentleOrStrong(mode);
            settings = settings.WithMode(mode);
            PersistQuietly();
            UpdateCompactMenu();
        }

        private void SetSubtleOpacity(bool enabled)
        {
            settings = settings.WithSubtleOpacity(enabled);
            PersistQuietly();
            ApplyCompactOpacity(false);
            UpdateCompactMenu();
        }

        private void SetPauseOnFullscreen(bool enabled)
        {
            settings = settings.WithPauseOnFullscreen(enabled);
            if (!enabled) fullscreenDeferred = false;
            PersistQuietly();
            if (trayFullscreen != null) trayFullscreen.Checked = enabled;
            UpdateUI();
        }

        private void SetAutoUpdate(bool enabled)
        {
            settings = settings.WithAutoUpdate(enabled);
            if (!enabled) pendingAutoInstall = false;
            PersistQuietly();
            if (trayAutoUpdate != null) trayAutoUpdate.Checked = enabled;
            UpdateUpdateUI();
        }

        internal void TriggerUpdateAction()
        {
            if (availableUpdate != null) InstallAvailableUpdateAsync();
            else CheckForUpdateAsync(true, false);
        }

        internal void CheckForUpdateAsync(bool userInitiated, bool allowAutoInstall)
        {
            if (closing || checkingUpdate || installingUpdate) return;
            checkingUpdate = true;
            updateError = null;
            updateUpToDate = false;
            UpdateUpdateUI();
            Task.Factory.StartNew(() =>
            {
                try { return Tuple.Create(checkUpdate(), (string)null); }
                catch (Exception ex) { return Tuple.Create((UpdateInfo)null, FriendlyUpdateError(ex)); }
            }).ContinueWith(task =>
            {
                if (View.Dispatcher.HasShutdownStarted) return;
                View.Dispatcher.BeginInvoke(new Action(() =>
                {
                    checkingUpdate = false;
                    if (closing) return;
                    if (task.Result.Item2 != null)
                    {
                        if (userInitiated) updateError = task.Result.Item2;
                        UpdateUpdateUI();
                        return;
                    }
                    availableUpdate = task.Result.Item1;
                    if (availableUpdate != null)
                    {
                        if (allowAutoInstall && settings.AutoUpdate)
                        {
                            if (busy) pendingAutoInstall = true;
                            else
                            {
                                InstallAvailableUpdateAsync();
                                return;
                            }
                        }
                    }
                    else if (userInitiated)
                    {
                        updateUpToDate = true;
                    }
                    UpdateUpdateUI();
                }));
            });
        }

        internal void InstallAvailableUpdateAsync()
        {
            if (closing || installingUpdate || availableUpdate == null) return;
            if (busy)
            {
                updateError = "正在清理内存，请等本轮结束后再更新。";
                UpdateUpdateUI();
                return;
            }
            UpdateInfo target = availableUpdate;
            string relaunchArg = compact != null && compact.IsVisible ? "--compact"
                : !View.IsVisible ? StartupEntry.TrayOption : "";
            pendingAutoInstall = false;
            installingUpdate = true;
            updateError = null;
            UpdateUI();
            Task.Factory.StartNew(() =>
            {
                try { applyUpdate(target, relaunchArg); return (string)null; }
                catch (Exception ex) { return FriendlyUpdateError(ex); }
            }).ContinueWith(task =>
            {
                if (View.Dispatcher.HasShutdownStarted) return;
                View.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (closing) return;
                    if (task.Result == null)
                    {
                        if (withTray)
                        {
                            closing = true;
                            View.Close();
                        }
                        else
                        {
                            installingUpdate = false;
                            UpdateUI();
                        }
                        return;
                    }
                    installingUpdate = false;
                    updateError = task.Result;
                    UpdateUI();
                }));
            });
        }

        private static string FriendlyUpdateError(Exception ex)
        {
            if (ex is WebException) return "连不上 GitHub，请稍后再试。";
            return string.IsNullOrWhiteSpace(ex.Message) ? "更新未完成，请稍后再试。" : ex.Message;
        }

        private void UpdateUpdateUI()
        {
            string ver = Updater.CurrentDisplayVersion;
            updateButton.IsEnabled = !checkingUpdate && !installingUpdate;
            if (installingUpdate && availableUpdate != null)
            {
                updateButton.Content = "正在更新 " + availableUpdate.DisplayVersion + "…";
                updateButton.Background = (Brush)View.Resources["Panel"];
                updateButton.Foreground = (Brush)View.Resources["Amber"];
                updateButton.ToolTip = "正在下载并安装最新版本，完成后将自动重启。";
            }
            else if (checkingUpdate)
            {
                updateButton.Content = "检查更新中…";
                updateButton.Background = Brushes.Transparent;
                updateButton.Foreground = (Brush)View.Resources["Muted"];
                updateButton.ToolTip = "正在检查 GitHub 最新版本…";
            }
            else if (availableUpdate != null)
            {
                updateButton.Content = "一键更新 " + availableUpdate.DisplayVersion;
                updateButton.Background = (Brush)View.Resources["Lime"];
                updateButton.Foreground = (Brush)View.Resources["MemoryInk"];
                updateButton.ToolTip = updateError != null
                    ? updateError + "（点击重试一键更新）"
                    : "发现新版本 " + availableUpdate.DisplayVersion + "，点击自动下载替换并重启。";
            }
            else if (updateError != null)
            {
                updateButton.Content = ver + " · 检查失败";
                updateButton.Background = Brushes.Transparent;
                updateButton.Foreground = Brushes.LightSalmon;
                updateButton.ToolTip = updateError;
            }
            else if (updateUpToDate)
            {
                updateButton.Content = ver + " · 已是最新";
                updateButton.Background = Brushes.Transparent;
                updateButton.Foreground = (Brush)View.Resources["Muted"];
                updateButton.ToolTip = "当前已是 GitHub 最新版本，点击可再次检查。";
            }
            else
            {
                updateButton.Content = ver + " · 检查更新";
                updateButton.Background = Brushes.Transparent;
                updateButton.Foreground = (Brush)View.Resources["Muted"];
                updateButton.ToolTip = settings.AutoUpdate
                    ? "点击立即检查更新；已开启启动自动更新。"
                    : "点击立即检查更新；启动自动更新已关闭。";
            }
            if (trayUpdate != null)
            {
                trayUpdate.Enabled = !checkingUpdate && !installingUpdate;
                trayUpdate.Text = installingUpdate && availableUpdate != null
                    ? "正在更新 " + availableUpdate.DisplayVersion + "…"
                    : availableUpdate != null ? "一键更新到 " + availableUpdate.DisplayVersion : "检查更新";
            }
            if (trayAutoUpdate != null) trayAutoUpdate.Checked = settings.AutoUpdate;
            UpdateCompactMenu();
        }

        private void PersistQuietly()
        {
            try { settings.Save(settingsPath); }
            catch { }
        }

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

        private bool SaveAutoConfig()
        {
            if (schedule.Active) return false;
            try
            {
                Settings chosen = settings.ParseFor(SelectedTrigger, seconds.Text, Selected);
                chosen.Save(settingsPath);
                settings = chosen;
                seconds.Text = chosen.ActiveValueDisplay;
                SetHint(ActiveHelp, false);
                return true;
            }
            catch (Exception error) { SetHint(error.Message, true); seconds.Focus(); return false; }
        }

        private void ToggleSchedule()
        {
            if (schedule.Active) { Pause(); return; }
            if (busy) return;
            if (!SaveAutoConfig())
            {
                if (compact != null && compact.IsVisible) ShowWindow();
                return;
            }
            RequestClean(true, settings);
        }

        private void Arm()
        {
            if (!admin || busy || closing || schedule.Active) return;
            long now = monotonic();
            schedule.Arm(settings, now);
            fullscreenDeferred = false;
            SetHint(ActiveHelp, false);
            if (settings.Trigger == AutoTrigger.Interval)
            {
                if (settings.PauseOnFullscreen && isFullscreen())
                {
                    fullscreenDeferred = true;
                    UpdateUI();
                }
                else StartClean(settings.Mode, "定时");
            }
            else
            {
                double? used = currentMemory.HasValue ? (double?)(currentMemory.Value.UsedFraction * 100.0) : null;
                if (schedule.IsDue(now, used))
                {
                    if (settings.PauseOnFullscreen && isFullscreen()) fullscreenDeferred = true;
                    else StartClean(settings.Mode, "监控");
                }
                UpdateUI();
            }
        }

        internal void Pause()
        {
            schedule.Pause();
            fullscreenDeferred = false;
            UpdateUI();
        }

        private void ManualClean()
        {
            if (busy || installingUpdate) return;
            settings = settings.WithMode(Selected);
            PersistQuietly();
            RequestClean(false, settings);
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

        internal void RefreshTopProcesses()
        {
            long now = monotonic();
            if (closing || readingTop || (lastTopRead.HasValue && now - lastTopRead.Value < 3 * Stopwatch.Frequency)) return;
            readingTop = true;
            Task.Factory.StartNew(() =>
            {
                try { return readTopProcesses(); }
                catch { return "暂时无法读取进程占用"; }
            }).ContinueWith(task =>
            {
                if (!View.Dispatcher.HasShutdownStarted)
                    View.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        readingTop = false;
                        lastTopRead = monotonic();
                        topProcessSummary = task.Result;
                        Find<Border>("MemoryBlock").ToolTip = topProcessSummary;
                        if (compact != null) ((Border)compact.FindName("CardRoot")).ToolTip = topProcessSummary;
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
                settings = request.Choice.Trigger == AutoTrigger.Threshold
                    ? settings.WithMode(request.Choice.Mode).WithTrigger(AutoTrigger.Threshold).WithThreshold(request.Choice.Threshold)
                    : settings.WithMode(request.Choice.Mode).WithTrigger(AutoTrigger.Interval).WithSeconds(request.Choice.Seconds);
                if (settings.Trigger == AutoTrigger.Threshold) triggerThreshold.IsChecked = true; else triggerInterval.IsChecked = true;
                GentleOrStrong(settings.Mode);
                SyncTriggerFields();
                Arm();
            }
            else
            {
                settings = settings.WithMode(request.Choice.Mode);
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
            if (DateTime.Today != todayDate) { todayDate = DateTime.Today; todayFreedBytes = 0; }
            long now = monotonic();
            Reading? measurement = read();
            PaintMemory(measurement);
            if (nextReading.HasValue && now >= nextReading.Value)
            {
                Text("Later", measurement.HasValue ? Gb(measurement.Value.Available) : "读取失败");
                nextReading = null;
            }
            double? usedPercent = measurement.HasValue ? (double?)(measurement.Value.UsedFraction * 100.0) : null;
            if (!busy && !installingUpdate && schedule.IsDue(now, usedPercent))
            {
                if (settings.PauseOnFullscreen && isFullscreen())
                {
                    fullscreenDeferred = true;
                }
                else
                {
                    fullscreenDeferred = false;
                    StartClean(settings.Mode, schedule.Trigger == AutoTrigger.Threshold ? "监控" : "定时");
                }
            }
            else if (!busy)
            {
                fullscreenDeferred = false;
            }
            UpdateUI();
        }

        private void StartClean(CleanMode chosen, string source)
        {
            if (!admin || closing || busy || installingUpdate) return;
            schedule.Started();
            busy = true; failed = false; fullscreenDeferred = false; nextReading = null; cleanedUntil = null; lastFreedBytes = 0;
            Text("Before", "—"); Text("After", "—"); Text("Later", "—");
            Find<TextBlock>("Delta").Visibility = Visibility.Collapsed;
            SetResult(source + "清理中…");
            UpdateUI();
            TriggerRocketLaunch();
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
            long freed = outcome.Success ? outcome.FreedBytes : 0;
            if (outcome.Success)
            {
                long now = monotonic();
                schedule.Finished(now);
                cleanedUntil = now + 8 * Stopwatch.Frequency;
                if (DateTime.Today != todayDate) { todayDate = DateTime.Today; todayFreedBytes = 0; }
                lastFreedBytes = freed;
                todayFreedBytes += freed;
            }
            Text("Before", outcome.Before.HasValue ? Gb(outcome.Before.Value.Available) : "—");
            Text("After", outcome.After.HasValue ? Gb(outcome.After.Value.Available) : "—");
            var delta = Find<TextBlock>("Delta");
            if (outcome.Success && outcome.Before.HasValue && outcome.After.HasValue)
            {
                long signed = outcome.SignedDeltaBytes;
                delta.Text = signed > 0 ? "本次释放 +" + Gb(signed)
                    : signed < 0 ? "可用变化 -" + Gb(-signed)
                    : "空间持平 0.00 GB";
                delta.Foreground = (Brush)View.Resources[signed > 0 ? "Lime" : "Muted"];
                delta.Visibility = Visibility.Visible;
            }
            else
            {
                delta.Visibility = Visibility.Collapsed;
            }
            if (outcome.After.HasValue)
            {
                nextReading = monotonic() + 30 * Stopwatch.Frequency;
                bool shortInterval = schedule.Active && schedule.Trigger == AutoTrigger.Interval && settings.Seconds < 30;
                Text("Later", shortInterval ? "间隔不足" : "等待读数");
                PaintMemory(outcome.After);
            }
            string daily = outcome.Success && todayFreedBytes > 0 ? " · 今日累计 +" + Gb(todayFreedBytes) : "";
            string pauseNote = stopped ? (schedule.Trigger == AutoTrigger.Threshold ? "；监控已暂停。" : "；定时已暂停。") : "";
            SetResult(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + source + " · " + outcome.Summary + daily + pauseNote);
            UpdateUI();
            if (pendingAutoInstall && settings.AutoUpdate && availableUpdate != null && !closing)
                InstallAvailableUpdateAsync();
        }

        private static string Gb(ulong bytes) { return (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB"; }
        private static string Gb(long bytes) { return (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB"; }
        private static string ShortDelta(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 0.1) return "+" + gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return "+" + Math.Round(bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        private void PaintMemory(Reading? measurement)
        {
            currentMemory = measurement;
            double? fraction = measurement.HasValue ? (double?)measurement.Value.UsedFraction : null;
            double p = fraction.HasValue ? fraction.Value * 100.0 : 0;
            Text("Usage", measurement.HasValue ? p.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—");
            var meter = Find<ProgressBar>("Meter");
            meter.Value = p;
            meter.Foreground = DialArt.MeterBrush(fraction);
            Find<Image>("MiniDial").Source = DialArt.Draw(fraction);
            dashboardRocket.SetAccent(DialArt.AccentBrush(fraction));
            Text("Available", measurement.HasValue ? Gb(measurement.Value.Available) : "—");
            Text("Physical", measurement.HasValue ? Gb(measurement.Value.Total) : "—");
            Text("Commit", measurement.HasValue ? Gb(measurement.Value.Committed) + " / " + Gb(measurement.Value.CommitLimit) : "—");
            PaintCompact();
        }

        private void UpdateUI()
        {
            bool enabled = schedule.Active;
            bool thresholdActive = enabled && schedule.Trigger == AutoTrigger.Threshold;
            bool thresholdSelected = SelectedTrigger == AutoTrigger.Threshold;
            Text("ScheduleState", !enabled ? (thresholdSelected ? "监控未启用" : "定时未启用")
                : thresholdActive ? "监控已启用" : "定时已启用");
            Find<Ellipse>("ScheduleDot").Fill = (Brush)View.Resources[enabled ? "Lime" : "Muted"];

            int? left = schedule.SecondsLeft(monotonic());
            TextBlock nextLabel = Find<TextBlock>("Next");
            if (!enabled)
            {
                nextLabel.Text = thresholdSelected ? "自动监控：未启用" : "下次清理：未启用";
                nextLabel.Foreground = (Brush)View.Resources["Muted"];
            }
            else if (busy)
            {
                nextLabel.Text = "本轮正在清理…";
                nextLabel.Foreground = (Brush)View.Resources["Amber"];
            }
            else if (fullscreenDeferred)
            {
                nextLabel.Text = "全屏避让中";
                nextLabel.Foreground = (Brush)View.Resources["Azure"];
            }
            else if (thresholdActive)
            {
                nextLabel.Text = left.HasValue ? "间隔 " + left.Value + " 秒后恢复" : "达到 " + schedule.Threshold + "% 自动清";
                nextLabel.Foreground = (Brush)View.Resources["Amber"];
            }
            else
            {
                nextLabel.Text = left.HasValue ? "下次清理：" + left.Value + " 秒后" : "下次清理：就绪";
                nextLabel.Foreground = (Brush)View.Resources["Amber"];
            }

            arm.Content = enabled
                ? (thresholdActive ? "暂停监控" : "暂停定时")
                : (thresholdSelected ? "启用监控" : "启用定时");
            seconds.IsEnabled = gentle.IsEnabled = strong.IsEnabled = triggerInterval.IsEnabled = triggerThreshold.IsEnabled = !enabled;
            arm.IsEnabled = enabled || (!busy && !installingUpdate);
            run.IsEnabled = !busy && !installingUpdate;
            if (admin) run.Content = busy ? "正在清理…" : "立即清理";
            if (trayClean != null) trayClean.Enabled = !busy && !installingUpdate;
            if (trayPause != null)
            {
                trayPause.Enabled = enabled;
                trayPause.Text = thresholdActive ? "暂停监控" : "暂停自动清理";
            }
            if (trayFullscreen != null) trayFullscreen.Checked = settings.PauseOnFullscreen;
            UpdateUpdateUI();
            UpdateCompact();
        }

        internal Window CompactView { get { return compact; } }

        internal static Point ResolveCompactLocation(Settings current, double width, double height, Rect workArea, Rect[] screenAreas)
        {
            if (current.CompactLeft.HasValue && current.CompactTop.HasValue && screenAreas != null)
            {
                var saved = new Rect(current.CompactLeft.Value, current.CompactTop.Value, width, height);
                foreach (Rect screen in screenAreas)
                {
                    Rect overlap = Rect.Intersect(saved, screen);
                    if (!overlap.IsEmpty && overlap.Width >= 32 && overlap.Height >= 32)
                    {
                        double clampedX = Math.Max(screen.Left, Math.Min(screen.Right - width, saved.X));
                        double clampedY = Math.Max(screen.Top, Math.Min(screen.Bottom - height, saved.Y));
                        return new Point(clampedX, clampedY);
                    }
                }
            }
            return new Point(workArea.Right - width - 22, workArea.Bottom - height - 22);
        }

        private Rect[] CurrentScreenAreas()
        {
            double scaleX = 1.0, scaleY = 1.0;
            PresentationSource source = PresentationSource.FromVisual(View);
            if (source != null && source.CompositionTarget != null)
            {
                Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                scaleX = fromDevice.M11; scaleY = fromDevice.M22;
            }
            Forms.Screen[] screens = Forms.Screen.AllScreens;
            var areas = new Rect[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                var r = screens[i].WorkingArea;
                areas[i] = new Rect(r.Left * scaleX, r.Top * scaleY, r.Width * scaleX, r.Height * scaleY);
            }
            return areas;
        }

        private void PlaceCompactSafely()
        {
            if (compact == null) return;
            if (!withTray)
            {
                compact.Left = -10000; compact.Top = -10000;
                return;
            }
            Point pos = ResolveCompactLocation(settings, compact.Width, compact.Height, SystemParameters.WorkArea, CurrentScreenAreas());
            compact.Left = pos.X;
            compact.Top = pos.Y;
        }

        internal void ShowCompact()
        {
            if (closing) return;
            if (compact == null)
            {
                using (Stream layout = typeof(Dashboard).Assembly.GetManifestResourceStream("MemoryClock.Compact"))
                    compact = (Window)XamlReader.Load(layout);
                PlaceCompactSafely();
                compact.ShowActivated = withTray;
                compact.Topmost = withTray;

                var cardRoot = (Border)compact.FindName("CardRoot");
                cardRoot.ToolTip = topProcessSummary;
                cardRoot.MouseEnter += (sender, evt) => RefreshTopProcesses();
                cardRoot.ToolTipOpening += (sender, evt) => RefreshTopProcesses();

                ((FrameworkElement)compact.FindName("Handle")).MouseLeftButtonDown += (sender, evt) =>
                {
                    if (evt.LeftButton != MouseButtonState.Pressed) return;
                    compact.DragMove();
                    if (withTray)
                    {
                        settings = settings.WithCompactLocation(compact.Left, compact.Top);
                        PersistQuietly();
                    }
                };
                compactRocket = RocketRig.Attach(compact, "Rocket");
                var cleanButton = (Button)compact.FindName("Clean");
                cleanButton.Click += (sender, evt) => ManualClean();
                cleanButton.MouseEnter += (sender, evt) => { if (cleanButton.IsEnabled) compactRocket.SetHover(true, withTray); };
                cleanButton.MouseLeave += (sender, evt) => compactRocket.SetHover(false, withTray);
                cleanButton.PreviewMouseLeftButtonDown += (sender, evt) => { if (cleanButton.IsEnabled) compactRocket.SetPressed(true, withTray); };
                cleanButton.PreviewMouseLeftButtonUp += (sender, evt) => compactRocket.SetPressed(false, withTray);
                cleanButton.MouseMove += (sender, evt) => { if (evt.LeftButton == MouseButtonState.Pressed) compactRocket.SetPressed(cleanButton.IsEnabled && cleanButton.IsPressed, withTray); };
                cleanButton.LostMouseCapture += (sender, evt) => compactRocket.SetPressed(false, withTray);
                cleanButton.IsEnabledChanged += (sender, evt) => { if (!cleanButton.IsEnabled) compactRocket.SetHover(false, withTray); };
                compact.StateChanged += (sender, evt) => { if (compact.WindowState == WindowState.Minimized && compactRocket != null) compactRocket.Reset(); };
                ((Button)compact.FindName("Expand")).Click += (sender, evt) => ShowWindow();

                compact.MouseEnter += (sender, evt) => ApplyCompactOpacity(true);
                compact.MouseLeave += (sender, evt) => ApplyCompactOpacity(true);
                compact.IsKeyboardFocusWithinChanged += (sender, evt) => ApplyCompactOpacity(true);

                var menu = (ContextMenu)compact.FindName("CompactMenu");
                menu.Opened += (sender, evt) => { compactMenuOpen = true; UpdateCompactMenu(); ApplyCompactOpacity(true); };
                menu.Closed += (sender, evt) => { compactMenuOpen = false; ApplyCompactOpacity(true); };
                ((MenuItem)compact.FindName("MenuClean")).Click += (sender, evt) => ManualClean();
                ((MenuItem)compact.FindName("MenuGentle")).Click += (sender, evt) => OnModeClicked(CleanMode.Gentle);
                ((MenuItem)compact.FindName("MenuStrong")).Click += (sender, evt) => OnModeClicked(CleanMode.Strong);
                ((MenuItem)compact.FindName("MenuAuto")).Click += (sender, evt) => ToggleSchedule();
                ((MenuItem)compact.FindName("MenuSubtle")).Click += (sender, evt) => SetSubtleOpacity(!settings.SubtleOpacity);
                ((MenuItem)compact.FindName("MenuFullscreen")).Click += (sender, evt) => SetPauseOnFullscreen(!settings.PauseOnFullscreen);
                ((MenuItem)compact.FindName("MenuAutoUpdate")).Click += (sender, evt) => SetAutoUpdate(!settings.AutoUpdate);
                ((MenuItem)compact.FindName("MenuUpdate")).Click += (sender, evt) => TriggerUpdateAction();
                ((MenuItem)compact.FindName("MenuExpand")).Click += (sender, evt) => ShowWindow();
                ((MenuItem)compact.FindName("MenuExit")).Click += (sender, evt) => Exit();

                compact.Closing += (sender, evt) => { if (!closing) { evt.Cancel = true; if (compactRocket != null) compactRocket.Reset(); compact.Hide(); } };
                compact.Closed += (sender, evt) => { if (compactRocket != null) compactRocket.Reset(); compactRocket = null; compact = null; };
                PaintCompact();
                UpdateCompact();
                ApplyCompactOpacity(false);
            }
            else
            {
                PlaceCompactSafely();
            }
            if (View.IsVisible)
            {
                dashboardRocket.Reset();
                View.Hide();
            }
            compact.Show();
            if (busy) TriggerRocketLaunch();
        }

        internal RocketRig CompactRocket { get { return compactRocket; } }
        internal RocketRig DashboardRocket { get { return dashboardRocket; } }

        private void TriggerRocketLaunch()
        {
            if (!withTray || !SystemParameters.ClientAreaAnimation) return;
            if (compact != null && compact.IsVisible && compactRocket != null)
            {
                compactRocket.Launch(
                    () => !closing && busy && compact != null && compact.IsVisible && compact.WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation,
                    () => !closing && compact != null && compact.IsVisible && ((Button)compact.FindName("Clean")).IsEnabled && ((Button)compact.FindName("Clean")).IsMouseOver);
            }
            if (View.IsVisible && View.WindowState != WindowState.Minimized)
            {
                dashboardRocket.Launch(
                    () => !closing && busy && View.IsVisible && View.WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation,
                    () => !closing && View.IsVisible && (Find<Button>("Compact").IsMouseOver || (run.IsEnabled && run.IsMouseOver)));
            }
        }

        private void ApplyCompactOpacity(bool animate)
        {
            if (compact == null) return;
            bool engaged = !withTray || !settings.SubtleOpacity || compact.IsMouseOver || compact.IsKeyboardFocusWithin || compactMenuOpen;
            double target = engaged ? 1.0 : SubtleIdleOpacity;
            if (!animate)
            {
                compact.BeginAnimation(UIElement.OpacityProperty, null);
                compact.Opacity = target;
                return;
            }
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(engaged ? 140 : 210))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            compact.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static string DialGb(ulong bytes)
        { return (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture); }

        private void PaintCompact()
        {
            if (compact == null) return;
            double? fraction = currentMemory.HasValue ? (double?)currentMemory.Value.UsedFraction : null;
            TextBlock used = (TextBlock)compact.FindName("Used"), free = (TextBlock)compact.FindName("Free");
            used.Text = currentMemory.HasValue ? DialGb(currentMemory.Value.Total - Math.Min(currentMemory.Value.Total, currentMemory.Value.Available)) : "—";
            free.Text = currentMemory.HasValue ? DialGb(currentMemory.Value.Available) : "—";
            var percent = (TextBlock)compact.FindName("DialPercent");
            percent.Text = fraction.HasValue ? fraction.Value.ToString("0%", CultureInfo.InvariantCulture) : "—";
            percent.Foreground = DialArt.AccentBrush(fraction);
            ((Image)compact.FindName("Dial")).Source = Find<Image>("MiniDial").Source;
        }

        private void UpdateCompactMenu()
        {
            if (compact == null) return;
            bool enabled = schedule.Active;
            ((MenuItem)compact.FindName("MenuClean")).IsEnabled = !busy && !installingUpdate;
            var mGentle = (MenuItem)compact.FindName("MenuGentle");
            var mStrong = (MenuItem)compact.FindName("MenuStrong");
            mGentle.IsChecked = Selected == CleanMode.Gentle;
            mStrong.IsChecked = Selected == CleanMode.Strong;
            mGentle.IsEnabled = mStrong.IsEnabled = !enabled;
            var mAuto = (MenuItem)compact.FindName("MenuAuto");
            mAuto.IsEnabled = enabled || !busy;
            mAuto.Header = enabled
                ? (schedule.Trigger == AutoTrigger.Threshold ? "暂停监控" : "暂停定时")
                : (SelectedTrigger == AutoTrigger.Threshold ? "启用占用监控" : "启用定时清理");
            ((MenuItem)compact.FindName("MenuSubtle")).IsChecked = settings.SubtleOpacity;
            ((MenuItem)compact.FindName("MenuFullscreen")).IsChecked = settings.PauseOnFullscreen;
            ((MenuItem)compact.FindName("MenuAutoUpdate")).IsChecked = settings.AutoUpdate;
            var mUpdate = (MenuItem)compact.FindName("MenuUpdate");
            mUpdate.IsEnabled = !checkingUpdate && !installingUpdate;
            mUpdate.Header = installingUpdate && availableUpdate != null
                ? "正在更新 " + availableUpdate.DisplayVersion + "…"
                : availableUpdate != null ? "一键更新到 " + availableUpdate.DisplayVersion : "检查更新";
        }

        private void UpdateCompact()
        {
            if (compact == null) return;
            var button = (Button)compact.FindName("Clean");
            button.IsEnabled = !busy && !installingUpdate;
            button.ToolTip = admin ? "立即清理内存" : "授权后立即清理内存";
            var status = (TextBlock)compact.FindName("Status");
            bool justCleaned = cleanedUntil.HasValue && monotonic() < cleanedUntil.Value;
            string cleanedText = lastFreedBytes >= 10L * 1024 * 1024 ? ShortDelta(lastFreedBytes) : "刚清理完";
            string idleAuto = schedule.Trigger == AutoTrigger.Threshold ? "监控中" : "定时中";
            string label = busy ? "正在清理"
                : failed ? "未完成"
                : justCleaned ? cleanedText
                : fullscreenDeferred ? "全屏避让"
                : schedule.Active ? idleAuto
                : "就绪";
            if (status.Text != label)
            {
                status.Text = label;
                Announce(status);
            }
            status.Foreground = busy ? (Brush)View.Resources["Amber"]
                : failed ? Brushes.LightSalmon
                : justCleaned ? (Brush)View.Resources["Lime"]
                : (Brush)View.Resources[(schedule.Active || fullscreenDeferred) ? "Azure" : "Muted"];
            status.ToolTip = resultMessage;
            UpdateCompactMenu();
        }

        private void ShowWindow()
        {
            if (compact != null && compact.IsVisible)
            {
                if (compactRocket != null) compactRocket.Reset();
                compact.Hide();
            }
            if (!View.IsVisible) View.Show();
            if (View.WindowState == WindowState.Minimized) View.WindowState = WindowState.Normal;
            if (withTray) View.Activate();
            if (busy) TriggerRocketLaunch();
        }

        private void Exit()
        {
            if (busy || installingUpdate) { SetResult(installingUpdate ? "正在更新，请稍候完成。" : "正在清理，请等本轮结束再退出。"); return; }
            closing = true; View.Close();
        }

        internal void AllowShutdown() { closing = true; }

        public void Dispose()
        {
            timer.Stop();
            if (startupUpdateTimer != null) startupUpdateTimer.Stop();
            closing = true;
            if (compact != null) compact.Close();
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); icon.Dispose(); }
        }
    }
}
