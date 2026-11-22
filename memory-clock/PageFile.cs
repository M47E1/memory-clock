using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;

namespace MemoryClock
{
    // Windows owns configuration, validation and restart prompts. We read the
    // allocation and open the native settings; no registry or WMI writes.
    internal static class PageFile
    {
        internal static string PerformanceSettingsPath
        { get { return Path.Combine(Environment.SystemDirectory, "SystemPropertiesPerformance.exe"); } }

        internal static void OpenSettings()
        {
            using (Process process = Process.Start(new ProcessStartInfo(PerformanceSettingsPath) { UseShellExecute = true })) { }
        }

        internal static string ReadSummary()
        {
            var active = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            Query("SELECT Name, AllocatedBaseSize FROM Win32_PageFileUsage",
                row => active[(string)row["Name"]] = (uint)row["AllocatedBaseSize"]);
            if (active.Count == 0) return "当前未分配分页文件";
            var maximum = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Query("SELECT Name, MaximumSize FROM Win32_PageFileSetting",
                    row => maximum[(string)row["Name"]] = (uint)row["MaximumSize"]);
            }
            catch (ManagementException) { maximum.Clear(); }
            catch (UnauthorizedAccessException) { maximum.Clear(); }
            return FormatSummary(active, maximum);
        }

        internal static string FormatSummary(Dictionary<string, uint> active, Dictionary<string, uint> maximum)
        {
            string drives = string.Join(" / ", active.Keys.Select(Path.GetPathRoot)
                .Where(root => !string.IsNullOrEmpty(root)).Select(root => root.TrimEnd('\\'))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(root => root, StringComparer.OrdinalIgnoreCase));
            long allocated = active.Values.Sum(size => (long)size);
            uint cap;
            string configured = active.Count == 1 && maximum.TryGetValue(active.Keys.First(), out cap) && cap > 0
                ? " · 设定上限 " + Gb(cap) : "";
            return (drives.Length > 0 ? drives + " · " : "") + "已分配 " + Gb(allocated) + configured;
        }

        private static string Gb(long megabytes)
        { return (megabytes / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB"; }

        private static void Query(string query, Action<ManagementObject> read)
        {
            using (var search = new ManagementObjectSearcher(query))
            {
                search.Options.Timeout = TimeSpan.FromSeconds(3);
                using (ManagementObjectCollection rows = search.Get())
                    foreach (ManagementObject row in rows) using (row) read(row);
            }
        }
    }
}
