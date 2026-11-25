using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MemoryClock
{
    internal enum CleanMode { Gentle, Strong }
    internal enum AutoTrigger { Interval, Threshold }

    internal sealed class Settings
    {
        internal const int MinimumSeconds = 10;
        internal const int MaximumSeconds = 86400;
        internal const int DefaultSeconds = 500;
        internal const int MinimumThreshold = 50;
        internal const int MaximumThreshold = 95;
        internal const int DefaultThreshold = 85;
        internal const int ThresholdCooldownSeconds = 60;

        internal readonly int Seconds;
        internal readonly int Threshold;
        internal readonly CleanMode Mode;
        internal readonly AutoTrigger Trigger;
        internal readonly bool SubtleOpacity;
        internal readonly bool PauseOnFullscreen;
        internal readonly bool AutoUpdate;
        internal readonly double? CompactLeft;
        internal readonly double? CompactTop;

        private Settings(int seconds, int threshold, CleanMode mode, AutoTrigger trigger,
            bool subtleOpacity, bool pauseOnFullscreen, bool autoUpdate, double? compactLeft, double? compactTop)
        {
            Seconds = seconds;
            Threshold = threshold;
            Mode = mode;
            Trigger = trigger;
            SubtleOpacity = subtleOpacity;
            PauseOnFullscreen = pauseOnFullscreen;
            AutoUpdate = autoUpdate;
            CompactLeft = compactLeft;
            CompactTop = compactTop;
        }

        internal static Settings Default
        {
            get { return new Settings(DefaultSeconds, DefaultThreshold, CleanMode.Gentle, AutoTrigger.Interval, true, true, true, null, null); }
        }

        internal string Display { get { return Seconds.ToString(CultureInfo.InvariantCulture); } }
        internal string ThresholdDisplay { get { return Threshold.ToString(CultureInfo.InvariantCulture); } }
        internal string ActiveValueDisplay { get { return Trigger == AutoTrigger.Threshold ? ThresholdDisplay : Display; } }

        internal Settings WithMode(CleanMode mode)
        { return new Settings(Seconds, Threshold, mode, Trigger, SubtleOpacity, PauseOnFullscreen, AutoUpdate, CompactLeft, CompactTop); }

        internal Settings WithTrigger(AutoTrigger trigger)
        { return new Settings(Seconds, Threshold, Mode, trigger, SubtleOpacity, PauseOnFullscreen, AutoUpdate, CompactLeft, CompactTop); }

        internal Settings WithSeconds(int seconds)
        { return new Settings(seconds, Threshold, Mode, Trigger, SubtleOpacity, PauseOnFullscreen, AutoUpdate, CompactLeft, CompactTop); }

        internal Settings WithThreshold(int threshold)
        { return new Settings(Seconds, threshold, Mode, Trigger, SubtleOpacity, PauseOnFullscreen, AutoUpdate, CompactLeft, CompactTop); }

        internal Settings WithSubtleOpacity(bool enabled)
        { return new Settings(Seconds, Threshold, Mode, Trigger, enabled, PauseOnFullscreen, AutoUpdate, CompactLeft, CompactTop); }

        internal Settings WithPauseOnFullscreen(bool enabled)
        { return new Settings(Seconds, Threshold, Mode, Trigger, SubtleOpacity, enabled, AutoUpdate, CompactLeft, CompactTop); }

        internal Settings WithAutoUpdate(bool enabled)
        { return new Settings(Seconds, Threshold, Mode, Trigger, SubtleOpacity, PauseOnFullscreen, enabled, CompactLeft, CompactTop); }

        internal Settings WithCompactLocation(double left, double top)
        { return new Settings(Seconds, Threshold, Mode, Trigger, SubtleOpacity, PauseOnFullscreen, AutoUpdate, left, top); }

        internal static int ParseSeconds(string input)
        {
            int seconds;
            if (input == null || !int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                seconds < MinimumSeconds || seconds > MaximumSeconds)
                throw new FormatException("请输入 10 至 86400 之间的整数秒数。");
            return seconds;
        }

        internal static int ParseThreshold(string input)
        {
            int percent;
            if (input == null || !int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out percent) ||
                percent < MinimumThreshold || percent > MaximumThreshold)
                throw new FormatException("请输入 50 至 95 之间的整数百分比。");
            return percent;
        }

        internal static Settings Parse(string input, CleanMode mode)
        { return Default.WithMode(mode).WithSeconds(ParseSeconds(input)); }

        internal Settings ParseFor(AutoTrigger trigger, string input, CleanMode mode)
        {
            return trigger == AutoTrigger.Threshold
                ? WithMode(mode).WithTrigger(AutoTrigger.Threshold).WithThreshold(ParseThreshold(input))
                : WithMode(mode).WithTrigger(AutoTrigger.Interval).WithSeconds(ParseSeconds(input));
        }

        internal static Settings Load(string path)
        {
            string[] lines;
            try
            {
                if (new FileInfo(path).Length > 2048) throw new FormatException("设置文件格式有误，请重新保存。");
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (FileNotFoundException) { return Default; }
            catch (DirectoryNotFoundException) { return Default; }
            if (lines.Length < 3 || (lines[1] != "Gentle" && lines[1] != "Strong"))
                throw new FormatException("设置文件格式有误，请重新保存。");
            CleanMode mode = lines[1] == "Strong" ? CleanMode.Strong : CleanMode.Gentle;
            if (lines[0] == "1")
            {
                if (lines.Length != 3) throw new FormatException("设置文件格式有误，请重新保存。");
                return Default.WithMode(mode);
            }
            if (lines[0] == "2")
            {
                if (lines.Length != 3) throw new FormatException("设置文件格式有误，请重新保存。");
                return Default.WithMode(mode).WithSeconds(ParseSeconds(lines[2]));
            }
            if (lines[0] == "3" || lines[0] == "4")
            {
                bool v4 = lines[0] == "4";
                if (lines.Length != (v4 ? 9 : 8) || (lines[3] != "Interval" && lines[3] != "Threshold") ||
                    (lines[5] != "0" && lines[5] != "1") || (lines[6] != "0" && lines[6] != "1") ||
                    (v4 && lines[8] != "0" && lines[8] != "1"))
                    throw new FormatException("设置文件格式有误，请重新保存。");
                int seconds = ParseSeconds(lines[2]);
                AutoTrigger trigger = lines[3] == "Threshold" ? AutoTrigger.Threshold : AutoTrigger.Interval;
                int threshold = ParseThreshold(lines[4]);
                bool subtleOpacity = lines[5] == "1";
                bool pauseOnFullscreen = lines[6] == "1";
                bool autoUpdate = !v4 || lines[8] == "1";
                double? left = null, top = null;
                if (!string.IsNullOrWhiteSpace(lines[7]))
                {
                    string[] pair = lines[7].Split(',');
                    double parsedLeft, parsedTop;
                    if (pair.Length != 2 ||
                        !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedLeft) ||
                        !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedTop) ||
                        double.IsNaN(parsedLeft) || double.IsInfinity(parsedLeft) ||
                        double.IsNaN(parsedTop) || double.IsInfinity(parsedTop))
                        throw new FormatException("设置文件格式有误，请重新保存。");
                    left = parsedLeft; top = parsedTop;
                }
                return new Settings(seconds, threshold, mode, trigger, subtleOpacity, pauseOnFullscreen, autoUpdate, left, top);
            }
            throw new FormatException("设置文件格式有误，请重新保存。");
        }

        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string location = CompactLeft.HasValue && CompactTop.HasValue
                ? Math.Round(CompactLeft.Value).ToString(CultureInfo.InvariantCulture) + "," +
                  Math.Round(CompactTop.Value).ToString(CultureInfo.InvariantCulture)
                : "";
            try
            {
                File.WriteAllLines(temp, new[]
                {
                    "4",
                    Mode.ToString(),
                    Display,
                    Trigger.ToString(),
                    ThresholdDisplay,
                    SubtleOpacity ? "1" : "0",
                    PauseOnFullscreen ? "1" : "0",
                    location,
                    AutoUpdate ? "1" : "0"
                }, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class IntervalTimer
    {
        private bool active, inFlight;
        private AutoTrigger trigger;
        private long interval, cooldown;
        private int threshold;
        private long? due, cooldownUntil, lastFinished;

        internal bool Active { get { return active; } }
        internal AutoTrigger Trigger { get { return trigger; } }
        internal int Threshold { get { return threshold; } }

        internal void Arm(Settings settings, long now)
        {
            active = true;
            inFlight = false;
            trigger = settings.Trigger;
            if (trigger == AutoTrigger.Threshold)
            {
                threshold = settings.Threshold;
                cooldown = Settings.ThresholdCooldownSeconds * Stopwatch.Frequency;
                interval = 0;
                due = null;
                cooldownUntil = lastFinished.HasValue && now >= lastFinished.Value && now < lastFinished.Value + cooldown
                    ? (long?)(lastFinished.Value + cooldown) : null;
            }
            else
            {
                interval = settings.Seconds * Stopwatch.Frequency;
                cooldown = 0;
                threshold = 0;
                due = now;
                cooldownUntil = null;
            }
        }

        internal void Pause()
        {
            active = false;
            inFlight = false;
            interval = 0;
            cooldown = 0;
            threshold = 0;
            due = null;
            cooldownUntil = null;
        }

        internal bool IsDue(long now)
        { return active && !inFlight && trigger == AutoTrigger.Interval && due.HasValue && now >= due.Value; }

        internal bool IsDue(long now, double? usedPercent)
        {
            if (!active || inFlight) return false;
            if (trigger == AutoTrigger.Interval) return due.HasValue && now >= due.Value;
            return usedPercent.HasValue && usedPercent.Value + 1e-6 >= threshold &&
                (!cooldownUntil.HasValue || now >= cooldownUntil.Value);
        }

        internal void Started() { inFlight = true; due = null; cooldownUntil = null; }

        internal void Finished(long now)
        {
            inFlight = false;
            lastFinished = now;
            if (!active) return;
            if (trigger == AutoTrigger.Threshold) cooldownUntil = now + cooldown;
            else due = now + interval;
        }

        internal int? SecondsLeft(long now)
        {
            if (!active || inFlight) return null;
            if (trigger == AutoTrigger.Threshold)
            {
                if (!cooldownUntil.HasValue || cooldownUntil.Value <= now) return null;
                return (int)Math.Ceiling((double)(cooldownUntil.Value - now) / Stopwatch.Frequency);
            }
            return due.HasValue ? (int?)Math.Ceiling((double)Math.Max(0L, due.Value - now) / Stopwatch.Frequency) : null;
        }
    }
}
