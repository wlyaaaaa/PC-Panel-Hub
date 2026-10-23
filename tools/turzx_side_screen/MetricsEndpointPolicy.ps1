# Shared by metrics startup and shutdown; no service, no socket retained.
if (-not ('TURZX.MetricsProcessState' -as [type])) {
 Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace TURZX {
 public static class MetricsProcessState {
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
  [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static bool IsActive(int id) {
   if(id<=0) throw new ArgumentOutOfRangeException("id");
   var handle=OpenProcess(0x00100000,false,id); // SYNCHRONIZE
   if(handle==IntPtr.Zero) { int error=Marshal.GetLastWin32Error(); if(error==87) return false; throw new Win32Exception(error); }
   try {
    uint result=WaitForSingleObject(handle,0);
    if(result==0) return false; // WAIT_OBJECT_0: process completed
    if(result==258) return true; // WAIT_TIMEOUT: process has not completed
    if(result==0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
    throw new InvalidOperationException("Unexpected process wait result: "+result);
   }
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
   # wait state, not an exit code or row presence, decides process completion.
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

function Get-TurzxMetricsEndpointOwnership {
    param([string]$SideRoot = $PSScriptRoot, [object]$Endpoint)

    $expectedAgent = [IO.Path]::GetFullPath((Join-Path $SideRoot 'metrics_agent.py'))
    try {
        if ($null -eq $Endpoint) {
            $Endpoint = Get-TurzxMetricsEndpoint -ConfigPath (Join-Path $SideRoot 'config.json')
        }
        $owners = @(Get-NetTCPConnection -State Listen -LocalPort $Endpoint.Port -ErrorAction Stop |
            Select-Object -ExpandProperty OwningProcess -Unique)
        if ($owners.Count -ne 1) {
            return [pscustomobject]@{ Managed = $false; Reason = ('listener-owner-count={0}' -f $owners.Count) }
        }
        $ownerId = [int]$owners[0]
        $process = @(Get-CimInstance Win32_Process -Filter ('ProcessId={0}' -f $ownerId) `
            -OperationTimeoutSec 8 -ErrorAction Stop)
        $agentArgument = '(?i)(?:^|[\s"])' + [regex]::Escape($expectedAgent) + '(?:[\s"]|$)'
        # A successful HTTP health response does not establish deployment identity.
        # In particular, an agent loaded from a deleted worktree can stay alive.
        $managed = $process.Count -eq 1 -and
            [string]$process[0].Name -like 'python*' -and
            [string]$process[0].CommandLine -match $agentArgument -and
            (Test-Path -LiteralPath $expectedAgent -PathType Leaf) -and
            (Test-MetricsProcessActive -ProcessId $ownerId)
        return [pscustomobject]@{
            Managed = [bool]$managed
            Reason = ('{0} pid={1}' -f $(if ($managed) { 'managed-agent' } else { 'unexpected-agent-source' }), $ownerId)
        }
    }
    catch {
        return [pscustomobject]@{ Managed = $false; Reason = ('ownership-unverified: {0}' -f $_.Exception.Message) }
    }
}

function Wait-ManagedMetricsAgentExitAndPortRelease {
    param([int[]]$ProcessIds, [object]$Endpoint, [ValidateRange(1, 30)][int]$TimeoutSeconds = 8)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $remaining = @($ProcessIds | Where-Object { Test-MetricsProcessActive -ProcessId $_ })
        if ($remaining.Count -eq 0 -and (Test-MetricsEndpointBind -Port $Endpoint.Port -HostName $Endpoint.HostName)) { return $true }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Invoke-TurzxMetricsAgentMaintenance {
    param([string]$SideRoot = $PSScriptRoot, [string]$PythonPath, [switch]$Restart)

    $endpoint = Get-TurzxMetricsEndpoint -ConfigPath (Join-Path $SideRoot 'config.json')
    $agent = Join-Path $SideRoot 'metrics_agent.py'
    $managed = @(Get-CimInstance Win32_Process -OperationTimeoutSec 8 -ErrorAction Stop |
        Where-Object { $_.Name -like 'python*' -and $_.CommandLine -like ('*' + $agent + '*') })
    foreach ($candidate in $managed) {
        if ($Restart) {
            Stop-Process -Id $candidate.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    if ($Restart) {
        [void](Wait-ManagedMetricsAgentExitAndPortRelease -ProcessIds @($managed | ForEach-Object { [int]$_.ProcessId }) -Endpoint $endpoint)
    }
    $remaining = @($managed | Where-Object { Test-MetricsProcessActive -ProcessId ([int]$_.ProcessId) })
    if ($remaining.Count -gt 0 -or -not (Test-MetricsEndpointBind -Port $endpoint.Port -HostName $endpoint.HostName)) {
        $owners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop |
            Where-Object { [int]$_.LocalPort -eq $endpoint.Port } |
            Select-Object -ExpandProperty OwningProcess -Unique) -join ','
        return [pscustomobject]@{ Process = $null; Status = ('blocked remainingPids={0} metricsPort={1} metricsPortOwners={2}' -f
            (@($remaining | ForEach-Object { $_.ProcessId }) -join ','), $endpoint.Port, $owners) }
    }
    if ([string]::IsNullOrWhiteSpace($PythonPath)) {
        $PythonPath = Find-Python
    }
    $requiredModules = if (
        [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DSN) -and
        ([string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_PASSWORD) -or [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_USER))
    ) { @() } else { @("psutil", "asyncpg") }
    if (!(Test-PythonModules -Path $PythonPath -Modules $requiredModules)) {
        throw "Selected Python interpreter is missing required runtime modules."
    }

    $outDir = Join-Path $SideRoot 'out'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    # Keep the newest 32 launches (stdout and stderr together). A restart storm
    # must not leave thousands of permanent diagnostic files behind.
    $oldMetricsLogs = @(Get-ChildItem -LiteralPath $outDir -File -Filter 'metrics-agent.*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^metrics-agent\.(\d{8}-\d{9}-\d+)\.(stdout|stderr)\.log$' })
    $keepTags = @($oldMetricsLogs |
        ForEach-Object { [regex]::Match($_.Name, '^metrics-agent\.(\d{8}-\d{9}-\d+)\.').Groups[1].Value } |
        Sort-Object -Descending -Unique | Select-Object -First 31)
    foreach ($oldLog in $oldMetricsLogs) {
        $tag = [regex]::Match($oldLog.Name, '^metrics-agent\.(\d{8}-\d{9}-\d+)\.').Groups[1].Value
        if ($tag -notin $keepTags) {
            Remove-Item -LiteralPath $oldLog.FullName -Force -ErrorAction SilentlyContinue
        }
    }
    $tag = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'), $PID
    $process = Start-HiddenProcess -FilePath $PythonPath -WorkingDirectory $SideRoot `
        -ArgumentList @('-u', $agent, '--host', $endpoint.HostName, '--port', [string]$endpoint.Port) `
        -RedirectStandardOutput (Join-Path $outDir "metrics-agent.$tag.stdout.log") `
        -RedirectStandardError (Join-Path $outDir "metrics-agent.$tag.stderr.log")
    return [pscustomobject]@{ Process = $process; Status = "started pid=$($process.Id) metricsPort=$($endpoint.Port)" }
}

function Test-PythonModules {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$Modules = @()
    )

    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    if ($Modules.Count -eq 0) {
        return $true
    }

    & $Path -c "import importlib.util,sys;sys.exit(0 if all(importlib.util.find_spec(name) is not None for name in sys.argv[1:]) else 1)" @Modules 2>$null
    return $LASTEXITCODE -eq 0
}

function Find-Python {
    $requiresTimeAudit = (
        -not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DSN) -or
        (-not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_PASSWORD) -and -not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_USER))
    )
    $requiredModules = if ($requiresTimeAudit) { @("psutil", "asyncpg") } else { @() }
    $command = Get-Command python -ErrorAction SilentlyContinue
    $commandPath = if ($command) { $command.Source } else { $null }
    $candidates = if ($requiresTimeAudit) {
        @(
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe"),
            $commandPath,
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python314\python.exe"),
            (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\python.exe")
        )
    } else {
        @(
            $commandPath,
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python314\python.exe"),
            (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\python.exe")
        )
    }

    foreach ($candidate in @($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        if (Test-PythonModules -Path $candidate -Modules $requiredModules) {
            return $candidate
        }
    }

    if ($requiresTimeAudit) {
        throw "No Python interpreter with required modules psutil+asyncpg was found for TimeAudit FPS."
    }
    throw "python.exe not found"
}
