# Read-only comparison; never mutates devices. IDs are discovered, not machine-specific defaults.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HS2ActiveRecoveryPolicy.ps1')
$old = Get-HS2UsbRecoverySnapshot -UsePnp
# Force the new path here: a fallback would make this comparison meaningless.
$nativeType = Initialize-HS2DeviceNativeMethods
$native = @($nativeType::GetPresentDevices([string[]]@(
    'USB\VID_1A86&PID_8091\*', 'USB\VID_0416&PID_8051\*',
    'USB\VID_1A86&PID_AD23&MI_00\*', 'USB\VID_1A86&PID_AD23\*',
    'USB\VID_1CBE&PID_A068\*', 'USB\VID_A108&PID_EAEF\*', 'USB\VID_0000&PID_0002\*')))
$fields = @('InstanceId','ParentInstanceId','ProblemCode','Present','Status','LocationInfo')
$ids = @(@($old.Devices.InstanceId) + @($native.InstanceId) | Sort-Object -Unique)
$rows = @(foreach ($id in $ids) {
    $o = @($old.Devices | Where-Object { $_.InstanceId -ieq $id })
    $n = @($native | Where-Object { $_.InstanceId -ieq $id })
    $mismatches = @()
    if ($o.Count -ne 1 -or $n.Count -ne 1) { $mismatches += 'device-count' }
    else {
        foreach ($field in $fields) {
            # Existing identity comparisons are case-insensitive; keep both raw values below.
            $equal = if ($field -in @('InstanceId','ParentInstanceId')) {
                [string]$o[0].$field -ieq [string]$n[0].$field
            } else { [string]$o[0].$field -ceq [string]$n[0].$field }
            if (-not $equal) { $mismatches += $field }
        }
    }
    [pscustomobject]@{InstanceId=$id;MismatchFields=$mismatches;Old=$o;Native=$n}
})
$count = @($rows | Where-Object { $_.MismatchFields.Count -gt 0 }).Count
[pscustomobject]@{ReadOnly=$true;Compared=$rows.Count;Fields=$fields;MismatchCount=$count;Rows=$rows} |
    ConvertTo-Json -Depth 8
if ($count -gt 0) { throw "Native/PnP comparison has $count mismatches." }
