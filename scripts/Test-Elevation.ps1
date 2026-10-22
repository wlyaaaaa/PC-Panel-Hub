param([string]$HelperPath=(Join-Path $PSScriptRoot 'Elevation.ps1'))
$ErrorActionPreference='Stop'
. $HelperPath
function Check($Ok,$Label){if(-not $Ok){throw $Label};Write-Output "PASS: $Label"}
$script:mode=3;$script:fail=$false
function Get-Command($Name,$ErrorAction){
    if($Name -eq 'sudo.exe'){return [pscustomobject]@{Source='Invoke-ElevationSudoFixture'}}
    Microsoft.PowerShell.Core\Get-Command $Name -ErrorAction Stop
}
function Get-ItemProperty { [pscustomobject]@{Enabled=$script:mode} }
function Invoke-ElevationSudoFixture {
    $script:encoded=$args[-1];$global:LASTEXITCODE=17
    '中文 sudo 输出'
}
function Start-Process {
    param($FilePath,$Verb,$WindowStyle,[switch]$Wait,[switch]$PassThru,$ArgumentList,$ErrorAction)
    Check ($Verb -eq 'RunAs' -and $WindowStyle -eq 'Hidden' -and $Wait -and $PassThru) 'fallback is hidden, elevated and awaited' | Out-Null
    if($script:fail){throw 'fixture UAC unavailable'}
    $body=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($ArgumentList[-1]))
    $script:log=[regex]::Match($body,"\*> '([^']+)'").Groups[1].Value
    [IO.File]::WriteAllText($script:log,'中文 fallback 输出',[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ExitCode=19}
}
$result=Invoke-ProjectElevated 'Write-Output fixture'
Check ($result -eq '中文 sudo 输出' -and $LASTEXITCODE -eq 17) 'inline sudo preserves output and exit code'
$body=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($script:encoded))
Check ($body -match 'OutputEncoding' -and $body -match 'Set-Location' -and $body -match 'WindowsIdentity') 'child enforces UTF-8, original directory and Windows user'
$script:mode=0
$result=Invoke-ProjectElevated 'Write-Output fixture'
Check ($result -eq '中文 fallback 输出' -and $LASTEXITCODE -eq 19) 'fallback preserves UTF-8 output and exit code'
Check (-not(Test-Path -LiteralPath $script:log)) 'fallback removes its own output file'
$script:fail=$true;$failed=$false
try{Invoke-ProjectElevated 'Write-Output fixture'}catch{$failed=$_.Exception.Message -match '需要本人在场提权'}
Check $failed 'unavailable elevation reports the need for owner presence'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('elevation-'+[guid]::NewGuid().ToString('N')+'.ps1')
try{
    [IO.File]::WriteAllText($fixture,'param([switch]$Flag,[string[]]$Items,[string]$Text) [pscustomobject]@{flag=[bool]$Flag;items=$Items;text=$Text}',[Text.UTF8Encoding]::new($false))
    $command=New-ProjectInvocation $fixture @{Flag=[Management.Automation.SwitchParameter]::new($false);Items=@('a b',"c'd");Text='中文 $literal'}
    $value=& ([scriptblock]::Create($command))
    Check (-not $value.flag -and $value.items.Count -eq 2 -and $value.items[1] -eq "c'd" -and $value.text -eq '中文 $literal') 'script parameters preserve false switches, arrays, quotes and Chinese'
}finally{Remove-Item -LiteralPath $fixture -Force}
