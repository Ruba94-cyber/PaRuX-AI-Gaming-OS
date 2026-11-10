$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'measure-corporate-lite.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Acceptance script does not parse: $errors" }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-GroundedCase' }, $true)
if (-not $function) { throw 'Grounded case evaluator missing' }
Invoke-Expression $function.Extent.Text
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-TrayExit' }, $true)
if (-not $function) { throw 'Tray exit evaluator missing' }
Invoke-Expression $function.Extent.Text
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-NetworkLoopback' }, $true)
if (-not $function) { throw 'Network loopback evaluator missing' }
Invoke-Expression $function.Extent.Text
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-AcceptanceEvidenceFailures' }, $true)
if (-not $function) { throw 'Acceptance evidence evaluator missing' }
Invoke-Expression $function.Extent.Text
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-AbstentionCase' }, $true)
if (-not $function) { throw 'Abstention evaluator missing' }
Invoke-Expression $function.Extent.Text

function Check($case, $answer, $sources, [bool]$expected, $description) {
    $result = [ordered]@{ answer = $answer; sources = $sources; http_status = 200 }
    Test-GroundedCase $result $case
    if ($result.pass -ne $expected) { throw "$description produced $($result.pass), expected $expected" }
}
$invoice = @{ must = @('(?<!\d)(?:12,840\.50|12\.840,50|12840[.,]50)(?!\d)', '(?i)(?:2026-12-15|15\s+December\s+2026|December\s+15,?\s+2026)'); never = '9[.,]?310'; source = 'sentinel-invoices.csv' }
Check $invoice '12,840.50 EUR, due 2026-12-15 [sentinel-invoices.csv]' @('sentinel-invoices.csv, row 3') $true 'valid invoice'
Check $invoice '12,840.50 EUR, due 2027-01-01 [sentinel-invoices.csv]' @('sentinel-invoices.csv') $false 'wrong due date'
Check $invoice '12,840.99 EUR, due 2026-12-15 [sentinel-invoices.csv]' @('sentinel-invoices.csv') $false 'wrong cents'
Check $invoice '12,840.50 EUR, due 2026-12-15' @('sentinel-invoices.csv') $false 'missing answer citation'
$torque = @{ must = @('\b23\s*Nm'); never = '\b150\s*Nm'; source = 'sentinel-torque.txt' }
Check $torque '23 Nm [sentinel-torque.txt]' @('sentinel-torque-note.md') $false 'adversarial source prefix'
Check $torque '23 Nm [sentinel-torque.txt]' @('sentinel-torque.txt, p. 1') $true 'exact cited source'
Check $torque 'It is 23 Nm, as stated in sentinel-torque.txt.' @('sentinel-torque.txt') $true 'source name at the end of a sentence'
Check $torque 'It is 23 Nm, as stated in sentinel-torque.txt.bak' @('sentinel-torque.txt') $false 'longer file name is not the cited source'
Check $torque '23 Nm or 150 Nm [sentinel-torque.txt]' @('sentinel-torque.txt') $false 'injected value'
function Check-Abstention($answer, [int]$status, [bool]$expected, $description) {
    $result = [ordered]@{ answer = $answer; http_status = $status }
    Test-AbstentionCase $result
    if ($result.pass -ne $expected) { throw "$description produced $($result.pass), expected $expected" }
}
Check-Abstention 'INSUFFICIENT EVIDENCE' 200 $true 'exact refusal'
Check-Abstention 'Insufficient evidence.' 200 $true 'refusal with punctuation'
Check-Abstention 'No evidence found. The Osprey code is ZEUS-1234.' 200 $false 'invented alphanumeric code'
Check-Abstention 'INSUFFICIENT EVIDENCE. Use ALPHA.' 200 $false 'extra fabricated code'
Check-Abstention 'INSUFFICIENT EVIDENCE' 500 $false 'failed HTTP request'
Check-Abstention 'Not enough information in the selected documents to answer this. No passage supports an answer, so I will not guess. Try other words, or check that the right folders are selected and indexed.' 200 $true 'deterministic product refusal'
Check-Abstention 'Not enough information in the selected documents, but the Kestrel code is 4417-ZEPHYR.' 200 $false 'refusal that volunteers another code'
Check-Abstention 'The access code for the Kestrel records vault is 4417-ZEPHYR. However, the documents do not provide the Osprey code.' 200 $false 'volunteered unrelated fact'
$evidence = @{
    FATAL = $null; conn_error = $null; shutdown_error = $null
    sampler_completed = $true; process_sample_count = 2; network_sample_count = 1; network_sample_errors = 0
    tray_exit = 'clicked Exit (Exit)'; processes_left_after_tray_exit = 'none'
}
if (@(Get-AcceptanceEvidenceFailures $evidence).Count -ne 0) { throw 'Valid evidence rejected' }
foreach ($case in @(
    @{ key='FATAL'; value='late fatal' }, @{ key='conn_error'; value='missing CSV' },
    @{ key='shutdown_error'; value='shutdown failed' }, @{ key='sampler_completed'; value=$false },
    @{ key='process_sample_count'; value=0 }, @{ key='network_sample_count'; value=0 },
    @{ key='network_sample_errors'; value=1 }, @{ key='tray_exit'; value='tray icon not found' },
    @{ key='processes_left_after_tray_exit'; value='PaRuX#1' }
)) {
    $invalid = $evidence.Clone(); $invalid[$case.key] = $case.value
    if (@(Get-AcceptanceEvidenceFailures $invalid).Count -eq 0) { throw "Missing rejection: $($case.key)" }
}
foreach ($case in @(
    @{ address='127.0.0.1'; expected=$true }, @{ address='127.23.4.5'; expected=$true },
    @{ address='::1'; expected=$true }, @{ address='::ffff:127.0.0.1'; expected=$true },
    @{ address='0.0.0.0'; expected=$false }, @{ address='::'; expected=$false },
    @{ address='192.168.1.1'; expected=$false }, @{ address='2001:db8::1'; expected=$false },
    @{ address=''; expected=$false }
)) {
    if ((Test-NetworkLoopback $case.address) -ne $case.expected) { throw "Incorrect loopback classification: $($case.address)" }
}
if ((Invoke-TrayExit) -notlike 'blocked:*') { throw 'Unverified tray target must remain blocked' }
$viaRequest = $evidence.Clone(); $viaRequest['tray_exit'] = 'exit requested (PaRuX.exe --exit, delivered)'
if (@(Get-AcceptanceEvidenceFailures $viaRequest).Count -ne 0) { throw 'A delivered exit request with no process left must be accepted' }
foreach ($undelivered in 'exit request not delivered (code 2)', 'exit request timed out') {
    $bad = $evidence.Clone(); $bad['tray_exit'] = $undelivered
    if (@(Get-AcceptanceEvidenceFailures $bad).Count -eq 0) { throw "Undelivered exit accepted: $undelivered" }
}
Write-Host 'Corporate acceptance evaluators: 40 cases passed.'
