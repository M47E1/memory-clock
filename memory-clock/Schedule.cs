using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MemoryClock
{
    internal enum CleanMode { Gentle, Strong }
    internal sealed class Settings
    {
        internal const int MinimumSeconds = 10;
        internal const int MaximumSeconds = 86400;
        internal readonly int Seconds;
        internal readonly CleanMode Mode;
        private Settings(int seconds, CleanMode mode) { Seconds = seconds; Mode = mode; }
        internal static Settings Default { get { return new Settings(500, CleanMode.Gentle); } }
        internal string Display { get { return Seconds.ToString(CultureInfo.InvariantCulture); } }
        internal Settings WithMode(CleanMode mode) { return new Settings(Seconds, mode); }

        internal static Settings Parse(string input, CleanMode mode)
        {
            int seconds;
            if (!int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                seconds < MinimumSeconds || seconds > MaximumSeconds)
                throw new FormatException("请输入 10 至 86400 之间的整数秒数。");
            return new Settings(seconds, mode);
        }

        internal static Settings Load(string path)
        {
            string[] lines;
            try
            {
                if (new FileInfo(path).Length > 1024) throw new FormatException("设置文件格式有误，请重新保存。");
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (FileNotFoundException) { return Default; }
            catch (DirectoryNotFoundException) { return Default; }
            if (lines.Length != 3 || (lines[0] != "1" && lines[0] != "2") || (lines[1] != "Gentle" && lines[1] != "Strong"))
                throw new FormatException("设置文件格式有误，请重新保存。");
            CleanMode mode = lines[1] == "Strong" ? CleanMode.Strong : CleanMode.Gentle;
            return lines[0] == "1" ? Default.WithMode(mode) : Parse(lines[2], mode);
        }

        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, new[] { "2", Mode.ToString(), Display }, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class IntervalTimer
    {
        private long interval;
        private long? due;
        internal bool Active { get { return interval > 0; } }
        internal void Arm(Settings settings, long now) { interval = settings.Seconds * Stopwatch.Frequency; due = now; }
        internal void Pause() { interval = 0; due = null; }
        internal bool IsDue(long now) { return due.HasValue && now >= due.Value; }
        internal void Started() { due = null; }
        internal void Finished(long now) { if (Active) due = now + interval; }
        internal int? SecondsLeft(long now)
        {
            return due.HasValue ? (int?)Math.Ceiling((double)Math.Max(0L, due.Value - now) / Stopwatch.Frequency) : null;
        }
    }
}
