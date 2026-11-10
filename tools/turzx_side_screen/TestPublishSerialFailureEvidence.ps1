$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot ('out\tests\publish-serial-' + [guid]::NewGuid().ToString('D'))
[IO.Directory]::CreateDirectory((Join-Path $fixture 'registries')) | Out-Null
$registryPath = Join-Path $fixture 'registries\pending_acceptance.json'
$bindingPath = Join-Path $fixture 'binding.json'
$evidencePath = Join-Path $fixture 'evidence.json'
$reviewPath = Join-Path $fixture 'review.ps1'
try {
    @'
param($Mode,$RepoRoot,$Id,$Who,$AskKind,$AttentionLevel,[switch]$ClearCheckpoint,$AiHint,[switch]$AppendNote,$Note,[switch]$SkipGitSync)
$path=Join-Path $RepoRoot 'registries\pending_acceptance.json'
$data=Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
$data.items[0].ai_hint.verification_method=$AiHint
$data.reviews++
$data | ConvertTo-Json -Depth 64 | Set-Content $path -Encoding utf8
'@ | Set-Content -LiteralPath $reviewPath -Encoding utf8
    $hint=@{sentinel='preserved';handoff_decision=@{choice='wait_evidence'};handoff_progress=@{evidence=@('earlier diagnosis')}}
    @{items=@(@{id='test-item';status='pending';ai_hint=@{verification_method=($hint | ConvertTo-Json -Depth 10 -Compress)}});reviews=0} |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $registryPath -Encoding utf8
    @{item_id='test-item';repo_root=$fixture;owner_entry=$reviewPath} |
        ConvertTo-Json | Set-Content -LiteralPath $bindingPath -Encoding utf8
    $record=@{schema='pcpanel.serial-native-failure.v1';session_id=[guid]::NewGuid().ToString('D');recorded_utc='2026-11-10T04:00:00Z';
        exceptions=@(@{type='System.IO.IOException';hresult=-2147024858;native_error=38;native_site='RJCP.IO.Ports.Native.Windows.CommOverlappedIo.DoReadEvent';native_il_offset=379;thread_exited=$true})}
    function Publish-TestRecord {
        $record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding utf8
        & (Join-Path $PSScriptRoot 'PublishSerialFailureEvidence.ps1') -EvidencePath $evidencePath -OwnerConfigPath $bindingPath -NotBefore '2026-11-10T03:00:00Z'
    }
    Publish-TestRecord
    $result=Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -AsHashtable
    $actual=$result.items[0].ai_hint.verification_method | ConvertFrom-Json -AsHashtable
    if ($result.reviews -ne 1 -or $actual.sentinel -cne 'preserved' -or $actual.handoff_decision.choice -cne 'wait_evidence' -or $actual.handoff_progress.evidence.Count -ne 2) { throw 'new evidence or preserved fields failed' }
    $record.session_id=[guid]::NewGuid().ToString('D'); $record.recorded_utc='2026-11-10T05:00:00Z'; $record.process_id=999
    $record.exceptions[0].thread_exited=$false
    Publish-TestRecord
    $result=Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -AsHashtable
    if ($result.reviews -ne 1) { throw 'identity/time changes caused duplicate Review' }
    $record.exceptions[0].native_il_offset=399
    Publish-TestRecord
    $result=Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -AsHashtable
    if ($result.reviews -ne 2) { throw 'new native call site on a still-alive thread was not published' }
    $record.exceptions[0].native_il_offset=409; $record.recorded_utc='2026-11-10T02:00:00Z'
    Publish-TestRecord
    $result=Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -AsHashtable
    if ($result.reviews -ne 2) { throw ('old process evidence was published; reviews=' + $result.reviews + '; recorded=' + $record.recorded_utc) }
    'PASS serial evidence publication: new code/site, duplicate identities, stale record, preserved hint'
}
finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    $testRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'out\tests')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($testRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'fixture outside test output' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
