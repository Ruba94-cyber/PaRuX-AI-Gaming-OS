<#
.SYNOPSIS
  Repeatable source audit (L10N-01): lists string literals that contain Italian and are NOT
  routed through the Italian slot of a language switch, i.e. Italian text that an English-UI user could see.
.DESCRIPTION
  Scans src/PaRuX.App, PaRuX.Server, PaRuX.Cli, PaRuX.Contracts, PaRuX.Intelligence (*.cs, *.xaml).
  A literal is "Italian" if it has an accented Italian vowel or >= 2 common Italian UI words
  (or 1 distinctive word). It is EXCLUDED when it is:
    - the Italian slot of T("en","it") / DT / Tr / L(...) / Loc(...) style pair calls,
    - on a line that switches on the language (Italian / IsItalian / isIt / "it" ? : etc.), where Italian is intentional,
    - in a comment, a log call, an AutomationId / Name / key, or a file listed in $IntentionalFiles
      (bilingual INPUT understanding: routers, refusal heuristics, stop-word lists).
  Use -ListAll to print every hit; default prints per-file counts and a total.
.EXAMPLE
  pwsh tools/audit-ui-italian.ps1 -ListAll
#>
[CmdletBinding()]
param([string]$Root = "", [switch]$ListAll, [switch]$SelfTest, [int]$FailAbove = -1)
$ErrorActionPreference = "Stop"

$accent = '[àèéìòùÀÈÉÌÒÙ]'
$words = 'salva|annulla|apri|chiudi|impostazioni|elimina|cerca|errore|avvia|ferma|aggiungi|rimuovi|seleziona|giochi|gioco|modelli|modello|scarica|nessun|nessuna|nessuno|conferma|indietro|avanti|caricamento|riprova|della|delle|degli|dello|nella|nelle|sono|stato|stata|non|puoi|vuoi|verr[àa]|disponibil[ei]|trovat[oaie]|impossibile|attendi|attendere|completat[oa]|in corso|fallit[oa]|aggiorna|aggiornamento|cartella|file|chat|memoria|schermo|avviso|attivo|attiva|disattiv\w+|abilita\w*|installa\w*|scegli|incolla|copia|invia|sposta|rinomina|esporta|importa|usa|per|con|il|lo|la|le|gli|di|da|del|dei|un|una|che|più|già|ancora|oppure|prima|dopo'
$strong = 'salva|annulla|chiudi|impostazioni|elimina|cerca|errore|avvia|ferma|aggiungi|rimuovi|seleziona|giochi|scarica|nessun|nessuna|nessuno|conferma|indietro|avanti|caricamento|riprova|della|delle|degli|nella|nelle|sono|puoi|vuoi|verr[àa]|disponibil[ei]|trovat[oaie]|impossibile|attendi|completat[oa]|fallit[oa]|aggiornamento|cartella|abilita\w*|disattiv\w+|oppure|ancora|scegli|incolla|rinomina|esporta|importa|stato|stata'
$weak = 'non|per|con|il|lo|la|le|gli|di|da|del|dei|un|una|che|più|già|prima|dopo|usa|apri|copia|invia|sposta|attivo|attiva|modello|modelli|gioco|file|chat|memoria'

function Test-Italian([string]$s) {
    if ($s.Length -lt 3) { return $false }
    if ($s -cmatch $accent) { return $true }
    $t = $s.ToLowerInvariant()
    $strongHits = [regex]::Matches($t, "(?<![\p{L}\d])(?:$strong)(?![\p{L}\d])").Count
    if ($strongHits -ge 1) { return $true }
    $weakHits = [regex]::Matches($t, "(?<![\p{L}\d])(?:$weak)(?![\p{L}\d])").Count
    return ($weakHits -ge 3)
}

$IntentionalFiles = @(
  'RouterService','ComputerUseService','WebIntentDetector','RefusalHeuristic','LocalClockAnswer',
  'CodingVerificationCommandPolicy','StopWords','ItalianKeywords','IntentKeywords','UiLanguage'
)
$langSwitch = '\w*Italian\w*|\bisIt\b|\bIt\s*\?|\bit\s*:|TwoLetterISOLanguageName|\bUiLanguage\b|"it"\s*\?|\blang(uage)?\s*==|\bitalian\b'
$skipLine = '^\s*(//|\*|/\*)|Logger|\bLog(Debug|Info|Warn|Warning|Error|Trace)?\(|_log\.|Debug\.Write|Trace\.Write|Console\.Error|AutomationId|x:Name|\[Fact|\[Theory|InlineData|nameof\('

