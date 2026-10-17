[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$report = Get-Content -LiteralPath (Join-Path $ReportDirectory 'report.json') -Raw | ConvertFrom-Json
$samples = Get-Content -LiteralPath (Join-Path $ReportDirectory 'samples.json') -Raw | ConvertFrom-Json
$writer = Get-Content -LiteralPath (Join-Path $ReportDirectory 'writer.json') -Raw | ConvertFrom-Json
$fixture = Get-Content -LiteralPath (Join-Path $ReportDirectory 'fixture.json') -Raw | ConvertFrom-Json
$checks = @(Get-Content -LiteralPath (Join-Path $ReportDirectory 'checks.json') -Raw | ConvertFrom-Json)
$budgets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-budgets.json') -Raw | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()
function Require-Budget([bool]$Condition, [string]$Message) {
    if (!$Condition) { $failures.Add($Message) }
}
Require-Budget (!$report.Smoke -and $report.Passed) 'A complete successful measurement is required; smoke is not acceptance.'
Require-Budget ($fixture.Repositories -eq 20 -and $fixture.WorktreeFiles -eq 5020 -and $fixture.WorktreeBytes -eq 17408780) 'Fixture size differs from the calibrated dataset.'
Require-Budget ($report.CompletedAndWatchingRepositories -eq 20) 'All 20 repository watchers and checks must be active.'
Require-Budget ($report.RemainingDescendants -eq 0) 'Descendant processes remain after shutdown.'
Require-Budget ($report.CleanupMs -le $budgets.CleanupMaxMs) 'Scheduler shutdown exceeded the time budget.'
Require-Budget ($report.OpenToRenderMs -le $budgets.OpenToRenderMaxMs) 'Window restore to Rendering event exceeded the time budget.'
Require-Budget ($writer.Writes -ge $budgets.ChurnWritesMin) 'Churn workload was too small.'
$phases = @{}
foreach ($phase in $report.Phases) { $phases[$phase.Phase] = $phase }
Require-Budget ($phases.ContainsKey('warmup') -and $phases['warmup'].Seconds -ge 30) 'Warmup must complete before measurement.'
foreach ($definition in $budgets.Phases) {
    if (!$phases.ContainsKey($definition.Name)) { throw "Missing phase: $($definition.Name)" }
    $phase = $phases[$definition.Name]
    Require-Budget ($phase.Seconds -ge $definition.SecondsMin) "$($phase.Phase): duration too short."
    Require-Budget ($phase.TotalCpuCorePercent -le $definition.TotalCpuCorePercentMax) "$($phase.Phase): CPU exceeded budget."
    Require-Budget ($phase.AppWorkingSetPeakMiB -le $budgets.AppWorkingSetMaxMiB) "$($phase.Phase): working set exceeded budget."
    Require-Budget ($phase.AppPrivatePeakMiB -le $budgets.AppPrivateMaxMiB) "$($phase.Phase): private bytes exceeded budget."
    Require-Budget ($phase.DispatcherP95Ms -le $budgets.DispatcherP95MaxMs -and $phase.DispatcherMaxMs -le $budgets.DispatcherMaxMs) "$($phase.Phase): dispatcher latency exceeded budget."
    Require-Budget ($phase.LocalChecks -ge $definition.LocalChecksMin -and $phase.LocalErrorCount -eq 0) "$($phase.Phase): insufficient local checks or local errors."
    Require-Budget ($phase.LocalConcurrencyPeak -le 2 -and $phase.RemoteConcurrencyPeak -le 1) "$($phase.Phase): concurrency exceeded configured limit."
    Require-Budget ($phase.LocalCheckP95Ms -le $budgets.LocalCheckP95MaxMs) "$($phase.Phase): local check latency exceeded budget."
    Require-Budget ($phase.DescendantsCreated / $phase.Seconds -le $definition.DescendantsPerSecondMax) "$($phase.Phase): descendant creation rate exceeded budget."
}
$remote = $phases['remote-failure']
Require-Budget ($remote.RemoteChecks -ge 20 -and $remote.RemoteOutcomes.Offline -eq $remote.RemoteChecks) 'Remote phase must complete offline attempts across the fixture set.'
Require-Budget (@($checks | Where-Object { $_.Kind -eq 'remote' -and $_.Outcome -eq 'Offline' } | Select-Object -ExpandProperty Repository -Unique).Count -eq 20) 'Remote phase did not cover every repository.'
Require-Budget ($remote.RemoteCheckP95Ms -le $budgets.RemoteCheckP95MaxMs) 'Remote failure latency exceeded budget.'
foreach ($name in @('warmup', 'minimized', 'expanded', 'churn', 'recovery')) {
    Require-Budget ($phases[$name].RemoteChecks -eq 0) "$name`: remote checks ran while disabled."
}
# Compare steady windows, not transient GC peaks or window creation allocations.
function Window-Mean($Readings, [string]$Property, [bool]$Tail) {
    $edge = if ($Tail) { $Readings[-1].Seconds - 60 } else { 60 }
    $window = @($Readings | Where-Object { if ($Tail) { $_.Seconds -ge $edge } else { $_.Seconds -le $edge } })
    if ($window.Count -lt 30) { throw 'Insufficient samples for resource stability comparison.' }
    return ($window | Measure-Object -Property $Property -Average).Average
}
$stability = @()
foreach ($name in @('minimized', 'recovery')) {
    $readings = $samples.$name
    $privateGrowth = (Window-Mean $readings 'AppPrivateMiB' $true) - (Window-Mean $readings 'AppPrivateMiB' $false)
    $handleGrowth = (Window-Mean $readings 'Handles' $true) - (Window-Mean $readings 'Handles' $false)
    Require-Budget ($privateGrowth -le $budgets.SteadyPrivateGrowthMaxMiB) "$name`: steady private-byte growth exceeded budget."
    Require-Budget ($handleGrowth -le $budgets.SteadyHandleGrowthMax) "$name`: steady handle growth exceeded budget."
    $stability += @{ Phase = $name; PrivateGrowthMiB = $privateGrowth; HandleGrowth = $handleGrowth }
}
$result = @{
    Passed = $failures.Count -eq 0
    BudgetVersion = $budgets.Version
    Scope = $budgets.Scope
    Failures = @($failures)
    Stability = $stability
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'budget-result.json') -Encoding utf8
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Output "Performance budgets passed ($($budgets.Version))."
$stability | ForEach-Object { [pscustomobject]$_ } | Format-Table -AutoSize
