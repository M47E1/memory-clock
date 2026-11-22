using System;
using System.IO;
using Microsoft.Win32;

namespace MemoryClock
{
    internal enum StartupState { Off, Registered, Conflict }

    internal sealed class StartupEntry
    {
        internal const string TrayOption = "--tray";
        private const string Name = "MemoryClock";
        private readonly string key, command;
        internal StartupEntry(string executable, string key = @"Software\Microsoft\Windows\CurrentVersion\Run")
        {
            this.key = key;
            command = "\"" + Path.GetFullPath(executable) + "\" " + TrayOption;
        }

        private StartupState Read(RegistryKey registry)
        {
            object value = registry == null ? null : registry.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return StartupState.Off;
            return string.Equals(value as string, command, StringComparison.OrdinalIgnoreCase) ? StartupState.Registered : StartupState.Conflict;
        }
        internal StartupState Read()
        { using (var registry = Registry.CurrentUser.OpenSubKey(key, false)) return Read(registry); }

        internal void Set(bool enable)
        {
            if (enable && command.Length > 260) throw new IOException("程序路径太长，请把便携版移到较短的固定路径。");
            using (var registry = enable ? Registry.CurrentUser.CreateSubKey(key) : Registry.CurrentUser.OpenSubKey(key, true))
            {
                StartupState current = Read(registry);
                if (current == StartupState.Conflict) throw new IOException("已有其他版本的同名启动项，请先从原版本关闭。");
                if (enable && current == StartupState.Off) registry.SetValue(Name, command, RegistryValueKind.String);
                else if (!enable && current == StartupState.Registered) registry.DeleteValue(Name);
            }
        }
    }
}
