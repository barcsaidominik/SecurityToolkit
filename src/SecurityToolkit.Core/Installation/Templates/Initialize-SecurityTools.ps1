#requires -Version 7.2
[CmdletBinding()]
param([switch]$RequirePinned)
. (Join-Path $PSScriptRoot 'Toolkit.Common.ps1')
$lock=Read-ToolkitLock $PSScriptRoot
$errors=[Collections.Generic.List[string]]::new()
$bin=Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
foreach($name in @('semgrep','gitleaks','osv')) {
    $tool=$lock.tools[$name]
    if (!$tool.enabled) { continue }
    try {
        if ($RequirePinned -and $tool.image -notmatch '@sha256:[a-f0-9]{64}$') { throw 'Image must be pinned first by the installer.' }
        $r=Invoke-ToolkitProcess docker @('pull',$tool.image) -TimeoutSeconds 1800
        if ($r.ExitCode -ne 0) { throw $r.StdErr }
        $r=Invoke-ToolkitProcess docker @('image','inspect',$tool.image,'--format','{{index .RepoDigests 0}}')
        if ($r.ExitCode -ne 0 -or $r.StdOut.Trim() -notmatch '@sha256:[a-f0-9]{64}$') { throw 'Cannot resolve image digest.' }
        $tool.image=$r.StdOut.Trim(); $tool.ready=$true
        Write-Host "Ready: $name"
    } catch { $tool.ready=$false; $errors.Add("${name}: $($_.Exception.Message)") }
}
foreach($name in @('devskim','sonarScanner','dotnetCoverage')) {
    $tool=$lock.tools[$name]
    if (!$tool -or !$tool.enabled) { continue }
    try {
        if ($RequirePinned -and !$tool.version) { throw 'Pin the .NET tool version first.' }
        if (!$tool.version) { $tool.version=Get-ToolkitNuGetVersion $tool.package }
        $r=Invoke-ToolkitProcess dotnet @('tool','list','--tool-path',$bin)
        if ($r.ExitCode -ne 0) { throw $r.StdErr }
        $escaped=[regex]::Escape($tool.package)
        $row=[regex]::Match($r.StdOut,"(?im)^$escaped\s+(\S+)\s+")
        if ($row.Success -and $row.Groups[1].Value -ne $tool.version) {
            throw "Existing tool version differs. Remove/update $($tool.package) explicitly before reprovisioning."
        }
        if (!$row.Success) {
            $r=Invoke-ToolkitProcess dotnet @('tool','install',$tool.package,'--version',$tool.version,'--tool-path',$bin)
            if ($r.ExitCode -ne 0) { throw $r.StdErr }
        }
        $tool.ready=$true; Write-Host "Ready: $name $($tool.version)"
    } catch { $tool.ready=$false; $errors.Add("${name}: $($_.Exception.Message)") }
}
Write-ToolkitJson (Join-Path $PSScriptRoot 'security.lock.json') $lock
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'results') | Out-Null
Write-ToolkitJson (Join-Path $PSScriptRoot 'results/setup-status.json') @{errors=$errors.ToArray();completed=($errors.Count -eq 0)}
if ($errors.Count) { $errors | ForEach-Object { Write-Warning $_ }; exit 2 }
exit 0
