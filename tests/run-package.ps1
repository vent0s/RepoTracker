#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageZip)

$ErrorActionPreference = 'Stop'
$zip = (Resolve-Path -LiteralPath $PackageZip).Path
$expectedHash = (Get-Content -LiteralPath "$zip.sha256" -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expectedHash) { throw 'ZIP checksum mismatch.' }
$hostExe = Join-Path $PSScriptRoot 'RepoTracker.Desktop.TestHost/bin/Release/net10.0-windows/RepoTracker.Desktop.TestHost.exe'
if (!(Test-Path -LiteralPath $hostExe)) { throw 'Build the desktop TestHost in Release first.' }
$root = Join-Path $PSScriptRoot ('artifacts/package-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $root | Out-Null
$installations = @('original 中文', 'upgrade space') | ForEach-Object { Join-Path $root $_ }
$executables = @()
foreach ($installation in $installations) {
    Expand-Archive -LiteralPath $zip -DestinationPath $installation
    $manifests = @(Get-ChildItem -LiteralPath $installation -Filter package-manifest.json -Recurse)
    if ($manifests.Count -ne 1) { throw 'Expected exactly one package manifest.' }
    $package = $manifests[0].DirectoryName
    $manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $package $entry.Path))
        if (!$path.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes package.' }
        if ((Get-Item -LiteralPath $path).Length -ne $entry.Bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Sha256) {
            throw "Package checksum mismatch: $($entry.Path)"
        }
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $package -File -Recurse).Count
    if ($actualFiles -ne $manifest.Files.Count + 1) { throw 'Unexpected files in package.' }
    $executables += Join-Path $package 'RepoTracker.Desktop.exe'
}

$start = [Diagnostics.ProcessStartInfo]::new($hostExe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @('--package-probe', $executables[0], $executables[1], $root)) { $start.ArgumentList.Add($argument) }
foreach ($key in @($start.Environment.Keys | Where-Object { $_ -like 'REPOTRACKER_*' })) { $start.Environment.Remove($key) | Out-Null }
$probe = [Diagnostics.Process]::Start($start)
try {
    Write-Output "Package evidence: $root"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (!$probe.WaitForExit(1000)) {
        if ($clock.Elapsed.TotalMinutes -gt 3) { throw 'Published package probe exceeded three minutes.' }
    }
    $report = Get-Content -LiteralPath (Join-Path $root 'package-report.json') -Raw | ConvertFrom-Json
    if ($probe.ExitCode -ne 0 -or !$report.Passed) { throw "Published package probe failed: $($report.Error)" }
} finally {
    if (!$probe.HasExited) { $probe.Kill($true); $probe.WaitForExit(10000) | Out-Null }
    $probe.Dispose()
}

# Portable removal rehearsal: delete only this run's extracted application directories.
$profile = Join-Path $root 'profile 中文'
$stateHash = (Get-FileHash -LiteralPath (Join-Path $profile 'state.json') -Algorithm SHA256).Hash
$backupHash = (Get-FileHash -LiteralPath (Join-Path $profile 'state.json.bak') -Algorithm SHA256).Hash
$validatedRoot = (Resolve-Path -LiteralPath $root).Path
foreach ($installation in $installations) {
    $target = (Resolve-Path -LiteralPath $installation).Path
    if ([IO.Path]::GetDirectoryName($target) -ne $validatedRoot -or $target -eq $profile) { throw 'Removal target is outside this test run.' }
    $reparsePoints = @(Get-ChildItem -LiteralPath $target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($reparsePoints.Count) { throw 'Refusing recursive removal through reparse points.' }
    Remove-Item -LiteralPath $target -Recurse -Force
}
if ((Get-FileHash -LiteralPath (Join-Path $profile 'state.json') -Algorithm SHA256).Hash -ne $stateHash -or
    (Get-FileHash -LiteralPath (Join-Path $profile 'state.json.bak') -Algorithm SHA256).Hash -ne $backupHash) { throw 'Portable removal changed the external profile.' }
@{ Passed = $true; Package = $zip; Sha256 = $expectedHash; Version = $manifest.Version; Sdk = $manifest.Sdk;
    Runtime = $manifest.Runtime; CheckedPackageFiles = $manifest.Files.Count; RemovedTestInstallations = $installations;
    PreservedProfileSha256 = $stateHash; PreservedBackupSha256 = $backupHash;
    Note = 'Portable folder removal only; startup registration uses separate isolated adapter tests, not a real Windows login.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'removal-report.json') -Encoding utf8
$report.Checks | Write-Output
Write-Output 'PASS: package checksums, real apphost lifecycle/languages/runtime modules, relocation and portable removal with profile preservation.'
