param([string]$Filter)
Set-Location C:\Users\paolo\Desktop\PaRuX
$layout = "artifacts\validation\ui-tests\$(Get-Date -Format yyyyMMdd-HHmmss)"; $app="$layout\app"
dotnet publish src\PaRuX.App\PaRuX.App.csproj -c Debug -r win-x64 --self-contained true -o $app -nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet publish src\PaRuX.Server\PaRuX.Server.csproj -c Debug -r win-x64 --self-contained true -o "$app\PaRuX.Server" -nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$env:PARUX_UI_APP_PATH = (Resolve-Path "$app\PaRuX.exe").Path
dotnet test tests\PaRuX.UITests\PaRuX.UITests.csproj -c Debug --filter $Filter
exit $LASTEXITCODE
