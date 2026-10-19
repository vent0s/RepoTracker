#Requires -Version 7.0
[CmdletBinding()]
param(
    # An explicit compatibility build; omission requires the repository's pinned SDK.
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$SdkVersion,
    [switch]$PortableOnly,
    [string]$CompilerPath = (Join-Path $PSScriptRoot '.local/installer-tools/inno-7.1.0/ISCC.exe')
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$pinnedSdk = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
if (!$SdkVersion) { $SdkVersion = $pinnedSdk }
$runtimeVersion = '10.0.13'
$project = Join-Path $projectRoot 'src/RepoTracker.Desktop/RepoTracker.Desktop.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid package version.' }
$packageName = "RepoTracker-$version-win-x64"
$run = Join-Path $projectRoot ('publish/{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 6))
$work = Join-Path $run 'build'
$package = Join-Path $run $packageName
New-Item -ItemType Directory -Path $work, $package | Out-Null
# SDK resolution uses the command working directory. Never edit the repository pin.
@{ sdk = @{ version = $SdkVersion; rollForward = 'disable'; allowPrerelease = $false } } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'global.json') -Encoding utf8

Push-Location $work
try {
    $actualSdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $SdkVersion) { throw "Required SDK $SdkVersion is not installed. Use -SdkVersion only for an explicit compatibility build." }
    & dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
        --artifacts-path (Join-Path $work 'artifacts') --output $package '-p:RestoreLockedMode=true' `
        "-p:RuntimeFrameworkVersion=$runtimeVersion" '-p:PublishTrimmed=false' '-p:PublishSingleFile=false' `
        '-p:PublishReadyToRun=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:ContinuousIntegrationBuild=true'
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed; no ZIP was produced.' }
} finally { Pop-Location }

foreach ($required in @('RepoTracker.Desktop.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll',
    'RepoTracker.Core.dll', 'zh-Hans/RepoTracker.Core.resources.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $required))) { throw "Missing package file: $required" }
}
$runtime = Get-Content -LiteralPath (Join-Path $package 'RepoTracker.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.frameworks -or $runtime.runtimeOptions.framework) { throw 'Package unexpectedly depends on a shared framework.' }
if (@($runtime.runtimeOptions.includedFrameworks).Count -ne 2 -or
    @($runtime.runtimeOptions.includedFrameworks | Where-Object version -NE $runtimeVersion).Count -gt 0) { throw 'Unexpected bundled framework versions.' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'doc/便携版使用说明.md') -Destination (Join-Path $package 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'doc/安装与升级.md') -Destination (Join-Path $package 'INSTALL.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'doc/兼容性与发布说明.md') -Destination (Join-Path $package 'COMPATIBILITY.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'doc/依赖与素材核查.md') -Destination (Join-Path $package 'DEPENDENCIES.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $package
# Keep both runtime packages' license/notice files, including files not copied by the SDK.
Push-Location $work
try {
    $cacheLine = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate runtime license files.' }
} finally { Pop-Location }
$cache = ($cacheLine -replace '^[^:]+:\s*', '').Trim()
$licenses = Join-Path $package 'licenses'
$runtimePackages = @()
foreach ($dependency in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64', 'microsoft.netcore.app.host.win-x64')) {
    $source = Join-Path $cache "$dependency/$runtimeVersion"
    $destination = Join-Path $licenses $dependency
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $notices = @(Get-ChildItem -LiteralPath $source -File | Where-Object Name -Match '(?i)license|third.*notices')
    if (!$notices.Count) { throw "Missing runtime license notices: $dependency" }
    $notices | Copy-Item -Destination $destination
    $runtimePackages += @{ Id = $dependency; Version = $runtimeVersion;
        NuGetSha512 = (Get-Content -LiteralPath (Join-Path $source "$dependency.$runtimeVersion.nupkg.sha512") -Raw).Trim() }
}
if (@(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object Name -Match '(?i)xunit|testhost|testplatform|codecoverage').Count) {
    throw 'Test dependencies unexpectedly included in product package.'
}
$commit = & git -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision.' }
$dirty = [bool](& git -C $projectRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source state.' }
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ Path = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/'); Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{
    FormatVersion = 1; Product = 'RepoTracker'; Version = $version; RuntimeIdentifier = 'win-x64'
    Deployment = 'self-contained portable folder'; Signed = $false; BuiltAtUtc = [DateTime]::UtcNow.ToString('O')
    Sdk = $actualSdk; PinnedSdk = $pinnedSdk; CompatibilityBuild = $actualSdk -ne $pinnedSdk; Runtime = $runtimeVersion
    RuntimePackages = $runtimePackages
    SourceCommit = $commit; SourceDirty = $dirty; ExternalPrerequisites = @('Windows x64; tested/support scope in COMPATIBILITY.md', 'Git on PATH')
    Files = $files
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
$zip = Join-Path $run "$packageName.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$hash  $packageName.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
Write-Output "Package: $zip"
Write-Output "SHA256: $hash"
Write-Output "SDK: $actualSdk (pinned: $pinnedSdk); runtime: $runtimeVersion; unsigned local preview."
if (!$PortableOnly) { & (Join-Path $projectRoot 'build-installer.ps1') -PackageDirectory $package -CompilerPath $CompilerPath }
