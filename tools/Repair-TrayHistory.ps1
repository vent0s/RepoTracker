#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KeepExecutable,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TrayHistory.psm1') -Force
if ([Environment]::OSVersion.Version.Build -ne 19045) { throw 'This repair supports only the inspected Windows 10 22H2 layout (build 19045).' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$keepPath = (Resolve-Path -LiteralPath $KeepExecutable).Path
if ([IO.Path]::GetFileName($keepPath) -ine 'RepoTracker.Desktop.exe') { throw 'Retain a real RepoTracker.Desktop.exe.' }
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
# Never terminate programs or restart Explorer implicitly. The operator must explicitly authorize
# that separate action after reviewing the preview; Explorer otherwise overwrites this cache.
if ($Apply -and @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object SessionId -EQ $session).Count) {
    throw 'Explorer is running. Preview first, then explicitly approve its restart before applying this repair.'
}
$running = @(Get-Process -Name RepoTracker.Desktop,RepoTracker.Desktop.TestHost -ErrorAction SilentlyContinue)
$keep = @($keepPath)
foreach ($process in $running) {
    if (!$process.Path) { throw 'Cannot inspect a running RepoTracker process; refusing to remove its history.' }
    $keep += $process.Path
}
$keep = @($keep | Sort-Object -Unique)
$keyPath = 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify'
$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath, [bool]$Apply)
if ($null -eq $key) { throw 'Current user tray history is unavailable.' }
try {
    $original = $key.GetValue('IconStreams')
    if ($original -isnot [byte[]]) { throw 'IconStreams is not binary.' }
    $plan = New-RepoTrackerTrayCleanup -Bytes $original -ProjectRoot $projectRoot -KeepExecutables $keep
    $report = [ordered]@{
        Mode = if ($Apply) { 'Apply' } else { 'Preview' }; Applied = $false
        RegistryKey = 'HKEY_CURRENT_USER\' + $keyPath; Value = 'IconStreams'
        KeepExecutables = $keep; BeforeCount = $plan.BeforeCount; AfterCount = $plan.AfterCount
        Remove = @($plan.Removed | ForEach-Object Path)
        BeforeSha256 = $plan.BeforeSha256; AfterSha256 = $plan.AfterSha256
        Note = 'Only selected project-owned RepoTracker records are removed; all other records are copied byte-for-byte. Other registry values are not written. A running Explorer may have additional unsaved history.'
    }
    $directory = Join-Path $projectRoot ('.artifacts/tray-history/{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $directory | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $directory 'IconStreams.before.bin'), $original)
    [IO.File]::WriteAllBytes((Join-Path $directory 'IconStreams.proposed.bin'), $plan.Bytes)
    if ($Apply -and $plan.Removed.Count -gt 0) {
        # Recheck immediately before mutation: neither Explorer nor another writer may race the preview.
        if (@(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object SessionId -EQ $session).Count) { throw 'Explorer restarted before the write; nothing was changed.' }
        $currentHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$key.GetValue('IconStreams')))
        if ($currentHash -ne $plan.BeforeSha256) { throw 'Tray history changed since it was read; nothing was changed.' }
        $key.SetValue('IconStreams', $plan.Bytes, [Microsoft.Win32.RegistryValueKind]::Binary)
        $key.Flush()
        $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$key.GetValue('IconStreams')))
        if ($actualHash -ne $plan.AfterSha256) { throw "Read-back mismatch; retain backup at $directory and investigate before further changes." }
        $report.Applied = $true
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'report.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 5
    Write-Output "Evidence: $directory"
} finally { $key.Dispose() }
