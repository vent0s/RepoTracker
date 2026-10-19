#Requires -Version 7.0
[CmdletBinding()]
param([string]$CompilerPath = (Join-Path $PSScriptRoot '../.local/installer-tools/inno-7.1.0/ISCC.exe'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/installer-fixture'))
$appId = 'Septim.RepoTracker.InstallerTests'
$uninstallKey = "HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/${appId}_is1"
$appPathKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/App Paths/RepoTracker.InstallerTests.exe'
$target = Join-Path $root 'installed 中文'
$runKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run'
$ownRunName = 'RepoTracker.InstallerTests.Owned'
$foreignRunName = 'RepoTracker.InstallerTests.Foreign'
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $appPathKey)) { throw 'Installer fixture already exists. Inspect before retrying.' }
if ((Get-ItemProperty -LiteralPath $runKey).PSObject.Properties.Name -contains $ownRunName -or
    (Get-ItemProperty -LiteralPath $runKey).PSObject.Properties.Name -contains $foreignRunName) { throw 'Fixture startup values already exist.' }
New-Item -ItemType Directory -Path $root | Out-Null
$profile = Join-Path $root 'external profile'
New-Item -ItemType Directory -Path $profile | Out-Null
$state = Join-Path $profile 'state.json'
'{"repositories":["keep"],"language":"zh-Hans"}' | Set-Content -LiteralPath $state
$stateHash = (Get-FileHash -LiteralPath $state).Hash
$checks = [Collections.Generic.List[string]]::new()

function Invoke-Setup([string]$File, [string[]]$Extra, [string]$Log, [switch]$ExpectFailure) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    foreach ($arg in @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART','/RESTARTEXITCODE=3010',"/LOG=$Log") + $Extra) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(90000)) {
            $process.Kill($true); $process.WaitForExit(10000) | Out-Null
            throw 'Fixture installer exceeded 90 seconds; owned process tree stopped before cleanup.'
        }
        if ($ExpectFailure) { if ($process.ExitCode -eq 0) { throw 'Expected the installer to reject this operation.' } }
        elseif ($process.ExitCode -ne 0) { throw "Installer returned $($process.ExitCode); see $Log" }
        # Inno's uninstaller can finish its launcher before the temporary child closes the log.
        $logClosed = $false
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $stream = [IO.File]::Open($Log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                $stream.Dispose(); $logClosed = $true; break
            } catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
        }
        if (!$logClosed) { throw 'Installer child still owns its log; retaining fixture for inspection.' }
    } finally { $process.Dispose() }
}

