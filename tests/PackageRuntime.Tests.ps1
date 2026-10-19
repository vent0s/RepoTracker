#Requires -Version 7.0
# A tiny invalid package exercises relocation/failure cleanup without launching an app or tray icon.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$fixture = Join-Path $PSScriptRoot ('artifacts/package-script-test-' + [guid]::NewGuid().ToString('N'))
$runtime = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/package-runtime'))
if (Test-Path -LiteralPath $runtime) { throw 'A package run already owns the reserved runtime.' }
$ownsRuntime = $false
try {
    $package = Join-Path $fixture 'RepoTracker-fixture'
    New-Item -ItemType Directory -Path $package | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $package 'keep.txt'), [byte[]]@(1))
    @{ Files = @(@{ Path = 'keep.txt'; Bytes = 1; Sha256 = ('0' * 64) }) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json')
    $zip = Join-Path $fixture 'fixture.zip'
    Compress-Archive -LiteralPath $package -DestinationPath $zip
    ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + '  fixture.zip') | Set-Content -LiteralPath "$zip.sha256"
    $script = Join-Path $PSScriptRoot 'run-package.ps1'
    $output = & (Get-Process -Id $PID).Path -NoProfile -File $script -PackageZip $zip 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output | Out-String) -notmatch 'Package checksum mismatch: keep.txt') {
        throw "Invalid package failed for the wrong reason: $output"
    }
    if (Test-Path -LiteralPath $runtime) { throw 'Failed package left its reserved executable paths behind.' }
    # An existing runtime belongs to someone else and must never be cleaned or overwritten.
    New-Item -ItemType Directory -Path $runtime -ErrorAction Stop | Out-Null
    $ownsRuntime = $true
    $sentinel = Join-Path $runtime 'owner.txt'
    [IO.File]::WriteAllText($sentinel, 'another run')
    $output = & (Get-Process -Id $PID).Path -NoProfile -File $script -PackageZip $zip 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output | Out-String) -notmatch 'already in use or needs cleanup' -or
        [IO.File]::ReadAllText($sentinel) -ne 'another run') { throw 'Occupied runtime was not protected.' }
    Write-Output 'PASS: tiny package relocates to fixed app path, checksum failure cleans its runtime, occupied runtime is preserved.'
} finally {
    # Only paths created by this fixture may be removed, after absolute boundary/reparse checks.
    $targets = @($fixture)
    if ($ownsRuntime) { $targets += $runtime }
    $parent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
    foreach ($path in $targets) {
        if (!(Test-Path -LiteralPath $path)) { continue }
        $resolved = (Resolve-Path -LiteralPath $path).Path
        if ([IO.Path]::GetDirectoryName($resolved) -ne $parent) { throw 'Fixture cleanup escaped artifacts.' }
        $entries = @(Get-Item -LiteralPath $resolved -Force) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
        if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Fixture contains reparse points.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
