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
$runtimeRoot = Join-Path $PSScriptRoot 'artifacts/package-runtime'
# Reserve fixed paths before extracting anything. A concurrent or interrupted run must be inspected,
# never silently overwritten; random executable paths leave permanent Windows tray history entries.
if (Test-Path -LiteralPath $runtimeRoot) { throw "Package runtime is already in use or needs cleanup: $runtimeRoot" }
New-Item -ItemType Directory -Path $runtimeRoot -ErrorAction Stop | Out-Null
$installations = @('original 中文', 'upgrade space') | ForEach-Object { Join-Path $runtimeRoot $_ }
$executables = @()
$probe = $null
try {
    foreach ($installation in $installations) {
        Expand-Archive -LiteralPath $zip -DestinationPath $installation
        $manifests = @(Get-ChildItem -LiteralPath $installation -Filter package-manifest.json -Recurse)
        if ($manifests.Count -ne 1) { throw 'Expected exactly one package manifest.' }
        $extracted = [IO.Path]::GetFullPath($manifests[0].DirectoryName)
        $package = [IO.Path]::GetFullPath((Join-Path $installation 'app'))
        $validatedInstallation = (Resolve-Path -LiteralPath $installation).Path
        if ([IO.Path]::GetDirectoryName($extracted) -ne $validatedInstallation -or
            [IO.Path]::GetDirectoryName($package) -ne $validatedInstallation) { throw 'Package move escapes its reserved installation directory.' }
        if (@(Get-ChildItem -LiteralPath $installation -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw 'Refusing to move a package containing reparse points.'
        }
        if ($extracted -ne $package) { Move-Item -LiteralPath $extracted -Destination $package }
        $manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
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
    Write-Output "Package evidence: $root"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (!$probe.WaitForExit(1000)) {
        if ($clock.Elapsed.TotalMinutes -gt 3) { throw 'Published package probe exceeded three minutes.' }
    }
    $report = Get-Content -LiteralPath (Join-Path $root 'package-report.json') -Raw | ConvertFrom-Json
    if ($probe.ExitCode -ne 0 -or !$report.Passed) { throw "Published package probe failed: $($report.Error)" }

    # Preserve evidence/profile, but clean this run's fixed executable paths even after a failed probe.
    $profile = Join-Path $root 'profile 中文'
    $stateHash = (Get-FileHash -LiteralPath (Join-Path $profile 'state.json') -Algorithm SHA256).Hash
    $backupHash = (Get-FileHash -LiteralPath (Join-Path $profile 'state.json.bak') -Algorithm SHA256).Hash
} finally {
    if ($null -ne $probe) {
        if (!$probe.HasExited) { $probe.Kill($true); $probe.WaitForExit(10000) | Out-Null }
        $probe.Dispose()
    }
    $target = (Resolve-Path -LiteralPath $runtimeRoot).Path
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/package-runtime'))
    if ($target -ne $expected -or $target -eq [IO.Path]::GetFullPath($root)) { throw 'Removal target is outside the reserved package runtime.' }
    $entries = @(Get-Item -LiteralPath $target -Force) + @(Get-ChildItem -LiteralPath $target -Recurse -Force)
    $reparsePoints = @($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
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
