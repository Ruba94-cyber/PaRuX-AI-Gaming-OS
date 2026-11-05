$ErrorActionPreference='Continue'
Set-Location C:\Users\paolo\Desktop\PaRuX
$env:PARUX_ACCEPT_WIX7_EULA='1'
$log='.ai/logs/rc'; New-Item -ItemType Directory -Force $log | Out-Null
foreach ($s in @(
  @{n='msi'; c={ & .\scripts\build-msi.ps1 }},
  @{n='portable'; c={ & .\scripts\build-portable.ps1 -Smoke }},
  @{n='corporate'; c={ & .\scripts\build-corporate-lite.ps1 -Smoke }})) {
  $t=Get-Date
  & $s.c *>&1 | Out-File "$log\$($s.n).log" -Encoding utf8
  "$($s.n) exit=$LASTEXITCODE secs=$([int]((Get-Date)-$t).TotalSeconds)" | Add-Content "$log\summary.txt"
}
'DONE' | Add-Content "$log\summary.txt"
