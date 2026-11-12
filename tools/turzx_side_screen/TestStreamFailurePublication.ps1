$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot ('out\tests\stream-failure-' + [guid]::NewGuid().ToString('D'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    foreach ($name in @('StartVideoStream.ps1', 'PanelDevicePolicy.ps1', 'MetricsEndpointPolicy.ps1',
        'HiddenProcessLauncher.ps1', 'HiddenProcessLauncher.cs', 'SnapshotModels.cs',
        'TURZX.SideScreen.Renderer.cs', 'TURZX.SideScreen.TurzxHelperSender.cs', 'TURZX.SideScreen.Stream.cs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $fixture
    }
    @'
param($EvidencePath, $NotBefore)
@{evidence=$EvidencePath;not_before=$NotBefore} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'published.json')
'@ | Set-Content -LiteralPath (Join-Path $fixture 'PublishSerialFailureEvidence.ps1') -Encoding utf8
    @'
param($Fixture)
$ErrorActionPreference = 'Stop'
try {
    & (Join-Path $Fixture 'StartVideoStream.ps1') -Root $Fixture -Sample -DryRun -Frames -1 `
        -PreviewDir (Join-Path $Fixture 'preview') -ExecutablePath (Join-Path $Fixture 'invalid-frames.exe') *>&1 |
        ForEach-Object { [string]$_ | Out-Null }
    throw 'Expected native argument failure'
} catch {
    $_.Exception.GetType().FullName | Set-Content (Join-Path $Fixture 'caught.txt')
}
'@ | Set-Content -LiteralPath (Join-Path $fixture 'runner.ps1') -Encoding utf8
    & powershell -NoProfile -File (Join-Path $fixture 'runner.ps1') -Fixture $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell runner failed' }
    $caught = Get-Content -LiteralPath (Join-Path $fixture 'caught.txt') -Raw
    if ($caught.Trim() -cne 'System.Management.Automation.RemoteException') { throw "Original native failure was lost: $caught" }
    $published = Get-Content -LiteralPath (Join-Path $fixture 'published.json') -Raw | ConvertFrom-Json
    if ($published.evidence -ne (Join-Path $fixture 'preview\serial-native-failure.json') -or
        -not [DateTimeOffset]::Parse($published.not_before)) { throw 'Failure evidence arguments were lost' }
    'PASS Windows PowerShell Stop + merged stderr: original failure and evidence publication both retained'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'out\tests')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture outside test output' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
