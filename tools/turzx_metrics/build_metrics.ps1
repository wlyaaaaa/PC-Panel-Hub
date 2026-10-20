param(
    [string]$OutputPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path "TURZX.Metrics.dll")
)

$ErrorActionPreference = "Stop"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Missing .NET Framework compiler: $csc"
}

$source = Join-Path $PSScriptRoot "TURZX.Metrics.cs"
& $csc /nologo /target:library /platform:anycpu /optimize+ /out:$OutputPath $source
if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE"
}

"Built $OutputPath"
