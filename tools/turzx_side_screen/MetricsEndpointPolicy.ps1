# Shared by metrics startup and shutdown; no service, no socket retained.
param([switch]$RunTurzxMetricsRecoveryWorker, [int]$MetricsRecoveryParentId, [long]$MetricsRecoveryParentStartTicks)

function Test-MetricsProcessActive {
 param([Parameter(Mandatory=$true)][int]$ProcessId)
 if($ProcessId-le0){throw [ArgumentOutOfRangeException]::new('ProcessId')}
 $process=$null
 try {
  try { $process=[Diagnostics.Process]::GetProcessById($ProcessId) }
  catch [ArgumentException] { return $false }
  return -not $process.HasExited
 } finally { if($null-ne$process){$process.Dispose()} }
}

function Get-SnapshotProcess {
    param($Candidate)
    $process = Get-Process -Id $Candidate.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    $matchesSnapshot = $false
    try {
        $null = $process.Handle
        if ($process.HasExited) { return }
        # Win32_Process CreationDate truncates the 100 ns Windows timestamp to microseconds.
        $delta = $process.StartTime.ToUniversalTime() - $Candidate.CreationDate.ToUniversalTime()
        $matchesSnapshot = [Math]::Abs($delta.Ticks) -lt 10
        if ($matchesSnapshot) { return $process }
    }
    catch { if (-not $process.HasExited) { throw } }
    finally { if (-not $matchesSnapshot) { $process.Dispose() } }
}

# Match PowerShell's actual -File entrypoint before opening the process handle.
function Test-OwnedStreamParent {
    param($Candidate, [string]$SideRoot, [string[]]$EntryNames = @('StartVideoStream.ps1','StartSideScreenStack.ps1'))
    if ($null -eq $Candidate -or [string]$Candidate.Name -notin @('powershell.exe','pwsh.exe')) { return $false }
    $hostOptions = '(?:\s+-(?:NoLogo|NoProfile|NoExit|NonInteractive|Sta|Mta|ExecutionPolicy\s+\w+|WindowStyle\s+\w+))*'
    $fileArgument = [regex]::Match([string]$Candidate.CommandLine,
        '(?i)^(?:"[^"]+"|[^\s"]+)' + $hostOptions + '\s+-File\s+(?:"([^"]+)"|([^\s"]+))(?=\s|$)')
    if (-not $fileArgument.Success) { return $false }
    $path = if ($fileArgument.Groups[1].Success) { $fileArgument.Groups[1].Value } else { $fileArgument.Groups[2].Value }
    foreach ($name in $EntryNames) {
        if ([string]::Equals($path, (Join-Path $SideRoot $name), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Test-MetricsEndpointBind {
 param([ValidateRange(0,65535)][int]$Port=18765,[string]$HostName='127.0.0.1')
 $listener=$null
 $script:metricsPortProbeError=$null
 try {
  # Native exclusive bind is the availability authority; avoid unbounded CIM provider on stop deadline.
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

function Test-TurzxPythonScriptProcess {
    param([object]$Process, [string]$ScriptPath)
    if ($Process.Name -notlike 'python*') { return $false }
    $fullPath = [IO.Path]::GetFullPath($ScriptPath)
    $path = [regex]::Escape($fullPath)
    $barePath = if ($fullPath -notmatch '\s') { '|' + $path } else { '' }
    # The checkout's script must be Python's entry point, not an argument to another script.
    [string]$Process.CommandLine -match ('^(?:"[^"]+"|\S+)\s+(?:-u\s+)?(?:"' + $path + '"' + $barePath + ')(?:\s|$)')
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
        # A successful HTTP health response does not establish deployment identity.
        # In particular, an agent loaded from a deleted worktree can stay alive.
        $managed = $process.Count -eq 1 -and
            (Test-TurzxPythonScriptProcess -Process $process[0] -ScriptPath $expectedAgent) -and
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

    $outDir = (New-Item -ItemType Directory -Force -Path (Join-Path $SideRoot 'out') -ErrorAction Stop).FullName
    try {
        $lease = [IO.File]::Open((Join-Path $outDir 'metrics-maintenance.lock'), 'OpenOrCreate', 'Write', 'None')
    } catch [IO.IOException] {
        if (($_.Exception.GetBaseException().HResult -band 0xffff) -notin @(32, 33)) { throw }
        return [pscustomobject]@{ Process = $null; Status = 'blocked maintenance-in-progress' }
    }
    try { Invoke-TurzxMetricsAgentMaintenanceCore -SideRoot $SideRoot -OutDir $outDir -PythonPath $PythonPath -Restart:$Restart }
    finally { $lease.Dispose() }
}

function Invoke-TurzxMetricsAgentMaintenanceCore {
    param([string]$SideRoot, [string]$OutDir, [string]$PythonPath, [switch]$Restart)

    $endpoint = Get-TurzxMetricsEndpoint -ConfigPath (Join-Path $SideRoot 'config.json')
    $agent = Join-Path $SideRoot 'metrics_agent.py'
    $managed = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'python%'" -OperationTimeoutSec 8 -ErrorAction Stop |
        Where-Object { Test-TurzxPythonScriptProcess -Process $_ -ScriptPath $agent })
    if ($Restart) {
        foreach ($candidate in $managed) {
            $process = Get-SnapshotProcess $candidate
            if ($null -eq $process) { continue }
            try { $process.Kill() }
            catch { if (-not $process.HasExited) { throw } }
            finally { $process.Dispose() }
        }
        [void](Wait-ManagedMetricsAgentExitAndPortRelease -ProcessIds @($managed | ForEach-Object { [int]$_.ProcessId }) -Endpoint $endpoint)
    }
    $remaining = @($managed | Where-Object { Test-MetricsProcessActive -ProcessId ([int]$_.ProcessId) })
    if ($remaining.Count -gt 0 -or -not (Test-MetricsEndpointBind -Port $endpoint.Port -HostName $endpoint.HostName)) {
        $owners = @(Get-NetTCPConnection -State Listen -LocalPort $endpoint.Port -ErrorAction SilentlyContinue |
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
    if ('TURZX.SideScreen.WatchdogParentLiveness' -as [type]) { [TURZX.SideScreen.WatchdogParentLiveness]::EnsureParentAlive() }
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

if ($RunTurzxMetricsRecoveryWorker) {
    $ErrorActionPreference = 'Stop'
    try {
        . (Join-Path $PSScriptRoot 'HiddenProcessLauncher.ps1')
        [TURZX.SideScreen.WatchdogParentLiveness]::Start($MetricsRecoveryParentId, $MetricsRecoveryParentStartTicks)
        (Invoke-TurzxMetricsAgentMaintenance -Restart).Status
    }
    catch { 'deferred: {0}' -f $_.Exception.Message }
}
