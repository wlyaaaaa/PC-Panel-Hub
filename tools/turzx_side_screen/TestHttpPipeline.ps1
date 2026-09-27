Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$agent = Join-Path $root "metrics_agent.py"
$exe = Join-Path $root "out\TURZX.SideScreen.exe"
$out = Join-Path $root "out\side-screen-http-preview.png"

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "TestSideScreenApp.ps1") | Out-Host

$process = $null
$fixtureId = [Guid]::NewGuid().ToString('N')
$fixture = Join-Path $root "out\http-fixture-$fixtureId.py"
$ready = Join-Path $root "out\http-ready-$fixtureId.txt"
try {
    # Bind port 0 in the actual server, avoiding both production endpoints and
    # the race in selecting a free port and releasing it before process start.
    @'
import pathlib, sys
sys.path.insert(0, sys.argv[1])
import metrics_agent
server = metrics_agent.create_server("127.0.0.1", 0)
pathlib.Path(sys.argv[2]).write_text(str(server.server_address[1]), encoding="ascii")
server.serve_forever()
'@ | Set-Content -LiteralPath $fixture -Encoding UTF8
    $process = Start-Process -FilePath python -ArgumentList @(
        ('"{0}"' -f $fixture), ('"{0}"' -f $root), ('"{0}"' -f $ready)
    ) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($process.HasExited -or [DateTime]::UtcNow -ge $deadline) {
            throw 'Isolated HTTP fixture did not start.'
        }
        Start-Sleep -Milliseconds 100
    }
    $port = [int](Get-Content -LiteralPath $ready -Raw)

    & $exe --metrics-url "http://127.0.0.1:$port/snapshot" --timeout-ms 5000 --output $out
    if ($LASTEXITCODE -ne 0) {
        throw "SideScreen exe failed with exit code $LASTEXITCODE"
    }

    $preview = Get-Item -LiteralPath $out
    if ($preview.Length -le 0) {
        throw "HTTP preview file is empty: $out"
    }

    Write-Host ("OK {0} bytes -> {1}" -f $preview.Length, $preview.FullName)
}
finally {
    if ($process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    Remove-Item -LiteralPath $fixture, $ready -Force -ErrorAction SilentlyContinue
}
