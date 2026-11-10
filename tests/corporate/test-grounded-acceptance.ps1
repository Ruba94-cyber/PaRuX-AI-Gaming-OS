$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'measure-corporate-lite.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Acceptance script does not parse: $errors" }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-GroundedCase' }, $true)
if (-not $function) { throw 'Grounded case evaluator missing' }
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
Write-Host 'Corporate acceptance evaluators: 12 cases passed.'
