Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$testRoot = Join-Path $scriptDir ("out\tests\video-stream-{0}" -f $PID)
$testsRoot = Join-Path $scriptDir "out\tests"
$previewDir = Join-Path $testRoot "preview"
$exePath = Join-Path $testRoot "TURZX.SideScreen.Stream.Test.exe"

New-Item -ItemType Directory -Force -Path $testsRoot | Out-Null
Get-ChildItem -LiteralPath $testsRoot -Filter "video-stream-*" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-1) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $previewDir | Out-Null
try {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir "StartVideoStream.ps1") `
        -Sample `
        -DryRun `
        -Diff `
        -Frames 2 `
        -IntervalMs 10 `
        -PreviewIntervalSeconds 45 `
        -PreviewDir $previewDir `
        -ExecutablePath $exePath | Out-Host

    $preview = Join-Path $previewDir "stream-last.png"
    if (!(Test-Path -LiteralPath $preview)) {
        throw "Missing stream preview: $preview"
    }

    $item = Get-Item -LiteralPath $preview
    if ($item.Length -le 0) {
        throw "Stream preview is empty: $preview"
    }

    $heartbeat = Join-Path $previewDir "stream-heartbeat.json"
    if (!(Test-Path -LiteralPath $heartbeat)) {
        throw "Missing stream heartbeat: $heartbeat"
    }

    $heartbeatJson = Get-Content -Raw -LiteralPath $heartbeat | ConvertFrom-Json
    if ($heartbeatJson.status -ne "ok") {
        throw "Stream heartbeat status was not ok: $($heartbeatJson.status)"
    }
    if ([int]$heartbeatJson.frame -lt 2) {
        throw "Stream heartbeat did not reach the dry-run frame count: $($heartbeatJson.frame)"
    }
    if ([int]$heartbeatJson.failed -ne 0) {
        throw "Stream heartbeat reported failures: $($heartbeatJson.failed)"
    }

    Write-Host ("OK isolated preview {0} bytes -> {1}" -f $item.Length, $item.FullName)
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