try {
    $setups = @{}
    foreach ($version in @('0.4.0','0.5.0')) {
        $payload = Join-Path $root "payload-$version"
        New-Item -ItemType Directory -Path $payload | Out-Null
        # Build a tiny valid PE using the system framework compiler; never launch it or register a tray icon.
        $source = Join-Path $root "fixture-$version.cs"
        ('[assembly:System.Reflection.AssemblyFileVersion("' + $version + '.0")] class Fixture { static void Main() {} }') | Set-Content -LiteralPath $source
        & "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:winexe "/out:$(Join-Path $payload 'RepoTracker.Desktop.exe')" $source
        if ($LASTEXITCODE -ne 0) { throw 'Fixture PE compilation failed.' }
        if ($version -eq '0.4.0') {
            'old runtime' | Set-Content -LiteralPath (Join-Path $payload 'obsolete.dll')
            'original' | Set-Content -LiteralPath (Join-Path $payload 'modified.txt')
        }
        $list = Join-Path $root "files-$version.sha256"
        Get-ChildItem -LiteralPath $payload -File | ForEach-Object { '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash,$_.Name } | Set-Content -LiteralPath $list -Encoding utf8BOM
        & $CompilerPath /Qp "/DPayloadDir=$payload" "/DReleaseVersion=$version" "/DReleaseFileVersion=$version.0" "/DOutputDir=$root" "/DFileList=$list" "/DProductId=$appId" '/DProductName=RepoTracker Installer Tests' '/DAppPathName=RepoTracker.InstallerTests.exe' '/DRunningMutex=Local\Septim.RepoTracker.InstallerTests' (Join-Path $PSScriptRoot '../installer/RepoTracker.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Fixture installer compilation failed.' }
        $setups[$version] = Join-Path $root "RepoTracker-$version-win-x64-Setup.exe"
    }
    $occupied = Join-Path $root 'occupied'
    New-Item -ItemType Directory -Path $occupied | Out-Null
    'leave this alone' | Set-Content -LiteralPath (Join-Path $occupied 'personal.txt')
    Invoke-Setup $setups['0.4.0'] @("/DIR=$occupied") (Join-Path $root 'reject-nonempty.log') -ExpectFailure
    if ((Get-Content -LiteralPath (Join-Path $occupied 'personal.txt') -Raw).Trim() -ne 'leave this alone') { throw 'First install changed an occupied destination.' }
    Invoke-Setup $setups['0.4.0'] @("/DIR=$target") (Join-Path $root 'install.log')
    if ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -ne '0.4.0') { throw 'First install did not register its version.' }
    if ((Get-Item -LiteralPath $appPathKey).GetValue('') -ne (Join-Path $target 'RepoTracker.Desktop.exe')) { throw 'App Paths registration is incorrect.' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'RepoTracker Installer Tests.lnk'
    if (!(Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut missing.' }
    $checks.Add('Fresh install: version, App Paths, Chinese destination and Start menu registration.')
    'user data' | Set-Content -LiteralPath (Join-Path $target 'personal.txt')
    'user changed this' | Set-Content -LiteralPath (Join-Path $target 'modified.txt')
    $ownCommand = '"' + (Join-Path $target 'RepoTracker.Desktop.exe') + '" --profile "' + $profile + '" --minimized'
    $foreignCommand = '"E:\Different App\RepoTracker.Desktop.exe" --profile "E:\Other Profile" --minimized'
    New-ItemProperty -LiteralPath $runKey -Name $ownRunName -Value $ownCommand | Out-Null
    New-ItemProperty -LiteralPath $runKey -Name $foreignRunName -Value $foreignCommand | Out-Null
    Invoke-Setup $setups['0.5.0'] @("/DIR=$(Join-Path $root 'wrong path')") (Join-Path $root 'reject-path.log') -ExpectFailure
    Invoke-Setup $setups['0.5.0'] @() (Join-Path $root 'upgrade.log')
    if ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -ne '0.5.0' -or (Test-Path -LiteralPath (Join-Path $target 'obsolete.dll'))) { throw 'Upgrade or obsolete file removal failed.' }
    if ((Get-Content -LiteralPath (Join-Path $target 'modified.txt') -Raw).Trim() -ne 'user changed this') { throw 'Upgrade deleted a modified file.' }
    if ((Get-ItemProperty -LiteralPath $runKey).$ownRunName -ne $ownCommand) { throw 'Upgrade changed the existing startup preference.' }
    $checks.Add('Upgrade reuses registered location, replaces files, removes obsolete unchanged files, preserves modified files and startup preference.')
    Invoke-Setup $setups['0.4.0'] @() (Join-Path $root 'reject-downgrade.log') -ExpectFailure
    if ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -ne '0.5.0') { throw 'Downgrade changed registration.' }
    $checks.Add('Relocation and downgrade are rejected without changing the installed version.')
    $running = [Threading.Mutex]::new($false, 'Local\Septim.RepoTracker.InstallerTests')
    try { Invoke-Setup (Join-Path $target 'unins000.exe') @() (Join-Path $root 'reject-running-uninstall.log') -ExpectFailure }
    finally { $running.Dispose() }
    if (!(Test-Path -LiteralPath $uninstallKey)) { throw 'Running uninstall removed registration.' }
    Invoke-Setup (Join-Path $target 'unins000.exe') @() (Join-Path $root 'uninstall.log')
    if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $appPathKey) -or (Test-Path -LiteralPath $shortcut)) { throw 'Uninstall left application registration.' }
    if ((Get-ItemProperty -LiteralPath $runKey).PSObject.Properties.Name -contains $ownRunName) { throw 'Uninstall left its own login entry.' }
    if ((Get-ItemProperty -LiteralPath $runKey).$foreignRunName -ne $foreignCommand) { throw 'Uninstall changed a foreign login entry.' }
    if (!(Test-Path -LiteralPath (Join-Path $target 'personal.txt')) -or (Get-FileHash -LiteralPath $state).Hash -ne $stateHash) { throw 'Uninstall changed user data.' }
    $checks.Add('Uninstall removes registration/shortcut/owned startup only and preserves external profile and unlisted files.')
    $checks | Write-Output
    @{ Passed = $true; Checks = $checks.ToArray(); ProfileHash = $stateHash } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'report.json')
} finally {
    # Only the fixed fixture identity and its own directory are eligible for cleanup.
    if (Test-Path -LiteralPath $uninstallKey) {
        $registered = (Get-ItemProperty -LiteralPath $uninstallKey).'Inno Setup: App Path'
        if ([IO.Path]::GetFullPath($registered).TrimEnd('\') -ne $target) { throw 'Unexpected fixture install path; refusing cleanup.' }
        Invoke-Setup (Join-Path $target 'unins000.exe') @() (Join-Path $root 'cleanup-uninstall.log')
    }
    foreach ($name in @($ownRunName,$foreignRunName)) { Remove-ItemProperty -LiteralPath $runKey -Name $name -ErrorAction SilentlyContinue }
}
$evidence = Join-Path $PSScriptRoot ('artifacts/installer-{0}-{1}' -f (Get-Date -Format yyyyMMdd-HHmmss), [guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $evidence | Out-Null
Get-ChildItem -LiteralPath $root -File | Where-Object Extension -In @('.log','.json') | Copy-Item -Destination $evidence
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/installer-fixture'))
if ((Resolve-Path -LiteralPath $root).Path -ne $expected -or
    @(@(Get-Item -LiteralPath $root -Force) + @(Get-ChildItem -LiteralPath $root -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Unsafe fixture cleanup path.' }
Remove-Item -LiteralPath $root -Recurse -Force
Write-Output "PASS: installer fixture removed. Evidence: $evidence"
