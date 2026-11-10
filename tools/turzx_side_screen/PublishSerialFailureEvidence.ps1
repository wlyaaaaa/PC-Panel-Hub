# The local opt-in binding stays in ignored runtime output; no machine identity is shipped.
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$NotBefore,
    [string]$OwnerConfigPath = (Join-Path $PSScriptRoot 'out\serial-evidence-owner.json')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OwnerConfigPath) -or -not (Test-Path -LiteralPath $EvidencePath)) { return }
$binding = Get-Content -LiteralPath $OwnerConfigPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
$record = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
if ($record.schema -cne 'pcpanel.serial-native-failure.v1' -or
    [DateTimeOffset]::Parse($record.recorded_utc) -lt [DateTimeOffset]::Parse($NotBefore)) { return }
$signatures = @($record.exceptions | ForEach-Object {
    # Identity and observation times remain in the evidence file, never the progress fingerprint.
    '{0}|{1}|{2}|{3}|IL={4}' -f $_.type,$_.hresult,$_.native_error,$_.native_site,$_.native_il_offset
} | Sort-Object -Unique)
if (-not $signatures.Count) { return }
$registryPath = Join-Path $binding.repo_root 'registries\pending_acceptance.json'
$item = (Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String).items |
    Where-Object id -CEQ $binding.item_id
if (-not $item -or $item.status -cnotin @('pending','on_hold','failed')) { return }
$hint = $item.ai_hint.verification_method | ConvertFrom-Json -AsHashtable -DateKind String
$prefix = 'serial-native-v1: '
$new = @($signatures | ForEach-Object { $prefix + $_ } | Where-Object { $_ -cnotin @($hint.handoff_progress.evidence) })
if (-not $new.Count) { return }
$archive = Join-Path (Split-Path -Parent $EvidencePath) ('serial-native-' + [guid]::Parse($record.session_id).ToString('D') + '.json')
Copy-Item -LiteralPath $EvidencePath -Destination $archive
$hint.handoff_progress.evidence = @($hint.handoff_progress.evidence) + $new
# Review preserves the existing wait/decision and all other verification fields.
& $binding.owner_entry -Mode Review -RepoRoot $binding.repo_root -Id $binding.item_id -Who ai -AskKind ai `
    -AttentionLevel background -ClearCheckpoint -AiHint ($hint | ConvertTo-Json -Depth 64 -Compress) `
    -AppendNote -Note ("已捕获同一串口会话设备错误前的原始异常，实质证据已更新；只表示关联，尚未证明根因。原始证据：" + $archive) -SkipGitSync
if (-not $?) { throw 'serial evidence Review failed' }
