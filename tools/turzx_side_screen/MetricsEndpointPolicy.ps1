# Shared by metrics startup and shutdown; no service, no socket retained.
if (-not ('TURZX.MetricsProcessState' -as [type])) {
 Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace TURZX {
 public static class MetricsProcessState {
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static bool IsActive(int id) {
   if(id<=0) throw new ArgumentOutOfRangeException("id");
   var handle=OpenProcess(0x1000,false,id);
   if(handle==IntPtr.Zero) { int error=Marshal.GetLastWin32Error(); if(error==87) return false; throw new Win32Exception(error); }
   try { uint exitCode; if(!GetExitCodeProcess(handle,out exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error()); return exitCode==259; }
   finally { CloseHandle(handle); }
  }
 }
}
"@
}
function Test-MetricsProcessActive {
 param([Parameter(Mandatory=$true)][int]$ProcessId)
 return [TURZX.MetricsProcessState]::IsActive($ProcessId)
}
function Test-MetricsEndpointBind {
 param([ValidateRange(0,65535)][int]$Port=18765,[string]$HostName='127.0.0.1')
 $listener=$null
 $script:metricsPortProbeError=$null
 try {
  foreach($connection in @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { [int]$_.LocalPort -eq $Port })) {
   # Get-Process/CIM may retain an exiting process inconsistently. The kernel
   # exit code, not row presence, decides whether user-mode service still runs.
   if(Test-MetricsProcessActive -ProcessId ([int]$connection.OwningProcess)) { return $false }
  }
  $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse($HostName),$Port)
  # A dead owner can still retain kernel sockets. Never force-reuse that endpoint.
  $listener.Server.ExclusiveAddressUse=$true
  $listener.Start()
  return $true
 } catch { $script:metricsPortProbeError=$_.Exception.Message; return $false }
 finally { if($null-ne$listener){$listener.Stop()} }
}
function Get-TurzxMetricsEndpoint {
 param([string]$ConfigPath=(Join-Path $PSScriptRoot 'config.json'))
 $hostName='127.0.0.1';$port=18765
 if(Test-Path -LiteralPath $ConfigPath) {
  $config=Get-Content -LiteralPath $ConfigPath -Encoding UTF8 -Raw -ErrorAction Stop|ConvertFrom-Json
  if($null-ne$config.PSObject.Properties['metrics']) {
   $metrics=$config.metrics
   if($null-ne$metrics.PSObject.Properties['listenHost']){$hostName=[string]$metrics.listenHost}
   if($null-ne$metrics.PSObject.Properties['listenPort']){$port=[int]$metrics.listenPort}
   if($port-lt1-or$port-gt65535){throw 'Invalid configured metrics port'}
   $address=$null
   if(-not[Net.IPAddress]::TryParse($hostName,[ref]$address)-or-not[Net.IPAddress]::IsLoopback($address)){
    throw 'The metrics endpoint must remain loopback-only.'
   }
   $url='http://{0}:{1}/snapshot'-f $hostName,$port
   if($null-ne$metrics.PSObject.Properties['url']-and[string]$metrics.url-ne$url){
    throw 'Configured metrics URL and listener disagree; repair the single config source first.'
   }
  }
 }
 [pscustomobject]@{HostName=$hostName;Port=$port;Url=('http://{0}:{1}/snapshot'-f $hostName,$port)}
}
