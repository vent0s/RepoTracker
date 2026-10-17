[CmdletBinding()]
param([switch]$Smoke, [switch]$CheckBudgets)

$ErrorActionPreference = 'Stop'
if ($Smoke -and $CheckBudgets) { throw 'Smoke only checks orchestration and cannot validate performance budgets.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $PSScriptRoot 'RepoTracker.Desktop.TestHost/bin/Release/net10.0-windows/RepoTracker.Desktop.TestHost.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Build RepoTracker.slnx in Release before running this benchmark.' }
$mode = if ($Smoke) { 'smoke' } elseif ($CheckBudgets) { 'verify' } else { 'baseline' }
$outputDirectory = Join-Path $PSScriptRoot ('artifacts/performance-{0}-{1}-{2}' -f $mode, (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$processor = Get-CimInstance Win32_Processor
$system = Get-CimInstance Win32_ComputerSystem
$operatingSystem = Get-CimInstance Win32_OperatingSystem
@{
    Processor = @($processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    RamGiB = $system.TotalPhysicalMemory / 1GB
    OperatingSystem = $operatingSystem.Caption
    Build = $operatingSystem.BuildNumber
    GitCommit = (& git -C $projectRoot rev-parse HEAD)
    Binaries = @(Get-ChildItem -LiteralPath (Split-Path -Parent $executable) -Filter 'RepoTracker*.dll' |
        ForEach-Object { @{ Name = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    Note = 'Working-tree changes may be present; benchmark the built Release binaries. Fixtures are retained beneath tests/artifacts.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputDirectory 'environment.json') -Encoding utf8

function Start-FixtureProcess([string[]]$ProcessArguments, [string]$PerformanceMode) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $projectRoot
    foreach ($argument in $ProcessArguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('REPOTRACKER_PERFORMANCE') | Out-Null
    if ($PerformanceMode) { $start.Environment['REPOTRACKER_PERFORMANCE'] = $PerformanceMode }
    return [Diagnostics.Process]::Start($start)
}

$writer = $null
$benchmark = $null
try {
    Write-Output "Performance evidence: $outputDirectory"
    $writer = Start-FixtureProcess -ProcessArguments @('--performance-writer', $outputDirectory) -PerformanceMode ''
    $benchmark = Start-FixtureProcess -ProcessArguments @('--profile', (Join-Path $outputDirectory 'profile'), '--minimized') -PerformanceMode $mode
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastPhase = ''
    while (!$benchmark.WaitForExit(1000)) {
        if ($writer.HasExited) { throw "Load writer exited early: $($writer.ExitCode)" }
        if ($clock.Elapsed.TotalMinutes -gt 20) { throw 'Performance run exceeded 20 minutes.' }
        $phasePath = Join-Path $outputDirectory 'phase.txt'
        if (Test-Path -LiteralPath $phasePath) {
            $phaseStream = [IO.FileStream]::new($phasePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $phaseReader = [IO.StreamReader]::new($phaseStream)
            try { $phase = $phaseReader.ReadToEnd() }
            finally { $phaseReader.Dispose() }
            if ($phase -ne $lastPhase) { Write-Output "Phase: $phase"; $lastPhase = $phase }
        }
    }
    'stop' | Set-Content -LiteralPath (Join-Path $outputDirectory 'writer.stop')
    if (!$writer.WaitForExit(10000)) { throw 'Load writer failed to stop.' }
    $failure = Join-Path $outputDirectory 'failure.txt'
    if (Test-Path -LiteralPath $failure) { throw (Get-Content -LiteralPath $failure -Raw) }
    if ($benchmark.ExitCode -ne 0 -or $writer.ExitCode -ne 0) { throw "Process exit codes: benchmark=$($benchmark.ExitCode), writer=$($writer.ExitCode)" }
    $report = Get-Content -LiteralPath (Join-Path $outputDirectory 'report.json') -Raw | ConvertFrom-Json
    if (!$report.Passed) { throw 'Performance scenario checks failed; inspect report.json.' }
    $writerReport = Get-Content -LiteralPath (Join-Path $outputDirectory 'writer.json') -Raw | ConvertFrom-Json
    if ($writerReport.Writes -lt 20) { throw 'Churn phase did not produce file writes.' }
    $report.Phases | Select-Object Phase, Seconds, TotalCpuCorePercent, AppWorkingSetPeakMiB, DescendantsCreated, LocalChecks, RemoteChecks, DispatcherP95Ms | Format-Table -AutoSize
    Write-Output "Completed ($mode). Load writes: $($writerReport.Writes). Remaining descendants: $($report.RemainingDescendants)."
    if ($CheckBudgets) { & (Join-Path $PSScriptRoot 'check-performance.ps1') -ReportDirectory $outputDirectory }
}
finally {
    'stop' | Set-Content -LiteralPath (Join-Path $outputDirectory 'writer.stop')
    foreach ($child in @($benchmark, $writer)) {
        if ($null -ne $child) {
            if (!$child.HasExited -and !$child.WaitForExit(5000)) { $child.Kill($true); $child.WaitForExit() }
            $child.Dispose()
        }
    }
}
