#Requires -Version 7.0
# Pure binary fixtures: this test never opens the registry or registers a tray icon.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../tools/TrayHistory.psm1') -Force
function Assert-True([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Malformed fixture was accepted.'
}
$paths = @(
    'P:\Jvaqbjf\ncc.rkr',
    'R:\Ercb\choyvfu\byq\ErcbGenpxre.Qrfxgbc.rkr',
    'R:\Ercb\grfgf\ErcbGenpxre.Qrfxgbc.GrfgUbfg.rkr',
    'R:\Ercb\fep\ErcbGenpxre.Qrfxgbc.rkr',
    'R:\Ercbfvgbel\ErcbGenpxre.Qrfxgbc.rkr',
    'R:\Ercb\..\Bgure\ErcbGenpxre.Qrfxgbc.rkr',
    'R:\Ercb\ErcbGenpxre.Qrfxgbc.rkr.bgure',
    'R:\Ercb\choyvfu\旧\ErcbGenpxre.Qrfxgbc.rkr'
)
$bytes = [byte[]]::new(20 + 1640 * $paths.Count)
$header = @(20, 7, 65537, $paths.Count, 20)
for ($i = 0; $i -lt 5; $i++) { [Array]::Copy([BitConverter]::GetBytes([uint32]$header[$i]), 0, $bytes, $i * 4, 4) }
for ($i = 0; $i -lt $paths.Count; $i++) {
    $encoded = [Text.Encoding]::Unicode.GetBytes($paths[$i])
    [Array]::Copy($encoded, 0, $bytes, 20 + 1640 * $i, $encoded.Length)
    # Opaque visibility/icon/metadata bytes must survive filtering exactly.
    [Array]::Fill[byte]($bytes, [byte]($i + 1), 20 + 1640 * $i + 528, 1112)
}
$before = [Convert]::ToHexString($bytes)
$plan = New-RepoTrackerTrayCleanup -Bytes $bytes -ProjectRoot 'E:\Repo' -KeepExecutables @('e:\repo\src\repotracker.desktop.exe')
Assert-True ($plan.Removed.Count -eq 3 -and $plan.AfterCount -eq 5) 'Wrong selection/count.'
Assert-True (($plan.Removed.Index -join ',') -eq '1,2,7') 'Removed retained, unrelated, prefix or traversal paths.'
Assert-True ([Convert]::ToHexString($bytes) -eq $before) 'Input bytes were mutated.'
$keptIndices = @(0,3,4,5,6)
for ($i = 0; $i -lt $keptIndices.Count; $i++) {
    $old = [Convert]::ToHexString($bytes, 20 + 1640 * $keptIndices[$i], 1640)
    $new = [Convert]::ToHexString($plan.Bytes, 20 + 1640 * $i, 1640)
    Assert-True ($old -eq $new) 'Unrelated record bytes changed.'
}
foreach ($i in @(0..11) + @(16..19)) { Assert-True ($bytes[$i] -eq $plan.Bytes[$i]) 'Unknown header bytes changed.' }
$again = New-RepoTrackerTrayCleanup -Bytes $plan.Bytes -ProjectRoot 'E:\Repo' -KeepExecutables @('E:\Repo\src\RepoTracker.Desktop.exe')
Assert-True ($again.Removed.Count -eq 0 -and $again.BeforeSha256 -eq $again.AfterSha256) 'Cleanup is not idempotent.'
$bad = [byte[]]$bytes.Clone(); $bad[4] = 8
Assert-Rejected { Read-TrayIconStream -Bytes $bad }
$bad = [byte[]]$bytes.Clone(); $bad[12] = 1
Assert-Rejected { Read-TrayIconStream -Bytes $bad }
Assert-Rejected { Read-TrayIconStream -Bytes $bytes[0..($bytes.Length - 2)] }
$bad = [byte[]]$bytes.Clone(); [Array]::Fill[byte]($bad, 65, 20, 528)
Assert-Rejected { Read-TrayIconStream -Bytes $bad }
Write-Output 'PASS: target selection, case/Unicode paths, retained executable, unrelated bytes, prefix/traversal exclusion, idempotence, unknown format/count/length/unterminated path rejection.'
