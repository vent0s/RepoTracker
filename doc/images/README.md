# README screenshots

These PNGs are rendered from the production WPF windows using fictional repository names, paths, branches, and status results. They illustrate the interface, not successful Git operations against a real remote.

The documentation mode in `RepoTracker.Desktop.TestHost` uses in-memory configuration and sample status adapters. It does not read a real profile, create Git repositories or branches, run Git, access the network, register a tray icon, or change login startup. It briefly opens its own windows and writes screenshots and session logs under the specified output directory. The application's normal language defaults are unchanged.

## Regenerate on Windows

Use the SDK pinned in the repository's `global.json`. From the repository root:

```powershell
dotnet restore RepoTracker.slnx --locked-mode
dotnet build tests/RepoTracker.Desktop.TestHost/RepoTracker.Desktop.TestHost.csproj --configuration Release --no-restore

$captureExe = Join-Path $PWD 'tests/RepoTracker.Desktop.TestHost/bin/Release/net10.0-windows/RepoTracker.Desktop.TestHost.exe'
foreach ($language in @('en', 'zh-Hans')) {
    $captureOutput = Join-Path $PWD ".artifacts/readme-screenshots/$language"
    $captureProcess = Start-Process -FilePath $captureExe -WindowStyle Hidden -PassThru -ArgumentList @(
        '--readme-screenshots', ('"{0}"' -f $captureOutput), $language
    )
    if (-not $captureProcess.WaitForExit(45000)) {
        $captureProcess.Kill()
        throw "Screenshot capture timed out: $language"
    }
    if ($captureProcess.ExitCode -ne 0) {
        throw "Screenshot capture failed: $captureOutput/error.txt"
    }
    foreach ($name in @('main', 'main-light', 'add-repository', 'settings')) {
        Copy-Item -LiteralPath (Join-Path $captureOutput "$name-$language.png") -Destination 'doc/images/'
    }
}
```

Review all eight images before committing them. Main panels show six fictional repositories, including two children; settings show language and appearance controls. Screenshots use 100% app scaling and omit the operating system's window frame. Do not substitute an image of a personal profile or use these renders as physical DPI / Windows compatibility evidence.

The English [README](../../README.md) and [Chinese translation](../../README.zh-CN.md) should cover the same behavior and link to the corresponding language's images.
