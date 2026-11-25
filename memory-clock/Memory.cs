// Windows cleanup calls adapted 2026-11-22 from Mem Reduct v.3.5.2,
// src/main.c (_app_memoryclean). Copyright (c) 2011-2025 Henry++.
// GPL version 3. See README.md and the included upstream source and license.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace MemoryClock
{
    internal struct Reading
    {
        internal ulong Total, Available, Committed, CommitLimit;
        internal double UsedFraction { get { return Total == 0 ? 0 : 1.0 - (double)Available / Total; } }
    }

    internal enum Area { WorkingSets = 2, FileCache = 81, LowStandby = 5 }

    internal sealed class CleanResult
    {
        internal readonly string Summary;
        internal readonly bool Success;
        internal readonly Reading? Before, After;
        internal CleanResult(string summary, bool success, Reading? before, Reading? after)
        { Summary = summary; Success = success; Before = before; After = after; }

        internal long SignedDeltaBytes
        {
            get
            {
                if (!Before.HasValue || !After.HasValue) return 0;
                if (After.Value.Available >= Before.Value.Available)
                {
                    ulong up = After.Value.Available - Before.Value.Available;
                    return up > (ulong)long.MaxValue ? long.MaxValue : (long)up;
                }
                ulong down = Before.Value.Available - After.Value.Available;
                return down > (ulong)long.MaxValue ? long.MinValue : -(long)down;
            }
        }

        internal long FreedBytes
        {
            get
            {
                long delta = SignedDeltaBytes;
                return delta > 0 ? delta : 0;
            }
        }
    }

    internal static class Cleaner
    {
        internal static CleanResult Run(CleanMode mode, Func<Reading?> sample, Action<Area> apply)
        {
            Reading? before = sample();
            int finished = 0;
            try
            {
                if (mode != CleanMode.Gentle && mode != CleanMode.Strong) throw new ArgumentOutOfRangeException("mode");
                Area[] areas = mode == CleanMode.Strong
                    ? new[] { Area.WorkingSets, Area.FileCache, Area.LowStandby }
                    : new[] { Area.FileCache, Area.LowStandby };
                foreach (Area area in areas) { apply(area); finished++; }
                return new CleanResult("已完成", true, before, sample());
            }
            catch (Exception error)
            {
                return new CleanResult(finished > 0 ? "部分完成：" + error.Message : error.Message, false, before, sample());
            }
        }
    }

    internal static class WindowsMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PerformanceInfo
        {
            internal uint Size;
            internal UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            internal uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
        private static extern bool GetPerformanceInfo(out PerformanceInfo info, uint size);
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        internal static Reading? Read()
        {
            PerformanceInfo info;
            if (!GetPerformanceInfo(out info, (uint)Marshal.SizeOf(typeof(PerformanceInfo)))) return null;
            ulong page = info.PageSize.ToUInt64(), total = info.PhysicalTotal.ToUInt64() * page;
            if (total == 0) return null;
            return new Reading { Total = total, Available = Math.Min(total, info.PhysicalAvailable.ToUInt64() * page),
                Committed = info.CommitTotal.ToUInt64() * page, CommitLimit = info.CommitLimit.ToUInt64() * page };
        }

        internal static bool IsFullscreenBusy()
        {
            int state;
            // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4).
            return SHQueryUserNotificationState(out state) == 0 && (state == 2 || state == 3 || state == 4);
        }

        internal static string ReadTopProcesses(int maxCount = 3)
        {
            var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Process[] list;
            try { list = Process.GetProcesses(); }
            catch { return "暂时无法读取进程占用"; }
            foreach (Process proc in list)
            {
                try
                {
                    string name = proc.ProcessName;
                    if (string.IsNullOrEmpty(name) || string.Equals(name, "Idle", StringComparison.OrdinalIgnoreCase)) continue;
                    long bytes = proc.WorkingSet64;
                    if (bytes <= 0) continue;
                    if (!names.ContainsKey(name)) names[name] = name;
                    long current;
                    totals.TryGetValue(name, out current);
                    totals[name] = current + bytes;
                    int c;
                    counts.TryGetValue(name, out c);
                    counts[name] = c + 1;
                }
                catch { }
                finally { proc.Dispose(); }
            }
            var top = totals.OrderByDescending(item => item.Value).Take(maxCount).ToList();
            if (top.Count == 0) return "暂无可读取的进程占用";
            var text = new StringBuilder("当前占用内存前三");
            for (int i = 0; i < top.Count; i++)
            {
                string key = top[i].Key;
                int count = counts[key];
                text.Append('\n').Append(i + 1).Append(". ").Append(names[key]).Append(" · ").Append(FormatBytes(top[i].Value));
                if (count > 1) text.Append("（").Append(count).Append(" 个进程）");
            }
            return text.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1.0) return gb.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            return Math.Round(bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileCacheInfo
        {
            internal UIntPtr Current, Peak;
            internal uint Faults;
            internal UIntPtr Minimum, Maximum, Transition, TransitionPeak;
            internal uint Repurpose, Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Privilege { internal uint Count; internal Luid Id; internal uint Attributes; }
        [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
        private static extern int SetList(int informationClass, ref int command, int size);
        [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
        private static extern int SetCache(int informationClass, ref FileCacheInfo info, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string system, string name, out Luid id);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref Privilege state, int length, out Privilege previous, out int written);
        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
        private static extern bool RestorePrivileges(IntPtr token, bool disableAll, ref Privilege state, int length, IntPtr previous, IntPtr written);

        internal static bool IsAdmin()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal static CleanResult Clean(CleanMode mode)
        {
            // Same lock and ACL as the adjacent AgentGC app. Never run at the
            // same time as its system-memory operation.
            if (!IsAdmin()) return new CleanResult("需要管理员权限", false, null, null);
            MutexSecurity acl = new MutexSecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                acl.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
            bool created;
            using (Mutex mutex = new Mutex(false, @"Global\AgentGC-SystemMemory-v1", out created, acl))
            {
                try { if (!mutex.WaitOne(0)) return Busy(); }
                catch (AbandonedMutexException) { mutex.ReleaseMutex(); return Busy(); }
                try
                {
                    foreach (string name in new[] { "memreduct", "memreduct64", "WinMemoryCleaner" })
                    {
                        Process[] peers = Process.GetProcessesByName(name);
                        foreach (Process peer in peers) peer.Dispose();
                        if (peers.Length > 0) return new CleanResult("检测到另一个内存清理器，请退出它后再试", false, null, null);
                    }
                    return Cleaner.Run(mode, Read, Apply);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static CleanResult Busy() { return new CleanResult("系统已有清理操作，本轮已跳过", false, null, null); }

        private static void Apply(Area area)
        {
            if (area != Area.FileCache && area != Area.WorkingSets && area != Area.LowStandby) throw new ArgumentOutOfRangeException("area");
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Privilege request = new Privilege { Count = 1, Attributes = 2 };
                if (!LookupPrivilegeValue(null, area == Area.FileCache ? "SeIncreaseQuotaPrivilege" : "SeProfileSingleProcessPrivilege", out request.Id))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Privilege saved; int written;
                bool adjusted = AdjustTokenPrivileges(token, false, ref request, Marshal.SizeOf(typeof(Privilege)), out saved, out written);
                int error = Marshal.GetLastWin32Error();
                if (!adjusted || error != 0) throw new Win32Exception(error);
                try
                {
                    int status;
                    if (area == Area.FileCache)
                    {
                        UIntPtr maximum = new UIntPtr(ulong.MaxValue);
                        FileCacheInfo info = new FileCacheInfo { Minimum = maximum, Maximum = maximum };
                        status = SetCache(81, ref info, Marshal.SizeOf(typeof(FileCacheInfo)));
                    }
                    else
                    {
                        int command = (int)area;
                        status = SetList(80, ref command, sizeof(int));
                    }
                    if (status < 0) throw new InvalidOperationException(Label(area) + "返回 0x" + status.ToString("X8"));
                }
                finally
                {
                    if (saved.Count != 0)
                    {
                        bool restored = RestorePrivileges(token, false, ref saved, 0, IntPtr.Zero, IntPtr.Zero);
                        int restoreError = Marshal.GetLastWin32Error();
                        if (!restored || restoreError != 0) throw new Win32Exception(restoreError);
                    }
                }
            }
            finally { CloseHandle(token); }
        }

        private static string Label(Area area)
        { return area == Area.FileCache ? "系统文件缓存" : area == Area.WorkingSets ? "应用工作集" : "低优先级待机缓存"; }
    }
}
