$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$svg = Join-Path $root "design\dashboard-final-candidate.svg"
$png = Join-Path $root "design\dashboard-final-candidate.png"

if (!(Test-Path -LiteralPath $svg)) {
    throw "Missing SVG: $svg"
}

[xml](Get-Content -Raw -Encoding UTF8 -LiteralPath $svg) | Out-Null

$gpuCore = "GPU " + [char]0x6838 + [char]0x5FC3
$fpsTitle = "FPS / " + [char]0x5E27 + [char]0x7387
$diskUsage = [string]([char]0x4F7F) + [char]0x7528 + [char]0x7387
$systemOk = [string]([char]0x7CFB) + [char]0x7EDF + [char]0x6B63 + [char]0x5E38
$foreground = [string]([char]0x524D) + [char]0x53F0 + " game.exe"
$updated = [string]([char]0x66F4) + [char]0x65B0 + " 1s"

$requiredText = @(
    $gpuCore,
    $fpsTitle,
    $diskUsage,
    $systemOk,
    $foreground,
    $updated
)

$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $svg
foreach ($text in $requiredText) {
    if ($source -notlike "*$text*") {
        throw "SVG missing required text: $text"
    }
}

$chromeCandidates = @(
    "C:\Program Files\Google\Chrome\Application\chrome.exe",
    "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    "C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
)

$browser = $chromeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($browser) {
    $svgUrl = "file:///" + ($svg -replace "\\", "/")
    & $browser --headless=new --disable-gpu --hide-scrollbars "--screenshot=$png" --window-size=480,1920 $svgUrl | Out-Null
    if (!(Test-Path -LiteralPath $png)) {
        throw "Failed to render PNG: $png"
    }
}

"Final design OK: $svg"
