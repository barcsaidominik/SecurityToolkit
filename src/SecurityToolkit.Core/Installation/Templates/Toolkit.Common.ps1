#requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-ToolkitPath {
    param([string]$Root,[string]$Relative,[switch]$AllowMissing)
    if ([IO.Path]::IsPathRooted($Relative)) { throw "Project paths must be relative: $Relative" }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    $rel = [IO.Path]::GetRelativePath($Root,$full)
    if ($rel -eq '..' -or $rel -match '^\.\.[/\\]') { throw "Path leaves project: $Relative" }
    if (!$AllowMissing -and !(Test-Path -LiteralPath $full)) { throw "Missing path: $Relative" }
    return $full
}
function Invoke-ToolkitProcess {
    param([string]$File,[string[]]$Arguments=@(),[string]$WorkingDirectory=(Get-Location).Path,
          [string]$LogPath='', [int]$TimeoutSeconds=1800)
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName=$File; $psi.WorkingDirectory=$WorkingDirectory
    $psi.UseShellExecute=$false; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
    foreach($arg in $Arguments) { $psi.ArgumentList.Add($arg) }
    # npm/winget .cmd wrappers cannot be executed without a shell on Windows.
    $p=[Diagnostics.Process]::new(); $p.StartInfo=$psi
    try {
        if (!$p.Start()) { throw "Could not start $File" }
        $stdout=$p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync()
        if (!$p.WaitForExit($TimeoutSeconds*1000)) {
            $p.Kill($true); $p.WaitForExit(); throw "Timed out: $File"
        }
        $result=@{ExitCode=$p.ExitCode; StdOut=$stdout.GetAwaiter().GetResult(); StdErr=$stderr.GetAwaiter().GetResult()}
        if ($LogPath) { [IO.File]::WriteAllText($LogPath, $result.StdOut+"`n"+$result.StdErr) }
        return $result
    } finally { $p.Dispose() }
}
function Get-ToolkitNuGetVersion {
    param([string]$Package)
    $id=$Package.ToLowerInvariant()
    $data=Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$id/index.json"
    $stable=@($data.versions | Where-Object { $_ -notmatch '-' })
    if (!$stable.Count) { throw "No stable version: $Package" }
    return $stable[-1]
}
function Write-ToolkitJson {
    param([string]$Path,$Value)
    [IO.File]::WriteAllText($Path, (ConvertTo-ToolkitJson $Value),[Text.UTF8Encoding]::new($false))
}
function Read-ToolkitLock {
    param([string]$Directory)
    $path=Join-Path $Directory 'security.lock.json'
    $lock=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if ($lock.schemaVersion -ne 1) { throw 'Unsupported toolkit lock schema.' }
    return $lock
}
function ConvertTo-ToolkitCanonical {
    param($Value)
    if($Value -is [Collections.IDictionary]){
        $ordered=[ordered]@{}
        foreach($key in @($Value.Keys | Sort-Object -CaseSensitive)){$ordered[$key]=ConvertTo-ToolkitCanonical $Value[$key]}
        return $ordered
    }
    if($Value -is [Collections.IEnumerable] -and $Value -isnot [string]){
        $items=@(foreach($item in $Value){ConvertTo-ToolkitCanonical $item})
        return ,$items
    }
    return $Value
}
function ConvertTo-ToolkitJson {
    param($Value)
    return (ConvertTo-Json -InputObject (ConvertTo-ToolkitCanonical $Value) -Depth 30)+"`n"
}
