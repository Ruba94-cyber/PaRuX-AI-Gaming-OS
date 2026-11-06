Set-Location C:\Users\paolo\Desktop\PaRuX
$env:PARUX_ACCEPT_WIX7_EULA='1'; $a='C:\Users\paolo\Desktop\PaRuX\artifacts\corporate-lite\assets'
$log='.ai\logs\rc'; $t=Get-Date
& .\scripts\build-msi.ps1 *>&1 | Out-File "$log\msi2.log" -Encoding utf8; "msi2 exit=$LASTEXITCODE" | Add-Content "$log\summary.txt"
& .\scripts\build-portable.ps1 -Smoke *>&1 | Out-File "$log\portable2.log" -Encoding utf8; "portable2 exit=$LASTEXITCODE" | Add-Content "$log\summary.txt"
& .\scripts\build-corporate-lite.ps1 -SkipPublish -Smoke -ModelPath "$a\Qwen3-4B-Q4_K_M.gguf" -EmbeddingDir "$a\embeddings" -RuntimeDir "$a\llama-cpu" -RuntimeTag b12119 *>&1 | Out-File "$log\corporate4.log" -Encoding utf8; "corporate4 done" | Add-Content "$log\summary.txt"
'DONE2' | Add-Content "$log\summary.txt"
