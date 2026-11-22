param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\memory-clock'),
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$out = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$upstream = Join-Path (Split-Path $PSScriptRoot -Parent) 'engines\MemReduct'
$sources = @('Schedule.cs','Memory.cs','PageFile.cs','DialArt.cs','StartupEntry.cs','Program.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$options = @('/nologo','/optimize+','/platform:x64','/utf8output','/codepage:65001','/warnaserror+',
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Management.dll',
    "/reference:$framework\System.Xaml.dll",
    "/win32manifest:$PSScriptRoot\app.manifest", "/win32icon:$PSScriptRoot\clock.ico",
    "/resource:$PSScriptRoot\Dashboard.xaml,MemoryClock.Dashboard", "/resource:$PSScriptRoot\Compact.xaml,MemoryClock.Compact")
$options += @('PresentationCore','PresentationFramework','WindowsBase','UIAutomationProvider','UIAutomationTypes') |
    ForEach-Object { "/reference:$wpf\$_.dll" }

& $compiler @options /target:winexe /main:MemoryClock.Program "/out:$out\MemoryClock.exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Memory Clock build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $out 'MemoryClock.exe.config')
$license = Join-Path $upstream 'LICENSE'
Copy-Item -LiteralPath $license,(Join-Path $PSScriptRoot 'README.md') -Destination $out

Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$sourceZip = Join-Path $out 'MemoryClock-source.zip'
$file = [IO.File]::Open($sourceZip, [IO.FileMode]::Create)
try {
    $zip = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in @('Build.ps1','Dashboard.xaml','Compact.xaml','Memory.cs','PageFile.cs','DialArt.cs','StartupEntry.cs','Program.cs','Schedule.cs','Verification.cs','app.config','app.manifest','clock.ico','README.md')) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot $name), "memory-clock/$name")
        }
        foreach ($name in @('LICENSE','source-v.3.5.2.zip')) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $upstream $name), "engines/MemReduct/$name")
        }
    }
    finally { $zip.Dispose() }
}
finally { $file.Dispose() }

Compress-Archive -LiteralPath @(
    (Join-Path $out 'MemoryClock.exe'),
    (Join-Path $out 'MemoryClock.exe.config'),
    (Join-Path $out 'LICENSE'),
    (Join-Path $out 'README.md'),
    $sourceZip
) -DestinationPath (Join-Path $out 'MemoryClock-portable.zip') -Force

if ($Verify) {
    $scratch = (New-Item -ItemType Directory -Force -Path (Join-Path $out 'verification')).FullName
    & $compiler @options /target:exe /main:MemoryClock.Verification "/out:$scratch\VerifyClock.exe" @sources (Join-Path $PSScriptRoot 'Verification.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Verification build failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $scratch 'VerifyClock.exe.config')
    & (Join-Path $scratch 'VerifyClock.exe') $scratch
    if ($LASTEXITCODE -ne 0) { throw 'Memory Clock verification failed.' }
}
Write-Output "Memory Clock: $out\MemoryClock.exe"
