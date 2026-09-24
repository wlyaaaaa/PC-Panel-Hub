param(
 [Parameter(Mandatory=$true)][switch]$Live,
 [Parameter(Mandatory=$true)][string]$ResultPath,
 [ValidatePattern('^[A-Za-z0-9]+$')][string]$MainMonitorHardwareId='PHLC34B',
 [ValidatePattern('^[A-Za-z0-9]+$')][string]$VddMonitorHardwareId='MTT1337'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'Interactive desktop required.'}
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\turzx_side_screen\WindowsDisplayWindowPolicy.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies 'System.Windows.Forms','System.Drawing','System.ComponentModel.Primitives','System.ComponentModel.TypeConverter' -TypeDefinition @"
using System;using System.Windows.Forms;using System.Runtime.InteropServices;
public sealed class DesktopReturnFixture:Form {
 protected override bool ShowWithoutActivation { get {return true;} }
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@
$native=Initialize-HS2ExclusiveWindowGuardNativeMethods
$native::UsePerMonitorV2DpiAwareness()
$monitors=@($native::CaptureMonitors());$ids=@($native::CaptureMonitorIdentities())
$mainIds=@($ids|Where-Object {Test-WindowGuardMonitorHardwareId -Identity $_ -HardwareId $MainMonitorHardwareId}|ForEach-Object DeviceName)
$vddIds=@($ids|Where-Object {Test-WindowGuardMonitorHardwareId -Identity $_ -HardwareId $VddMonitorHardwareId}|ForEach-Object DeviceName)
$main=@($monitors|Where-Object {$mainIds -contains $_.DeviceName});$vdd=@($monitors|Where-Object {$vddIds -contains $_.DeviceName})
if($main.Count -ne 1 -or $vdd.Count -ne 1){throw "Unique main ($MainMonitorHardwareId) and VDD ($VddMonitorHardwareId) required; no topology modification."}
$rows=@();$forms=@();$fg=[DesktopReturnFixture]::GetForegroundWindow()
try {
 foreach($state in @('Normal','Minimized','Maximized')) {
  $form=[DesktopReturnFixture]::new();$forms+=$form
  $form.Text='Desktop return acceptance';$form.StartPosition='Manual'
  $form.Bounds=[Drawing.Rectangle]::new($vdd[0].WorkLeft+80,$vdd[0].WorkTop+80,520,360)
  $form.Show();$form.WindowState=[Windows.Forms.FormWindowState]::$state
  [Windows.Forms.Application]::DoEvents();$hwnd=$form.Handle.ToInt64()
  $end=[DateTime]::UtcNow.AddSeconds(5);$w=$null
  do {
   [Windows.Forms.Application]::DoEvents()
   $w=@($native::CaptureWindows([int[]]@($PID))|Where-Object {$_.Hwnd -eq $hwnd -and $_.ProcessId -eq $PID})|Select-Object -First 1
   $settled=$null -ne $w -and (($state -eq 'Minimized' -and $w.IsMinimized) -or ($state -eq 'Maximized' -and $w.IsMaximized) -or ($state -eq 'Normal' -and -not $w.IsMinimized -and -not $w.IsMaximized))
   if($null -ne $w -and $w.MonitorDevice -eq $main[0].DeviceName -and $settled){break}
   Start-Sleep -Milliseconds 30
  } while([DateTime]::UtcNow -lt $end)
  $preserved=$null -ne $w -and (($state -eq 'Minimized' -and $w.IsMinimized) -or ($state -eq 'Maximized' -and $w.IsMaximized) -or ($state -eq 'Normal' -and -not $w.IsMinimized -and -not $w.IsMaximized))
  $rows+=[pscustomobject]@{Case=$state;ReturnedToMain=($null -ne $w -and $w.MonitorDevice -eq $main[0].DeviceName);ShowStatePreserved=$preserved;ObservedMinimized=if($w){$w.IsMinimized}else{$null};ObservedMaximized=if($w){$w.IsMaximized}else{$null}}
  $form.Close();$form.Dispose()
 }
 $names=@{};Get-Process|ForEach-Object{$names[[int]$_.Id]=$_.ProcessName}
 $windows=@($native::CaptureWindows([int[]]@()))
 foreach($w in $windows){if($names.ContainsKey($w.ProcessId)){$w.ProcessName=$names[$w.ProcessId]}}
 $plan=Get-VddWindowReturnPlan -Monitors @($native::CaptureMonitors()) -MonitorIdentities @($native::CaptureMonitorIdentities()) -Windows $windows -MainMonitorHardwareId $MainMonitorHardwareId -VddMonitorHardwareId $VddMonitorHardwareId
 $result=@{status=if(@($rows|Where-Object {-not $_.ReturnedToMain -or -not $_.ShowStatePreserved}).Count -eq 0 -and @($plan.Actions).Count -eq 0 -and [DesktopReturnFixture]::GetForegroundWindow() -eq $fg){'pass'}else{'failed'};utc=[datetime]::UtcNow.ToString('o');cases=$rows;remainingOrdinaryVddWindows=@($plan.Actions).Count;foregroundUnchanged=([DesktopReturnFixture]::GetForegroundWindow() -eq $fg);noDisplayChange=$true}
 [IO.File]::WriteAllText($ResultPath,($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
 if($result.status -ne 'pass'){throw 'Live acceptance failed; see result.'}
} finally {foreach($f in $forms){if(-not $f.IsDisposed){$f.Close();$f.Dispose()}}}
