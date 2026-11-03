#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProjectRoot,[Parameter(Mandatory)][string]$Manifest,[switch]$DryRun)
. (Join-Path $PSScriptRoot 'Toolkit.Common.ps1')
$root=(Resolve-Path -LiteralPath $ProjectRoot).Path
$data=Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json -AsHashtable
if($data.schemaVersion -ne 1){throw 'Unsupported backup manifest.'}
# Preflight all entries before touching anything, to avoid a partial conflict restore.
foreach($change in $data.changes){
    $path=Get-ToolkitPath $root $change.path -AllowMissing
    if(!(Test-Path $path) -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $change.newHash){throw "Modified/missing since installation: $($change.path). Restore refuses to overwrite later changes."}
    if($change.existed -and !(Test-Path -LiteralPath $change.backup)){throw "Missing backup: $($change.path)"}
}
$changes=@($data.changes);[array]::Reverse($changes)
foreach($change in $changes){
    $path=Get-ToolkitPath $root $change.path -AllowMissing
    if($DryRun){Write-Host "Would restore: $($change.path)";continue}
    if($change.existed){Copy-Item -LiteralPath $change.backup -Destination $path -Force}
    else{Remove-Item -LiteralPath $path -Force}
}
if(!$DryRun){
    $state=Join-Path $root '.security/install-state.json'
    if(Test-Path $state){
        $data=Get-Content $state -Raw | ConvertFrom-Json -AsHashtable
        foreach($key in @($data.hashes.Keys)){
            $path=Join-Path $root $key
            if(Test-Path $path){$data.hashes[$key]=(Get-FileHash $path -Algorithm SHA256).Hash}else{$data.hashes.Remove($key)}
        }
        Write-ToolkitJson $state $data
    }
}
Write-Host 'Project files restored. Machine packages, Docker volumes and external service changes are not uninstalled.'