# Blank the Italian slot of an adjacent literal pair ("English", "Italiano") or ternary ? "English" : "Italiano",
# also across line breaks and for string concatenations inside the pair. The English slot must itself be non-Italian.
$litRx = '[$@]{0,2}"(?:\\.|[^"\\\r\n])*"'
$pairRx = [regex]("(?<en>$litRx)(?<sep>\s*[,:]\s*)(?<it>$litRx(?:\s*\+\s*$litRx)*)")
function Remove-ItalianSlots([string]$Text) {
    return $pairRx.Replace($Text, {
        param($m)
        $en = $m.Groups['en'].Value
        if (Test-Italian $en.Trim('"')) { return $m.Value }
        $it = $m.Groups['it'].Value
        return $en + $m.Groups['sep'].Value + ($it -replace '[^\r\n]', ' ')
    })
}

function Get-Hits([string]$Text, [string]$File) {
    $out = New-Object System.Collections.Generic.List[object]
    $Text = Remove-ItalianSlots $Text
    # XAML: loc:Loc.ItXxx="..." attributes are the Italian slot of the inline convention (English stays in the normal attribute).
    if ($File -like '*.xaml') { $Text = [regex]::Replace($Text, 'loc:Loc\.It\w+="[^"]*"', { param($m) $m.Value -replace '[^
]', ' ' }) }
    $n = 0
    $lines = $Text -split "`r?`n"
    foreach ($line in $lines) {
        $n++
        if ($line -match $skipLine) { continue }
        # language-switch context: this line or any of the previous 5 lines selects between EN and IT
        $from = [Math]::Max(0, $n - 7)
        $ctx = ($lines[$from..($n - 1)]) -join "`n"
        if ($ctx -match $langSwitch) { continue }
        foreach ($m in [regex]::Matches($line, '"((?:\\.|[^"\\])*)"')) {
            $v = $m.Groups[1].Value
            if (Test-Italian $v) { $out.Add([pscustomobject]@{ File = $File; Line = $n; Text = $v }) }
        }
    }
    return $out
}

if ($SelfTest) {
    $a = @(Get-Hits 'var x = "Impostazioni salvate";' 'x.cs'); $b = @(Get-Hits 'var x = T("Saved", "Salvato");' 'x.cs'); $c = @(Get-Hits 'var x = "Open the file";' 'x.cs')
    if ($a.Count -ne 1 -or $b.Count -ne 0 -or $c.Count -ne 0) { throw "[audit-ui-italian] self-test failed" }
    Write-Host "[audit-ui-italian] self-test passed"; return
}

if ([string]::IsNullOrWhiteSpace($Root)) { $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
$dirs = 'PaRuX.App','PaRuX.Server','PaRuX.Cli','PaRuX.Contracts','PaRuX.Intelligence' | ForEach-Object { Join-Path $Root "src/$_" } | Where-Object { Test-Path $_ }
$files = foreach ($d in $dirs) {
    Get-ChildItem -LiteralPath $d -Recurse -File -Include *.cs,*.xaml | Where-Object {
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and (-not ($IntentionalFiles | Where-Object { $_ -and $PSItem.Name -like "*$_*" }))
    }
}
$all = New-Object System.Collections.Generic.List[object]
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Root.Length + 1).Replace('\','/')
    foreach ($h in (Get-Hits ([IO.File]::ReadAllText($f.FullName)) $rel)) { $all.Add($h) }
}
if ($ListAll) { $all | ForEach-Object { "{0}:{1}: {2}" -f $_.File, $_.Line, $_.Text } }
$all | Group-Object File | Sort-Object Count -Descending | ForEach-Object { "{0,5}  {1}" -f $_.Count, $_.Name }
Write-Host "[audit-ui-italian] TOTAL Italian-bearing literals outside language switches: $($all.Count) in $(($all | Group-Object File).Count) files"
if ($FailAbove -ge 0 -and $all.Count -gt $FailAbove) { throw "[audit-ui-italian] $($all.Count) > $FailAbove" }

# ---- Measure 2: English-only residue in language-aware files -------------------------------------------------
# A "language-aware" file has an EN/IT helper (T/L/KT/TvT/StartItalian/...). Visible property assignments in such a
# file whose value is a bare English literal never switch to Italian: that is a mixed screen when Italian is selected.
# UI-test fixture strings ("UI test:", "UI fixture") and format-only literals are ignored.
$awareRx = 'static (bool|string) \w*(Italian|T|L|KT|TvT|DT)\b|string L\(string en|CultureInfo\.CurrentUICulture\.TwoLetterISOLanguageName'
$visRx = '\b(Text|Content|Header|Title|PlaceholderText|PrimaryButtonText|CloseButtonText|SecondaryButtonText)\s*=\s*\$?"(?<v>[A-Za-z][^"]*)"|SetName\([^,]+,\s*\$?"(?<v>[A-Za-z][^"]*)"|SetToolTip\([^,]+,\s*\$?"(?<v>[A-Za-z][^"]*)"|ShowTransientStatus\(\$?"(?<v>[A-Za-z][^"]*)"'
$eng = New-Object System.Collections.Generic.List[object]
foreach ($f in ($files | Where-Object { $_.Extension -eq '.cs' -and $_.FullName -match 'PaRuX\.App' })) {
    $txt = [IO.File]::ReadAllText($f.FullName)
    # a file is judged language-aware if it (or its partial-class siblings in the same folder with the same stem) has a helper
    $stem = ($f.BaseName -split '\.')[0]
    $sib = Get-ChildItem -LiteralPath $f.DirectoryName -Filter "$stem*.cs" -File | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }
    if (-not (($sib -join "`n") -match $awareRx)) { continue }
    $n = 0
    foreach ($line in ($txt -split "`r?`n")) {
        $n++
        if ($line -match $skipLine -or $line -match 'UI test:|UI fixture|Fixture') { continue }
        foreach ($m in [regex]::Matches($line, $visRx)) {
            $v = $m.Groups['v'].Value
            if ($v.Length -lt 3 -or $v -notmatch '[a-z]{3}') { continue }
            $eng.Add([pscustomobject]@{ File = $f.FullName.Substring($Root.Length + 1).Replace('\','/'); Line = $n; Text = $v })
        }
    }
}
if ($ListAll) { $eng | ForEach-Object { "EN-ONLY {0}:{1}: {2}" -f $_.File, $_.Line, $_.Text } }
$eng | Group-Object File | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object { "{0,5}  EN-only  {1}" -f $_.Count, $_.Name }
Write-Host "[audit-ui-italian] TOTAL English-only visible literals in language-aware files: $($eng.Count) in $(($eng | Group-Object File).Count) files"


# ---- Measure 3: English-only visible attributes in XAML ------------------------------------------------------
# A visible attribute (Text/Content/Header/...) with English words and no matching loc:Loc.ItXxx attribute on the same element.
# Values that are identical in both languages (product/technology names, units, single shared words, URLs, model ids) are ignored.
$xamlMap = [ordered]@{ 'Text'='ItText'; 'Content'='ItContent'; 'Header'='ItHeader'; 'PlaceholderText'='ItPlaceholder'; 'Title'='ItTitle'; 'OnContent'='ItOnContent'; 'OffContent'='ItOffContent'; 'ToolTipService.ToolTip'='ItToolTip'; 'AutomationProperties.Name'='ItName'; 'AutomationProperties.HelpText'='ItHelpText' }
$sameInBoth = '^(PaRuX.*|Chat|Gaming|Auto|AUTO|Standard|Incognito|Autopilot|Provider|Trigger|Dataset|Runtime|FPS|GAMING|INTERNET|PERSONAS|AVATAR STUDIO|GPU / VRAM|VS Code|OpenCode|English|Italiano|Model Hub|Lifecycle Lab|Console / TV|AI \(Ctrl\+1\)|Gaming \(Ctrl\+2\)|parux-\w+|\* \(default\)|https?://.*|[\w./-]+/[\w.-]+)$'
$xl = New-Object System.Collections.Generic.List[object]
foreach ($f in (Get-ChildItem -LiteralPath (Join-Path $Root 'src/PaRuX.App') -Recurse -File -Filter *.xaml | Where-Object { $_.FullName -notmatch '[\/](obj|bin)[\/]' })) {
    $t = [IO.File]::ReadAllText($f.FullName)
    foreach ($em in [regex]::Matches($t, '<[A-Za-z][^<>]*?>', 'Singleline')) {
        $el = $em.Value; $ln = ($t.Substring(0, $em.Index) -split "`n").Count
        foreach ($k in $xamlMap.Keys) {
            foreach ($am in [regex]::Matches($el, '(?<![\w.])' + [regex]::Escape($k) + '="([^"{][^"]*)"')) {
                $v = $am.Groups[1].Value
                if ($v -notmatch '[A-Za-z]{3}' -or $v -match $sameInBoth) { continue }
                if ($el -match ('loc:Loc\.' + $xamlMap[$k] + '=')) { continue }
                $xl.Add([pscustomobject]@{ File = $f.FullName.Substring($Root.Length + 1).Replace('\','/'); Line = $ln; Text = "$k=$v" })
            }
        }
    }
}
if ($ListAll) { $xl | ForEach-Object { "XAML-EN-ONLY {0}:{1}: {2}" -f $_.File, $_.Line, $_.Text } }
Write-Host "[audit-ui-italian] TOTAL English-only visible XAML attributes without loc:Loc.It*: $($xl.Count)"
