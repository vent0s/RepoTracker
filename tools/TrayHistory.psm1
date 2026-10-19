Set-StrictMode -Version Latest

# Windows 10 22H2's observed IconStreams layout. This is not a public Windows API:
# reject unfamiliar headers instead of attempting a best-effort registry rewrite.
function Read-TrayIconStream {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    if ($Bytes.Length -lt 20 -or ($Bytes.Length - 20) % 1640 -ne 0) { throw 'Unsupported IconStreams length.' }
    $count = [BitConverter]::ToUInt32($Bytes, 12)
    if ([BitConverter]::ToUInt32($Bytes, 0) -ne 20 -or
        [BitConverter]::ToUInt32($Bytes, 4) -ne 7 -or
        [BitConverter]::ToUInt32($Bytes, 8) -ne 65537 -or
        [BitConverter]::ToUInt32($Bytes, 16) -ne 20 -or
        $count -ne ($Bytes.Length - 20) / 1640) { throw 'Unsupported IconStreams header or record count.' }
    $encoding = [Text.UnicodeEncoding]::new($false, $false, $true)
    for ($index = 0; $index -lt $count; $index++) {
        $offset = 20 + $index * 1640
        $rawPath = $encoding.GetString($Bytes, $offset, 528)
        $end = $rawPath.IndexOf([char]0)
        if ($end -lt 0) { throw 'Unterminated icon path; refusing to change history.' }
        $path = -join ($rawPath.Substring(0, $end).ToCharArray() | ForEach-Object {
            $number = [int]$_
            if (($number -ge 65 -and $number -le 90) -or ($number -ge 97 -and $number -le 122)) {
                $base = if ($number -le 90) { 65 } else { 97 }
                [char]($base + ($number - $base + 13) % 26)
            } else { $_ }
        })
        [pscustomobject]@{ Index = $index; Offset = $offset; Path = $path }
    }
}

function New-RepoTrackerTrayCleanup {
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string[]]$KeepExecutables
    )
    if (![IO.Path]::IsPathFullyQualified($ProjectRoot) -or $KeepExecutables.Count -eq 0) { throw 'An absolute project root and retained executable are required.' }
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/') + '\'
    $keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $KeepExecutables) {
        if (![IO.Path]::IsPathFullyQualified($path)) { throw 'Retained executable paths must be absolute.' }
        [void]$keep.Add([IO.Path]::GetFullPath($path))
    }
    $records = @(Read-TrayIconStream -Bytes $Bytes)
    $removed = [Collections.Generic.List[object]]::new()
    $retained = [Collections.Generic.List[object]]::new()
    foreach ($record in $records) {
        $path = $record.Path
        $isTarget = $false
        if ([IO.Path]::IsPathFullyQualified($path)) {
            $path = [IO.Path]::GetFullPath($path)
            $name = [IO.Path]::GetFileName($path)
            $isTarget = $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and
                ($name -ieq 'RepoTracker.Desktop.exe' -or $name -ieq 'RepoTracker.Desktop.TestHost.exe')
        }
        if ($isTarget -and !$keep.Contains($path)) { $removed.Add($record) }
        else { $retained.Add($record) }
    }
    $result = [byte[]]::new(20 + $retained.Count * 1640)
    [Array]::Copy($Bytes, 0, $result, 0, 20)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$retained.Count), 0, $result, 12, 4)
    for ($index = 0; $index -lt $retained.Count; $index++) {
        # Copy the entire untouched record, including visibility, icon data and unknown fields.
        [Array]::Copy($Bytes, $retained[$index].Offset, $result, 20 + $index * 1640, 1640)
    }
    [void]@(Read-TrayIconStream -Bytes $result)
    [pscustomobject]@{
        BeforeCount = $records.Count; AfterCount = $retained.Count
        Removed = $removed.ToArray(); Bytes = $result
        BeforeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
        AfterSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($result))
    }
}

Export-ModuleMember -Function Read-TrayIconStream, New-RepoTrackerTrayCleanup
