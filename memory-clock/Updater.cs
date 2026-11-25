using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MemoryClock
{
    internal sealed class UpdateInfo
    {
        internal readonly Version Version;
        internal readonly string Tag;
        internal readonly string DownloadUrl;
        internal readonly string ReleaseUrl;
        internal readonly long SizeBytes;
        internal readonly string ExpectedSha256;

        internal UpdateInfo(Version version, string tag, string downloadUrl, string releaseUrl, long sizeBytes, string expectedSha256 = null)
        {
            Version = version;
            Tag = tag;
            DownloadUrl = downloadUrl;
            ReleaseUrl = releaseUrl;
            SizeBytes = sizeBytes;
            ExpectedSha256 = expectedSha256;
        }

        internal string DisplayVersion
        {
            get
            {
                return "v" + Version.Major + "." + Version.Minor + "." + Math.Max(0, Version.Build);
            }
        }
    }

    internal static class Updater
    {
        internal const string LatestApiUrl = "https://api.github.com/repos/M47E1/memory-clock/releases/latest";
        private const string OfficialDownloadPrefix = "/M47E1/memory-clock/releases/download/";
        private const string PortableAssetName = "MemoryClock-portable.zip";
        private const long MaxPackageBytes = 50L * 1024 * 1024;
        private const long MaxExtractedBytes = 100L * 1024 * 1024;
        private const int MaxRedirects = 5;
        private static readonly string[] AllowedHosts =
        {
            "api.github.com",
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com"
        };

        [DataContract]
        private sealed class ReleasePayload
        {
            [DataMember(Name = "tag_name")] internal string TagName { get; set; }
            [DataMember(Name = "html_url")] internal string HtmlUrl { get; set; }
            [DataMember(Name = "draft")] internal bool Draft { get; set; }
            [DataMember(Name = "prerelease")] internal bool Prerelease { get; set; }
            [DataMember(Name = "assets")] internal ReleaseAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class ReleaseAsset
        {
            [DataMember(Name = "name")] internal string Name { get; set; }
            [DataMember(Name = "browser_download_url")] internal string BrowserDownloadUrl { get; set; }
            [DataMember(Name = "size")] internal long Size { get; set; }
            [DataMember(Name = "digest")] internal string Digest { get; set; }
        }

        internal static Version CurrentVersion
        {
            get
            {
                Version raw = Assembly.GetExecutingAssembly().GetName().Version;
                return Normalize(raw ?? new Version(1, 6, 0, 0));
            }
        }

        internal static string CurrentDisplayVersion
        {
            get
            {
                Version v = CurrentVersion;
                return "v" + v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        internal static Version Normalize(Version v)
        {
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        internal static bool IsNewer(Version candidate, Version current)
        {
            return candidate != null && current != null && Normalize(candidate) > Normalize(current);
        }

        internal static Version ParseReleaseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string clean = tag.Trim();
            if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(1);
            int suffix = clean.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0) clean = clean.Substring(0, suffix);
            Version parsed;
            return Version.TryParse(clean, out parsed) ? Normalize(parsed) : null;
        }

        internal static bool IsOfficialAssetUrl(string url)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith(OfficialDownloadPrefix, StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.EndsWith("/" + PortableAssetName, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsAllowedRedirectUri(Uri uri)
        {
            if (uri == null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string host in AllowedHosts)
                if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string ParseSha256Digest(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return null;
            string trimmed = digest.Trim();
            if (!trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            string hex = trimmed.Substring(7).Trim();
            if (hex.Length != 64) return null;
            foreach (char c in hex)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return null;
            }
            return hex.ToLowerInvariant();
        }

        internal static UpdateInfo ParseReleaseJson(string json, Version currentVersion)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            ReleasePayload payload;
            var serializer = new DataContractJsonSerializer(typeof(ReleasePayload));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                payload = (ReleasePayload)serializer.ReadObject(stream);
            if (payload == null || payload.Draft || payload.Prerelease || payload.Assets == null) return null;
            Version remote = ParseReleaseVersion(payload.TagName);
            if (!IsNewer(remote, currentVersion)) return null;
            foreach (ReleaseAsset asset in payload.Assets)
            {
                if (asset == null || !string.Equals(asset.Name, PortableAssetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsOfficialAssetUrl(asset.BrowserDownloadUrl)) continue;
                if (asset.Size <= 0 || asset.Size > MaxPackageBytes) continue;
                return new UpdateInfo(remote, payload.TagName, asset.BrowserDownloadUrl, payload.HtmlUrl ?? "",
                    asset.Size, ParseSha256Digest(asset.Digest));
            }
            return null;
        }

        internal static UpdateInfo CheckLatest(Version currentVersion = null)
        {
            EnsureTls12();
            Version baseline = currentVersion ?? CurrentVersion;
            using (HttpWebResponse response = GetVerifiedResponse(new Uri(LatestApiUrl), "application/vnd.github+json", 10000))
            using (Stream stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return ParseReleaseJson(reader.ReadToEnd(), baseline);
            }
        }

        internal static string ComputeSha256Hex(string filePath)
        {
            using (var sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(filePath))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        internal static string CreateWorkspaceDirectory(bool restrictToAdmin)
        {
            string root = Path.Combine(Path.GetTempPath(), "MemoryClock-upd-" + Guid.NewGuid().ToString("N"));
            if (restrictToAdmin)
            {
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                foreach (WellKnownSidType sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                Directory.CreateDirectory(root, acl);
            }
            else
            {
                Directory.CreateDirectory(root);
            }
            return root;
        }

        internal static void ExtractVerifiedPackage(string zipPath, string stagingDir, Version currentVersion, Version expectedVersion, string expectedSha256 = null)
        {
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                string actual = ComputeSha256Hex(zipPath);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包校验和不一致，已中止安装。");
            }
            Directory.CreateDirectory(stagingDir);
            string fullRoot = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            long extractedBytes = 0;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                int extracted = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative)) throw new InvalidDataException("更新包内包含非法路径。");
                    string destination = Path.GetFullPath(Path.Combine(stagingDir, relative));
                    if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包内包含越界路径。");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (Stream input = entry.Open())
                    using (FileStream output = File.Create(destination))
                    {
                        byte[] buf = new byte[32768];
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            extractedBytes += n;
                            if (extractedBytes > MaxExtractedBytes)
                                throw new InvalidDataException("更新包解压体积超出安全上限。");
                            output.Write(buf, 0, n);
                        }
                    }
                    extracted++;
                }
                if (extracted == 0) throw new InvalidDataException("更新包为空。");
            }
            string stagedExe = Path.Combine(stagingDir, "MemoryClock.exe");
            if (!File.Exists(stagedExe) || new FileInfo(stagedExe).Length < 1024)
                throw new InvalidDataException("更新包缺少有效的 MemoryClock.exe。");
            Version fileVer = ParseReleaseVersion(FileVersionInfo.GetVersionInfo(stagedExe).FileVersion);
            Version asmVer = Normalize(AssemblyName.GetAssemblyName(stagedExe).Version);
            if (!IsNewer(fileVer, currentVersion) || !IsNewer(asmVer, currentVersion))
                throw new InvalidOperationException("更新包内程序版本未高于当前版本，已取消覆盖。");
            if (expectedVersion != null && (fileVer != Normalize(expectedVersion) || asmVer != Normalize(expectedVersion)))
                throw new InvalidOperationException("更新包内程序版本与发布标签不一致，已取消覆盖。");
        }

        internal static void LaunchApplyHelper(string workRoot, string installDir, string stagingDir, int pidToWait, string relaunchArg)
        {
            string scriptPath = Path.Combine(workRoot, "apply.ps1");
            // Paths arrive exclusively via environment variables (no string interpolation or quote/encoding issues).
            string script = string.Join(Environment.NewLine, new[]
            {
                "$ErrorActionPreference = 'Stop'",
                "$install = $env:MC_INSTALL",
                "$stage = $env:MC_STAGE",
                "$work = $env:MC_WORK",
                "$waitPid = [int]$env:MC_WAIT_PID",
                "$relaunchArg = $env:MC_RELAUNCH_ARG",
                "$exe = Join-Path $install 'MemoryClock.exe'",
                "$backup = Join-Path $work 'backup'",
                "$exited = $false",
                "for ($i = 0; $i -lt 80; $i++) {",
                "    if (-not (Get-Process -Id $waitPid -ErrorAction SilentlyContinue)) { $exited = $true; break }",
                "    Start-Sleep -Milliseconds 250",
                "}",
                "if (-not $exited) {",
                "    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue",
                "    exit 1",
                "}",
                "New-Item -ItemType Directory -Path $backup -Force | Out-Null",
                "$stagedFiles = Get-ChildItem -LiteralPath $stage -File",
                "foreach ($f in $stagedFiles) {",
                "    $existing = Join-Path $install $f.Name",
                "    if (Test-Path -LiteralPath $existing) { Copy-Item -LiteralPath $existing -Destination (Join-Path $backup $f.Name) -Force }",
                "}",
                "$copied = $false",
                "for ($try = 0; $try -lt 12; $try++) {",
                "    try {",
                "        foreach ($f in $stagedFiles) {",
                "            Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $install $f.Name) -Force",
                "        }",
                "        $copied = $true; break",
                "    } catch { Start-Sleep -Milliseconds 350 }",
                "}",
                "if (-not $copied) {",
                "    foreach ($b in (Get-ChildItem -LiteralPath $backup -File -ErrorAction SilentlyContinue)) {",
                "        Copy-Item -LiteralPath $b.FullName -Destination (Join-Path $install $b.Name) -Force -ErrorAction SilentlyContinue",
                "    }",
                "}",
                "if (Test-Path -LiteralPath $exe) {",
                "    if ([string]::IsNullOrEmpty($relaunchArg)) { Start-Process -FilePath $exe -WorkingDirectory $install }",
                "    else { Start-Process -FilePath $exe -ArgumentList $relaunchArg -WorkingDirectory $install }",
                "}",
                "Start-Sleep -Milliseconds 300",
                "Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue"
            });
            // UTF-8 with BOM so Windows PowerShell 5.1 always decodes reliably on any locale.
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var start = new ProcessStartInfo(powershell,
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.EnvironmentVariables["MC_INSTALL"] = installDir;
            start.EnvironmentVariables["MC_STAGE"] = stagingDir;
            start.EnvironmentVariables["MC_WORK"] = workRoot;
            start.EnvironmentVariables["MC_WAIT_PID"] = pidToWait.ToString(CultureInfo.InvariantCulture);
            start.EnvironmentVariables["MC_RELAUNCH_ARG"] = relaunchArg ?? "";
            using (Process helper = Process.Start(start))
            {
                if (helper == null) throw new InvalidOperationException("无法启动更新安装程序。");
            }
        }

        internal static void ApplyOnlineUpdate(UpdateInfo info, string installDir, int currentPid, string relaunchArg)
        {
            if (info == null || !IsOfficialAssetUrl(info.DownloadUrl))
                throw new InvalidOperationException("更新包地址不受信任。");
            VerifyWritableDirectory(installDir);
            EnsureTls12();
            string workRoot = CreateWorkspaceDirectory(WindowsMemory.IsAdmin());
            string zipPath = Path.Combine(workRoot, PortableAssetName);
            string stagingDir = Path.Combine(workRoot, "stage");
            try
            {
                DownloadFileVerified(new Uri(info.DownloadUrl), zipPath);
                ExtractVerifiedPackage(zipPath, stagingDir, CurrentVersion, info.Version, info.ExpectedSha256);
                LaunchApplyHelper(workRoot, installDir, stagingDir, currentPid, relaunchArg);
            }
            catch
            {
                TryDeleteDirectory(workRoot);
                throw;
            }
        }

        private static HttpWebResponse GetVerifiedResponse(Uri startUri, string accept, int timeoutMs)
        {
            Uri current = startUri;
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!IsAllowedRedirectUri(current))
                    throw new InvalidOperationException("更新地址跳转到不受信任的域名，已中止。");
                var request = (HttpWebRequest)WebRequest.Create(current);
                request.Method = "GET";
                request.AllowAutoRedirect = false;
                request.UserAgent = "MemoryClock/" + CurrentDisplayVersion;
                if (!string.IsNullOrEmpty(accept)) request.Accept = accept;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                var response = (HttpWebResponse)request.GetResponse();
                int code = (int)response.StatusCode;
                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                {
                    string location = response.Headers["Location"];
                    response.Dispose();
                    Uri next;
                    if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(current, location, out next))
                        throw new InvalidOperationException("更新跳转地址无效。");
                    current = next;
                    continue;
                }
                if (code < 200 || code >= 300)
                {
                    response.Dispose();
                    throw new InvalidOperationException("更新服务返回状态 " + code + "。");
                }
                return response;
            }
            throw new InvalidOperationException("更新重定向次数过多。");
        }

        private static void DownloadFileVerified(Uri uri, string destination)
        {
            var deadline = Stopwatch.StartNew();
            using (HttpWebResponse response = GetVerifiedResponse(uri, null, 20000))
            using (Stream input = response.GetResponseStream())
            using (FileStream output = File.Create(destination))
            {
                byte[] buffer = new byte[32768];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (deadline.ElapsedMilliseconds > 90000) throw new TimeoutException("更新包下载超时。");
                    total += read;
                    if (total > MaxPackageBytes) throw new InvalidDataException("更新包体积异常，已中止下载。");
                    output.Write(buffer, 0, read);
                }
            }
        }

        private static void EnsureTls12()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private static void VerifyWritableDirectory(string dir)
        {
            string probe = Path.Combine(dir, ".mc-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("当前目录没有写入权限，请把便携版移到用户目录后重试。");
            }
        }

        private static void TryDeleteDirectory(string path)
        { try { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    }
}
