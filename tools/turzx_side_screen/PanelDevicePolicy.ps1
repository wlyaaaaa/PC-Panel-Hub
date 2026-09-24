# Local configuration and live device checks shared by the public launchers.
function Get-TurzxConfiguredPort {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [AllowEmptyString()][string]$Port = ''
    )
    if ([string]::IsNullOrWhiteSpace($Port)) {
        $configPath = Join-Path $Root 'tools\turzx_side_screen\config.json'
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            $config = Get-Content -LiteralPath $configPath -Encoding UTF8 -Raw | ConvertFrom-Json
            if ($null -ne $config.PSObject.Properties['serial'] -and
                $null -ne $config.serial -and
                $null -ne $config.serial.PSObject.Properties['port']) {
                $Port = [string]$config.serial.port
                if ([string]::IsNullOrWhiteSpace($Port)) { throw 'Configured serial.port must not be empty.' }
            }
        }
        if ([string]::IsNullOrWhiteSpace($Port)) { $Port = 'COM7' }
    }
    if ($Port -notmatch '^COM[1-9][0-9]*$') { throw 'Serial port must be a Windows COM name, for example COM7.' }
    return $Port.ToUpperInvariant()
}

function Get-TurzxVerifiedSerialEndpoint {
    param([Parameter(Mandatory = $true)][string]$Port)
    if ($Port -notmatch '^COM[1-9][0-9]*$') { throw 'Invalid TURZX COM port.' }
    $serial = @(Get-CimInstance Win32_SerialPort -ErrorAction Stop |
        Where-Object { [string]$_.DeviceID -ieq $Port })
    if ($serial.Count -ne 1) {
        throw "Expected exactly one TURZX serial endpoint on $Port; found $($serial.Count). Check serial.port or -Port."
    }
    $instanceId = [string]$serial[0].PNPDeviceID
    if ($instanceId -notmatch '(?i)^USB\\VID_0525&PID_A4A7\\[^\\]+$') {
        throw "Refusing non-TURZX serial endpoint on $Port (expected VID_0525&PID_A4A7)."
    }
    $device = Get-PnpDevice -InstanceId $instanceId -ErrorAction Stop
    if ($null -eq $device -or $device.Present -ne $true -or
        [string]$device.Status -cne 'OK' -or $null -eq $device.ConfigManagerErrorCode -or
        [int]$device.ConfigManagerErrorCode -ne 0) {
        throw "TURZX serial endpoint on $Port is absent or unhealthy. No serial write was attempted."
    }
    return [pscustomobject]@{ Port = [string]$serial[0].DeviceID; InstanceId = $instanceId }
}

function Assert-TurzxStreamStopped {
    $owners = @(Get-Process 'TURZX.SideScreen.Stream*' -ErrorAction SilentlyContinue)
    if ($owners.Count -gt 0) { throw 'Stop the existing TURZX frame stream before opening its serial port.' }
}
