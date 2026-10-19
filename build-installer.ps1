#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$CompilerPath = (Join-Path $PSScriptRoot '.local/installer-tools/inno-7.1.0/ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Product -ne 'RepoTracker' -or $manifest.RuntimeIdentifier -ne 'win-x64' -or
    $manifest.Version -notmatch '^\d+\.\d+\.\d+$') { throw 'An official numeric-version Windows x64 package is required.' }
foreach ($entry in $manifest.Files) {
    $file = [IO.Path]::GetFullPath((Join-Path $package $entry.Path))
    if (!$file.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Item -LiteralPath $file).Length -ne $entry.Bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Package verification failed: $($entry.Path)" }
}
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File)
if ($files.Count -ne $manifest.Files.Count + 1 -or
    @(Get-ChildItem -LiteralPath $package -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Unexpected files or directory links in package.' }
$compiler = Get-Item -LiteralPath $CompilerPath
$compilerVersion = (& $compiler.FullName --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^7\.1\.0(?:\s|$)') { throw "Inno Setup 7.1.0 is required; found $compilerVersion" }
$output = Split-Path -Parent $package
$list = Join-Path $output 'installer-files.sha256'
$files | Sort-Object FullName | ForEach-Object {
    '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, [IO.Path]::GetRelativePath($package, $_.FullName)
} | Set-Content -LiteralPath $list -Encoding utf8BOM
& $compiler.FullName /Qp "/DPayloadDir=$package" "/DReleaseVersion=$($manifest.Version)" "/DReleaseFileVersion=$($manifest.Version).0" "/DOutputDir=$output" "/DFileList=$list" (Join-Path $PSScriptRoot 'installer/RepoTracker.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $output "RepoTracker-$($manifest.Version)-win-x64-Setup.exe"
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
@{ Product = 'RepoTracker'; Version = $manifest.Version; AppId = 'Septim.RepoTracker'; Scope = 'current-user';
    Installer = [IO.Path]::GetFileName($setup); Sha256 = $hash; Signed = $false; Compiler = $compilerVersion;
    SourceCommit = $manifest.SourceCommit; SourceDirty = $manifest.SourceDirty; Runtime = $manifest.Runtime
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'installer-manifest.json') -Encoding utf8
Write-Output "Installer: $setup"
Write-Output "SHA256: $hash"
