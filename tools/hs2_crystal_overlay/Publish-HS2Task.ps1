[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$')]
    [string]$Id,

    [Parameter(Mandatory)]
    [string]$Title,

    [string]$Detail,

    [ValidateRange(0, 100)]
    [double]$ProgressPercent,

    [ValidateRange(0, 525600)]
    [double]$RemainingMinutes,

    [ValidateSet('active', 'completed', 'cancelled')]
    [string]$State = 'active'
)

$payload = [ordered]@{
    id = $Id
    title = $Title
    state = $State
}
if ($PSBoundParameters.ContainsKey('Detail')) {
    $payload.detail = $Detail
}
if ($PSBoundParameters.ContainsKey('ProgressPercent')) {
    $payload.progress = $ProgressPercent / 100
}
if ($PSBoundParameters.ContainsKey('RemainingMinutes')) {
    $payload.remaining_seconds = $RemainingMinutes * 60
}

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    '.',
    'HS2.CrystalOverlay.Tasks',
    [System.IO.Pipes.PipeDirection]::Out
)
try {
    $pipe.Connect(2000)
    $writer = [System.IO.StreamWriter]::new(
        $pipe,
        [System.Text.UTF8Encoding]::new($false),
        4096,
        $true
    )
    try {
        $writer.WriteLine(($payload | ConvertTo-Json -Compress))
        $writer.Flush()
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $pipe.Dispose()
}
