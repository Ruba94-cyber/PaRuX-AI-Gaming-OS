<#
.SYNOPSIS
    Measures PaRuX Corporate Lite Offline on this PC with ~300 synthetic mixed-format documents and writes an honest results file.

.DESCRIPTION
    Extracts the built ZIP into a fresh temp dir (nothing is run from the repo), starts it like "Start PaRuX Corporate Lite.cmd"
    (PARUX_CORPORATE=1, PARUX_DATA_ROOT inside the package), generates synthetic documents (neutral invented content), registers
    the folder through the Core API, indexes it and measures: index time, working set + CPU per process (PaRuX.exe, PaRuX.Server.exe,
    llama-server), exact-id and semantic/hybrid search latency, pause/resume, a chat question through the Core with the bundled
    Qwen3-1.7B (time-to-first-token, tokens/s), non-loopback connections and clean shutdown. Also: non-writable folder,
    corrupt/empty files and files deleted while indexing.

    Synthetic data only (public repo). The results file goes under .ai/artifacts/overnight/ (not committed).
#>
[CmdletBinding()]
param(
    [string]$ZipPath = "",
    [string]$ResultsDir = "",
    [int]$DocCount = 300,
    [int]$StartTimeoutSeconds = 180,
    [int]$IndexTimeoutSeconds = 900,
    [int]$ChatTimeoutSeconds = 420,
    [switch]$KeepWorkDir,
    [switch]$EnforceAcceptance
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem, System.Net.Http

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ZipPath) {
    # The ZIP is a build artifact (not in Git); look in this checkout, then in the main checkout of the same repository.
    $cands = @((Join-Path $repoRoot "artifacts\corporate-lite\PaRuX-Corporate-Lite-Offline-win-x64.zip"))
    $common = (& git -C $repoRoot rev-parse --git-common-dir 2>$null)
    if ($common) { $cands += (Join-Path (Split-Path -Parent (Resolve-Path $common).Path) "artifacts\corporate-lite\PaRuX-Corporate-Lite-Offline-win-x64.zip") }
    $ZipPath = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ZipPath -or -not (Test-Path $ZipPath)) { throw "Corporate Lite ZIP not found. Build it with scripts/build-corporate-lite.ps1 or pass -ZipPath." }
if (-not $ResultsDir) { $ResultsDir = Join-Path $repoRoot ".ai\artifacts\overnight" }
New-Item -ItemType Directory -Force $ResultsDir | Out-Null

$work = Join-Path ([IO.Path]::GetTempPath()) ("corp-measure-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
$extract = Join-Path $work "pkg"; $docsRoot = Join-Path $work "docs-corpus"
$log = New-Object System.Collections.Generic.List[string]
$R = [ordered]@{}   # results
function Say($m) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Write-Host $line; $log.Add($line) }
$cores = [Environment]::ProcessorCount
$totalRamGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
$cpuName = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name

# ---------------------------------------------------------------------------------------------------------------------------
# Synthetic corpus
# ---------------------------------------------------------------------------------------------------------------------------
$topics = @(
    @{ n = "warehouse pallet rotation"; t = "Pallets in the east warehouse are rotated every fourteen days so that the oldest stock leaves first. The shift lead records each rotation on the shared log and flags damaged pallets for repair." },
    @{ n = "meeting room booking"; t = "Meeting rooms on the second floor can be booked up to thirty days ahead. Cancel at least one hour before the start so that the room is released for other teams." },
    @{ n = "travel expense reimbursement"; t = "Travel expenses are reimbursed after the receipts are uploaded. Train tickets in standard class and economy flights are covered; hotel nights are capped by the city tier." },
    @{ n = "visitor badge procedure"; t = "Visitors collect a printed badge at the front desk and must be escorted by a host. Badges are returned at the end of the day and the register is closed by the receptionist." },
    @{ n = "laptop replacement cycle"; t = "Laptops are replaced after four years of service. The old device is wiped by the support team and the asset register is updated with the new serial number." },
    @{ n = "quarterly safety drill"; t = "A fire evacuation drill runs every quarter. Floor wardens check the stairwells, count the people at the assembly point and report the total to facilities." },
    @{ n = "supplier onboarding checklist"; t = "A new supplier submits the company profile, the bank confirmation letter and the quality certificate. Purchasing verifies the documents and creates the supplier record." },
    @{ n = "customer complaint handling"; t = "A complaint is logged within one working day, assigned to an owner and answered within five days. Repeated complaints on the same product trigger a quality review." },
    @{ n = "backup and restore routine"; t = "Nightly backups of the file server are kept for thirty days. A restore test is performed monthly and the result is noted in the operations journal." },
    @{ n = "remote work guideline"; t = "Remote work is allowed up to three days per week by agreement with the team lead. Core hours are ten to four and the calendar must show availability." },
    @{ n = "inventory count schedule"; t = "A full inventory count happens in January and July. Cycle counts on fast-moving items are done weekly, with differences above two percent investigated." },
    @{ n = "maintenance request workflow"; t = "Building maintenance requests are opened through the facilities form, ranked by urgency and closed only after the requester confirms the repair." },
    @{ n = "training catalogue"; t = "The training catalogue lists safety, software and language courses. Employees agree the plan with their manager and book seats through the learning desk." },
    @{ n = "document retention rules"; t = "Invoices are kept for ten years, delivery notes for five and meeting notes for two. Archives are reviewed every year and expired items are destroyed under supervision." },
    @{ n = "printer and scanner policy"; t = "Shared printers use badge release. Colour printing needs a cost centre code and scanned files are sent to the personal folder of the requesting employee." }
)
$words = "alpha bravo cedar delta ember fjord garnet harbor indigo juniper kestrel lantern meadow nectar orchid pewter quartz russet saffron timber umber velvet willow xenon yarrow zephyr".Split(' ')
$rng = [Random]::new(20260605)
function New-Id($i) { "PRJ-{0:D5}-{1}{2}" -f (10000 + $i * 7), [char](65 + ($i % 26)), [char](65 + (($i * 3) % 26)) }
function Para($i, $k) {
    $t = $topics[($i + $k) % $topics.Count]
    $filler = (1..30 | ForEach-Object { $words[$rng.Next($words.Count)] }) -join ' '
    "$($t.t) Reference note $k for topic '$($t.n)': $filler."
}
function Xml-Esc($s) { [Security.SecurityElement]::Escape($s) }
function New-Zip($path, [hashtable]$entries) {
    $fs = [IO.File]::Open($path, [IO.FileMode]::Create)
    $z = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($k in $entries.Keys) {
        $e = $z.CreateEntry($k); $s = $e.Open(); $b = [Text.Encoding]::UTF8.GetBytes([string]$entries[$k]); $s.Write($b, 0, $b.Length); $s.Dispose()
    }
    $z.Dispose(); $fs.Dispose()
}
function New-Docx($path, $title, $paras) {
    $body = ("<w:p><w:r><w:t>{0}</w:t></w:r></w:p>" -f (Xml-Esc $title)) + (($paras | ForEach-Object { "<w:p><w:r><w:t xml:space=`"preserve`">{0}</w:t></w:r></w:p>" -f (Xml-Esc $_) }) -join '')
    New-Zip $path @{
        '[Content_Types].xml' = '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
        '_rels/.rels' = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
        'word/document.xml' = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>' + $body + '</w:body></w:document>'
    }
}
function New-Xlsx($path, $rows) {
    $strings = New-Object System.Collections.Generic.List[string]
    $sheetRows = for ($r = 0; $r -lt $rows.Count; $r++) {
        $cells = for ($c = 0; $c -lt $rows[$r].Count; $c++) {
            $ref = "{0}{1}" -f [char](65 + $c), ($r + 1)
            $v = $rows[$r][$c]
            if ($v -is [int]) { "<c r=`"$ref`"><v>$v</v></c>" } else { $strings.Add([string]$v); "<c r=`"$ref`" t=`"s`"><v>$($strings.Count - 1)</v></c>" }
        }
        "<row r=`"$($r + 1)`">$($cells -join '')</row>"
    }
    $sst = ($strings | ForEach-Object { "<si><t>$(Xml-Esc $_)</t></si>" }) -join ''
    New-Zip $path @{
        '[Content_Types].xml' = '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>'
        '_rels/.rels' = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
        'xl/workbook.xml' = '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Register" sheetId="1" r:id="rId1"/></sheets></workbook>'
        'xl/_rels/workbook.xml.rels' = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>'
        'xl/sharedStrings.xml' = '<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="' + $strings.Count + '" uniqueCount="' + $strings.Count + '">' + $sst + '</sst>'
        'xl/worksheets/sheet1.xml' = '<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>' + ($sheetRows -join '') + '</sheetData></worksheet>'
    }
}
function New-Pdf($path, $lines) {
    $esc = { param($s) $s.Replace('\', '\\').Replace('(', '\(').Replace(')', '\)') }
    $stream = "BT /F1 11 Tf 50 780 Td 14 TL`n" + (($lines | ForEach-Object { "(" + (& $esc $_) + ") Tj T*" }) -join "`n") + "`nET"
    $objs = @(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        "<< /Length $($stream.Length) >>`nstream`n$stream`nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    $sb = New-Object Text.StringBuilder; [void]$sb.Append("%PDF-1.4`n"); $offs = @()
    for ($i = 0; $i -lt $objs.Count; $i++) { $offs += $sb.Length; [void]$sb.Append("$($i + 1) 0 obj`n$($objs[$i])`nendobj`n") }
    $xref = $sb.Length
    [void]$sb.Append("xref`n0 $($objs.Count + 1)`n0000000000 65535 f `n")
    foreach ($o in $offs) { [void]$sb.Append(("{0:D10} 00000 n `n" -f $o)) }
    [void]$sb.Append("trailer`n<< /Size $($objs.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [IO.File]::WriteAllText($path, $sb.ToString(), [Text.Encoding]::ASCII)
}
function New-Corpus($root, $count) {
    New-Item -ItemType Directory -Force $root | Out-Null
    $sub = "finance", "operations", "hr", "it", "facilities", "quality" | ForEach-Object { Join-Path $root $_ }
    $sub | ForEach-Object { New-Item -ItemType Directory -Force $_ | Out-Null }
    $manifest = New-Object System.Collections.Generic.List[object]
    $kinds = "txt", "md", "csv", "docx", "xlsx", "pdf", "json", "code"
    for ($i = 0; $i -lt $count; $i++) {
        $kind = $kinds[$i % $kinds.Count]; $dir = $sub[$i % $sub.Count]; $id = New-Id $i
        $topic = $topics[$i % $topics.Count]
        $paras = 1..6 | ForEach-Object { Para $i $_ }
        $base = Join-Path $dir ("doc{0:D4}" -f $i)
        switch ($kind) {
            "txt"  { [IO.File]::WriteAllText("$base.txt", "Internal note $id`n`n" + ($paras -join "`n`n")) }
            "md"   { [IO.File]::WriteAllText("$base.md", "# $($topic.n) ($id)`n`n## Summary`n`n" + ($paras[0..2] -join "`n`n") + "`n`n## Details`n`n" + ($paras[3..5] -join "`n`n")) }
            "csv"  { $l = @("ticket,owner,topic,note"); 1..40 | ForEach-Object { $l += "T$i-$_,$($words[$rng.Next($words.Count)]),$($topics[($i + $_) % $topics.Count].n),$id line $_ status open" }; [IO.File]::WriteAllText("$base.csv", ($l -join "`n")) }
            "docx" { New-Docx "$base.docx" "$($topic.n) - case $id" $paras }
            "xlsx" { $rows = @(, @("Item", "Code", "Qty", "Note")); 1..30 | ForEach-Object { $rows += , @("Item $_", "$id-$_", [int]($rng.Next(1, 500)), $topics[($i + $_) % $topics.Count].n) }; New-Xlsx "$base.xlsx" $rows }
            "pdf"  { New-Pdf "$base.pdf" (@("Report $id", $topic.n) + ($paras | ForEach-Object { $_.Substring(0, [math]::Min(95, $_.Length)) })) }
            "json" { ([ordered]@{ id = $id; topic = $topic.n; items = @(1..8 | ForEach-Object { [ordered]@{ n = $_; text = (Para $i $_) } }) } | ConvertTo-Json -Depth 5) | Set-Content "$base.json" -Encoding UTF8 }
            "code" { $ext = @("py", "cs", "js")[$i % 3]; [IO.File]::WriteAllText("$base.$ext", "// module $id : $($topic.n)`nfunction handler_$($i)() {`n  // $($paras[0])`n  return `"$id`";`n}`n") }
        }
        $manifest.Add([pscustomobject]@{ Index = $i; Kind = $kind; Id = $id; Topic = $topic.n })
    }
    # Edge files that must not break anything
    $edge = Join-Path $root "edge-cases"; New-Item -ItemType Directory -Force $edge | Out-Null
    foreach ($e in "empty.txt", "empty.docx", "empty.xlsx", "empty.pdf", "empty.md", "empty.csv", "empty.json") { New-Item -ItemType File (Join-Path $edge $e) -Force | Out-Null }
    [IO.File]::WriteAllBytes((Join-Path $edge "corrupt.docx"), [byte[]](1..2000 | ForEach-Object { $rng.Next(256) }))
    [IO.File]::WriteAllBytes((Join-Path $edge "corrupt.xlsx"), [Text.Encoding]::ASCII.GetBytes("PK" + ("x" * 500)))
    [IO.File]::WriteAllBytes((Join-Path $edge "corrupt.pdf"), [Text.Encoding]::ASCII.GetBytes("%PDF-1.4`n1 0 obj << /Broken" + ("z" * 300)))
    [IO.File]::WriteAllBytes((Join-Path $edge "binary-as.txt"), [byte[]](1..4000 | ForEach-Object { $rng.Next(256) }))
    [IO.File]::WriteAllBytes((Join-Path $edge "truncated.docx"), ([IO.File]::ReadAllBytes((Join-Path $sub[3] "doc0003.docx")))[0..300])
    [IO.File]::WriteAllText((Join-Path $edge "invalid.json"), '{ "a": [1, 2, ')
    [IO.File]::WriteAllText((Join-Path $edge "weird name (1) #&%.txt"), "Edge file with unusual characters EDGE-TOKEN-7731.")
    return $manifest
}

# ---------------------------------------------------------------------------------------------------------------------------
# Process sampler (separate process so it never interferes with timing)
# ---------------------------------------------------------------------------------------------------------------------------
$samplerPath = Join-Path $work "sampler.ps1"
@'
param([string]$Root, [string]$Out, [string]$ConnOut, [string]$Stop, [int]$Cores)
$prev = @{}; $lastT = Get-Date
"ts,pid,name,ws_mb,private_mb,cpu_pct" | Set-Content $Out -Encoding ASCII
"ts,pid,name,local,remote,state" | Set-Content $ConnOut -Encoding ASCII
function IsLoop($a) { $a -in @("127.0.0.1","::1","0.0.0.0","::","") -or $a -like "127.*" -or $a -like "::ffff:127.*" }
while (-not (Test-Path $Stop)) {
    $now = Get-Date; $dt = ($now - $lastT).TotalSeconds; $lastT = $now
    $ours = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase) }
    $rows = @(); $pids = @()
    foreach ($p in $ours) {
        $pids += $p.Id
        $cpu = $null
        try { $cpu = $p.TotalProcessorTime.TotalSeconds } catch {}
        $pct = 0
        if ($cpu -ne $null -and $prev.ContainsKey($p.Id) -and $dt -gt 0) { $pct = [math]::Round(100 * ($cpu - $prev[$p.Id]) / $dt / $Cores, 1) }
        if ($cpu -ne $null) { $prev[$p.Id] = $cpu }
        $rows += [string]::Format([Globalization.CultureInfo]::InvariantCulture, "{0:o},{1},{2},{3},{4},{5}", $now.ToUniversalTime(), $p.Id, $p.ProcessName, [math]::Round($p.WorkingSet64/1MB,1), [math]::Round($p.PrivateMemorySize64/1MB,1), $pct)
    }
    if ($rows) { Add-Content $Out $rows -Encoding ASCII }
    if ($pids) {
        $c = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $pids -contains $_.OwningProcess }
        $lines = @()
        foreach ($x in $c) {
            $nm = ($ours | Where-Object Id -eq $x.OwningProcess | Select-Object -First 1).ProcessName
            $remoteOut = (-not (IsLoop $x.RemoteAddress)) -and $x.State -ne "Listen"
            $listenWide = $x.State -eq "Listen" -and -not (IsLoop $x.LocalAddress)
            if ($remoteOut -or $listenWide) { $lines += ("{0:o},{1},{2},{3}:{4},{5}:{6},{7}" -f $now.ToUniversalTime(), $x.OwningProcess, $nm, $x.LocalAddress, $x.LocalPort, $x.RemoteAddress, $x.RemotePort, $x.State) }
        }
        if ($lines) { Add-Content $ConnOut $lines -Encoding ASCII }
    }
    Start-Sleep -Milliseconds 1000
}
'@ | Set-Content $samplerPath -Encoding UTF8

function Get-SampleStats([datetime]$from, [datetime]$to) {
    $f = $from.ToUniversalTime(); $t = $to.ToUniversalTime(); $inv = [Globalization.CultureInfo]::InvariantCulture
    $rows = Import-Csv $script:sampOut | Where-Object { $d = [datetime]::Parse($_.ts, $inv, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime(); $d -ge $f -and $d -le $t }
    foreach ($r in $rows) { $r.ws_mb = [double]::Parse($r.ws_mb, $inv); $r.cpu_pct = [double]::Parse($r.cpu_pct, $inv) }
    $out = [ordered]@{}
    foreach ($g in ($rows | Group-Object name)) {
        $perTs = $g.Group | Group-Object ts   # a name may have several PIDs; sum per timestamp
        $ws = $perTs | ForEach-Object { ($_.Group | Measure-Object ws_mb -Sum).Sum }
        $cpu = $perTs | ForEach-Object { ($_.Group | Measure-Object cpu_pct -Sum).Sum }
        $out[$g.Name] = [ordered]@{ ws_peak_mb = [math]::Round(($ws | Measure-Object -Maximum).Maximum, 0); ws_avg_mb = [math]::Round(($ws | Measure-Object -Average).Average, 0)
            cpu_peak_pct = [math]::Round(($cpu | Measure-Object -Maximum).Maximum, 1); cpu_avg_pct = [math]::Round(($cpu | Measure-Object -Average).Average, 1); samples = $perTs.Count }
    }
    # total working set across all package processes per timestamp
    $tot = $rows | Group-Object ts | ForEach-Object { ($_.Group | Measure-Object ws_mb -Sum).Sum }
    if ($tot) { $out["_TOTAL"] = [ordered]@{ ws_peak_mb = [math]::Round(($tot | Measure-Object -Maximum).Maximum, 0); ws_avg_mb = [math]::Round(($tot | Measure-Object -Average).Average, 0) } }
    return $out
}
function Fmt-Stats($s) { if (-not $s.Count) { return "(no samples)" }; ($s.GetEnumerator() | ForEach-Object { $v = $_.Value; if ($_.Key -eq "_TOTAL") { "TOTAL ws peak $($v.ws_peak_mb) MB, avg $($v.ws_avg_mb) MB" } else { "$($_.Key): ws peak $($v.ws_peak_mb) MB / avg $($v.ws_avg_mb) MB, cpu peak $($v.cpu_peak_pct)% / avg $($v.cpu_avg_pct)% (of $cores logical cores)" } }) -join "; " }

# ---------------------------------------------------------------------------------------------------------------------------
# HTTP helpers
# ---------------------------------------------------------------------------------------------------------------------------
$http = [System.Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds(120)
function Api([string]$method, [string]$path, $body = $null, [int]$timeoutSec = 60) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), $script:endpoint + $path)
    $req.Headers.Add("X-PaRuX-Token", $script:token)
    if ($null -ne $body) { $req.Content = [System.Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 8 -Compress), [Text.Encoding]::UTF8, "application/json") }
    $cts = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($timeoutSec))
    $resp = $http.SendAsync($req, $cts.Token).GetAwaiter().GetResult()
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $json = $null; if ($text) { try { $json = $text | ConvertFrom-Json } catch {} }
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Json = $json; Text = $text }
}
function Percentile($vals, $p) { $s = @($vals | Sort-Object); if (-not $s.Count) { return $null }; $s[[math]::Min($s.Count - 1, [math]::Ceiling($p / 100 * $s.Count) - 1)] }
function Measure-Search($queries, $mode, [scriptblock]$check = $null) {
    $lat = @(); $hit = 0; $errs = 0
    foreach ($q in $queries) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Api GET ("/api/v1/knowledge/search?q=" + [uri]::EscapeDataString($q.Q) + "&mode=$mode&k=10")
        $sw.Stop(); $lat += $sw.Elapsed.TotalMilliseconds
        if ($r.Status -ne 200) { $errs++; continue }
        if ($null -eq $check -or (& $check $q @($r.Json))) { $hit++ }
    }
    [ordered]@{ n = $queries.Count; p50_ms = [math]::Round((Percentile $lat 50), 1); p95_ms = [math]::Round((Percentile $lat 95), 1); max_ms = [math]::Round(($lat | Measure-Object -Maximum).Maximum, 1); hits = $hit; errors = $errs }
}
function Wait-Index([string]$id, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $s = Api GET "/api/v1/knowledge/sources/$id/status"
        $st = $s.Json.job.state
        if ($st -in @("done", "failed", "cancelled")) { return $s.Json }
        Start-Sleep -Milliseconds 500
    }
    return $null
}


function Invoke-TrayExit {
    # Finds the PaRuX notification-area icon (taskbar or overflow flyout), right-clicks it and invokes the Exit item.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class Mouse { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e); public static void Click(int x,int y,bool right){ SetCursorPos(x,y); System.Threading.Thread.Sleep(150); mouse_event(right?0x8u:0x2u,0,0,0,UIntPtr.Zero); mouse_event(right?0x10u:0x4u,0,0,0,UIntPtr.Zero);} }' -ErrorAction SilentlyContinue
        $A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
        $root = $A::RootElement
        $find = {
            $all = $root.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($e in $all) { try { if ($e.Current.Name -like "PaRuX*" -and $e.Current.ControlType.ProgrammaticName -eq "ControlType.Button") { return $e } } catch {} }
            return $null
        }
        $icon = & $find
        if (-not $icon) {
            $chev = $root.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { try { $_.Current.AutomationId -eq "SystemTrayIcon" -or $_.Current.Name -match "hidden icons|icone nascoste|Mostra icone" } catch { $false } } | Select-Object -First 1
            if ($chev) { $chev.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1; $icon = & $find }
        }
        if (-not $icon) { return "tray icon not found via UI Automation" }
        $r = $icon.Current.BoundingRectangle
        [Mouse]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2), $true); Start-Sleep 1
        $menu = $root.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { try { $_.Current.ControlType.ProgrammaticName -eq "ControlType.MenuItem" -and $_.Current.Name -match "^(Exit|Esci|Quit|Chiudi)" } catch { $false } } | Select-Object -First 1
        if (-not $menu) { return "tray menu opened but no Exit item found" }
        try { $menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { $m = $menu.Current.BoundingRectangle; [Mouse]::Click([int]($m.X + $m.Width / 2), [int]($m.Y + $m.Height / 2), $false) }
        return "clicked Exit ($($menu.Current.Name))"
    } catch { return "tray exit automation failed: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------------------------------------------------------
# 1. Extract + start like the launcher
# ---------------------------------------------------------------------------------------------------------------------------
$sampler = $null; $appProc = $null; $stopFile = Join-Path $work "stop.flag"
$script:sampOut = Join-Path $work "samples.csv"; $connOut = Join-Path $work "conns.csv"
try {
    Say "Machine: $cpuName, $cores logical cores, $totalRamGb GB RAM. Work dir $work"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $extract)
    $app = Join-Path $extract "PaRuX-Corporate-Lite"
    $R.zip_mb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 0); $R.extract_s = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $R.package_extracted_mb = [math]::Round((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 0)
    $bundleManifestPath = Join-Path $app "MANIFEST.json"
    if (-not (Test-Path -LiteralPath $bundleManifestPath -PathType Leaf)) { throw "Corporate archive has no MANIFEST.json." }
    $bundleManifest = Get-Content -LiteralPath $bundleManifestPath -Raw | ConvertFrom-Json
    $R.package_model_included = [bool]$bundleManifest.modelIncluded
    $R.package_embedding_included = [bool]$bundleManifest.embeddingIncluded
    $R.package_reranker_included = [bool]$bundleManifest.rerankerIncluded
    if ($EnforceAcceptance -and (-not $R.package_model_included -or -not $R.package_embedding_included -or -not $R.package_reranker_included)) {
        throw "Complete Corporate acceptance requires a bundled text GGUF, ONNX embeddings and ONNX reranker. Rebuild with -Fetch or -RequireReranker plus locally provided assets."
    }
    Say "Extracted $($R.zip_mb) MB ZIP in $($R.extract_s)s -> $($R.package_extracted_mb) MB"

    $cmd = Get-Content (Join-Path $app "Start PaRuX Corporate Lite.cmd") -Raw
    if ($cmd -notmatch 'PARUX_CORPORATE=1' -or $cmd -notmatch 'PARUX_DATA_ROOT=%~dp0PaRuXData') { throw "Launcher no longer matches what this script replicates." }
    $dataRoot = Join-Path $app "PaRuXData"
    $env:PARUX_CORPORATE = "1"; $env:PARUX_DATA_ROOT = $dataRoot; $env:PARUX_LOCALAI_STATE_DIR = $dataRoot

    $sampler = Start-Process pwsh -ArgumentList @("-NoProfile", "-File", $samplerPath, "-Root", $extract, "-Out", $script:sampOut, "-ConnOut", $connOut, "-Stop", $stopFile, "-Cores", $cores) -PassThru -WindowStyle Hidden
    $t0 = Get-Date
    $appProc = Start-Process (Join-Path $app "PaRuX.exe") -PassThru -WorkingDirectory $app
    $boot = Join-Path $dataRoot "server\bootstrap.json"
    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $boot)) { if ($appProc.HasExited) { throw "App exited early (code $($appProc.ExitCode))." }; Start-Sleep -Milliseconds 300 }
    if (-not (Test-Path $boot)) { throw "Core did not start." }
    $b = Get-Content $boot -Raw | ConvertFrom-Json; $script:endpoint = $b.endpoint.TrimEnd('/'); $script:token = $b.token
    # wait for the Core to answer
    for ($i = 0; $i -lt 120; $i++) { try { if ((Api GET "/api/v1/knowledge/search?q=ping").Status -eq 200) { break } } catch {}; Start-Sleep -Milliseconds 500 }
    $R.cold_start_to_core_s = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    Say "Core up at $script:endpoint after $($R.cold_start_to_core_s)s"
    Start-Sleep 5
    $idleStats = Get-SampleStats $t0 (Get-Date)
    $R.idle_after_start = Fmt-Stats (Get-SampleStats (Get-Date).AddSeconds(-4) (Get-Date))

    # ------------------------------------------------------------------------------------------------------------------------
    # 2. Corpus + index
    # ------------------------------------------------------------------------------------------------------------------------
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $manifest = New-Corpus $docsRoot $DocCount
    $R.corpus = "$DocCount generated docs (+edge-cases) in $([math]::Round($sw.Elapsed.TotalSeconds,1))s, $([math]::Round((Get-ChildItem $docsRoot -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)) MB, files: $((Get-ChildItem $docsRoot -Recurse -File).Count)"
    Say $R.corpus

    $noFolder = Api POST "/api/v1/rag/sources" @{ folderPath = (Join-Path $work "does-not-exist") }
    $R.add_missing_folder_status = $noFolder.Status
    $add = Api POST "/api/v1/rag/sources" @{ folderPath = $docsRoot }
    if ($add.Status -ne 200) { throw "Adding source failed: $($add.Status) $($add.Text)" }
    $srcId = $add.Json.id; if (-not $srcId) { $srcId = $add.Json.Id }
    Say "Source $srcId added"
    $tIdx0 = Get-Date
    $swIdx = [Diagnostics.Stopwatch]::StartNew()
    $st = Api POST "/api/v1/knowledge/sources/$srcId/index"
    if ($st.Status -ne 202) { throw "Index start failed: $($st.Status) $($st.Text)" }

    # pause / resume while running
    Start-Sleep -Milliseconds 700
    $pr = [ordered]@{}
    $p1 = Api GET "/api/v1/knowledge/sources/$srcId/status"
    $pr.state_before = "$($p1.Json.job.state)"
    $pa = Api POST "/api/v1/knowledge/sources/$srcId/pause"
    Start-Sleep 3
    $a1 = Api GET "/api/v1/knowledge/sources/$srcId/status"; Start-Sleep 3; $a2 = Api GET "/api/v1/knowledge/sources/$srcId/status"
    $pr.pause_status = $pa.Status
    $pr.state_while_paused = "$($a2.Json.job.state)"
    $pr.chunks_during_pause = "$($a1.Json.chunks) -> $($a2.Json.chunks)"
    $pr.frozen_while_paused = ($a1.Json.chunks -eq $a2.Json.chunks -and $a1.Json.files -eq $a2.Json.files)
    $re = Api POST "/api/v1/knowledge/sources/$srcId/resume"
    Start-Sleep 3
    $a3 = Api GET "/api/v1/knowledge/sources/$srcId/status"
    $pr.resume_status = $re.Status
    $pr.progress_after_resume = ($a3.Json.chunks -gt $a2.Json.chunks) -or ($a3.Json.job.state -eq "done")
    $R.pause_resume = $pr
    Say "Pause/resume: $($pr | ConvertTo-Json -Compress)"
    $swIdx.Stop()  # (pause time is excluded below by measuring from progress)
    $final = Wait-Index $srcId $IndexTimeoutSeconds
    $tIdx1 = Get-Date
    $R.index_wall_s_including_6s_pause_test = [math]::Round(($tIdx1 - $tIdx0).TotalSeconds, 1)
    if (-not $final) { throw "Indexing did not finish within $IndexTimeoutSeconds s." }
    $R.index_final_state = $final.job.state
    $R.index_files = $final.files; $R.index_chunks = $final.chunks
    $R.index_progress = $final.job.progress | ConvertTo-Json -Compress
    $R.index_problems = @($final.problems | ForEach-Object { "$($_.relativePath) [$($_.kind)] $($_.reason)" })
    $R.index_stats = Fmt-Stats (Get-SampleStats $tIdx0 $tIdx1)
    Say "Indexed: $($final.job.state), files $($final.files), chunks $($final.chunks), $($R.index_wall_s_including_6s_pause_test)s"
    Say "During indexing: $($R.index_stats)"

    # ------------------------------------------------------------------------------------------------------------------------
    # 3. Search latency
    # ------------------------------------------------------------------------------------------------------------------------
    $idQ = $manifest | Where-Object { $_.Kind -in "txt", "md", "csv", "docx", "xlsx", "pdf", "json", "code" } | Get-Random -Count ([math]::Min(50, $manifest.Count)) -SetSeed 7 | ForEach-Object { [pscustomobject]@{ Q = $_.Id; Id = $_.Id } }
    $tS0 = Get-Date
    $null = Measure-Search ($idQ | Select-Object -First 3) "Auto"   # warm-up
    $R.search_exact_id_auto = Measure-Search $idQ "Auto" { param($q, $hits) (@($hits | Where-Object { $_.text -like "*$($q.Id)*" -or $_.fileName -like "*" } | Where-Object { $_.text -like "*$($q.Id)*" }).Count -gt 0) }
    $R.search_exact_id_exact_mode = Measure-Search $idQ "Exact" { param($q, $hits) (@($hits | Where-Object { $_.text -like "*$($q.Id)*" }).Count -gt 0) }
    $sem = $topics | ForEach-Object { [pscustomobject]@{ Q = "how do we handle $($_.n)"; Topic = $_.n } }
    $semQ = @(); while ($semQ.Count -lt 50) { $semQ += $sem }; $semQ = $semQ[0..49]
    $R.search_semantic = Measure-Search $semQ "Semantic" { param($q, $hits) $hits.Count -gt 0 }
    $R.search_hybrid = Measure-Search $semQ "Hybrid" { param($q, $hits) $hits.Count -gt 0 }
    $R.search_auto_natural = Measure-Search $semQ "Auto" { param($q, $hits) $hits.Count -gt 0 }
    $R.search_stats = Fmt-Stats (Get-SampleStats $tS0 (Get-Date))
    Say "Search exact-id Auto: $($R.search_exact_id_auto | ConvertTo-Json -Compress)"
    Say "Search semantic: $($R.search_semantic | ConvertTo-Json -Compress); hybrid: $($R.search_hybrid | ConvertTo-Json -Compress)"
    $edgeTok = Api GET "/api/v1/knowledge/search?q=EDGE-TOKEN-7731&mode=Exact"
    $R.edge_file_with_odd_name_found = (@($edgeTok.Json).Count -gt 0)

    # ------------------------------------------------------------------------------------------------------------------------
    # 4. Robustness: non-writable folder, deleted-while-indexing, locked file
    # ------------------------------------------------------------------------------------------------------------------------
    $rob = [ordered]@{}
    $ro = Join-Path $work "readonly-docs"; New-Item -ItemType Directory $ro | Out-Null
    1..5 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $ro "ro$_.txt"), "Read only folder document RO-TOKEN-$($_)88 about facility rules.") }
    $me = "$env:USERDOMAIN\$env:USERNAME"
    & icacls $ro /deny "${me}:(OI)(CI)(WD,AD,DC)" | Out-Null
    $canWrite = try { [IO.File]::WriteAllText((Join-Path $ro "probe.txt"), "x"); $true } catch { $false }
    $rob.readonly_folder_really_not_writable = (-not $canWrite)
    $a = Api POST "/api/v1/rag/sources" @{ folderPath = $ro }; $roId = $a.Json.id
    $null = Api POST "/api/v1/knowledge/sources/$roId/index"
    $f = Wait-Index $roId 120
    $rob.readonly_index_state = if ($f) { "$($f.job.state) files=$($f.files) chunks=$($f.chunks)" } else { "TIMEOUT" }
    $rob.readonly_token_found = (@((Api GET "/api/v1/knowledge/search?q=RO-TOKEN-388&mode=Exact").Json).Count -gt 0)
    $rob.readonly_no_files_written_in_folder = (@(Get-ChildItem $ro -Force).Count -eq 5)
    & icacls $ro /remove:d $me | Out-Null
    $null = Api DELETE "/api/v1/rag/sources/$roId"

    $churn = Join-Path $work "churn-docs"; New-Item -ItemType Directory $churn | Out-Null
    1..400 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $churn ("c{0:D3}.txt" -f $_)), ("Churn document $_ CHURN-$($_)-ID. " + ((Para $_ 1) * 4))) }
    $lockedPath = Join-Path $churn "c200.txt"
    $lockStream = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $a = Api POST "/api/v1/rag/sources" @{ folderPath = $churn }; $chId = $a.Json.id
    $null = Api POST "/api/v1/knowledge/sources/$chId/index"
    Start-Sleep -Milliseconds 400
    $deleted = 0; 150..399 | ForEach-Object { try { Remove-Item (Join-Path $churn ("c{0:D3}.txt" -f $_)) -Force -ErrorAction Stop; $deleted++ } catch {} }
    $rob.churn_files_deleted_during_indexing = $deleted
    $f = Wait-Index $chId 180
    $lockStream.Dispose()
    $rob.churn_index_state = if ($f) { "$($f.job.state) files=$($f.files) chunks=$($f.chunks) problems=$(@($f.problems).Count)" } else { "TIMEOUT" }
    $alive = try { (Api GET "/api/v1/knowledge/search?q=churn").Status -eq 200 } catch { $false }
    $rob.core_alive_after_churn = $alive -and -not $appProc.HasExited
    $null = Api DELETE "/api/v1/rag/sources/$chId"
    $R.robustness = $rob
    Say "Robustness: $($rob | ConvertTo-Json -Compress)"

    # ------------------------------------------------------------------------------------------------------------------------
    # 5. Chat with the bundled model + citations
    # ------------------------------------------------------------------------------------------------------------------------
    $target = $manifest[5]
    $question = "According to the documents, what is the rule for $($target.Topic)? Answer briefly and cite the source."
    $tC0 = Get-Date
    $chat = [ordered]@{ question = $question }
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $script:endpoint + "/api/v1/chat/completions")
    $req.Headers.Add("X-PaRuX-Token", $script:token)
    $req.Content = [System.Net.Http.StringContent]::new((@{ model = "local-default"; stream = $true; max_tokens = 200; messages = @(@{ role = "user"; content = $question }) } | ConvertTo-Json -Depth 5 -Compress), [Text.Encoding]::UTF8, "application/json")
    $http2 = [System.Net.Http.HttpClient]::new(); $http2.Timeout = [TimeSpan]::FromSeconds($ChatTimeoutSeconds)
    $swC = [Diagnostics.Stopwatch]::StartNew()
    $ttft = $null; $deltas = 0; $tLast = $null; $allText = New-Object Text.StringBuilder; $usage = $null; $raw = New-Object Text.StringBuilder
    try {
        $resp = $http2.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $chat.http_status = [int]$resp.StatusCode
        if (-not $resp.IsSuccessStatusCode) { $chat.error_body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
        $chat.time_to_headers_s = [math]::Round($swC.Elapsed.TotalSeconds, 1)
        $sr = [IO.StreamReader]::new($resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
        while ($null -ne ($line = $sr.ReadLine())) {
            if ($raw.Length -lt 6000) { [void]$raw.AppendLine($line) }
            if (-not $line.StartsWith("data:")) { continue }
            $d = $line.Substring(5).Trim(); if ($d -eq "[DONE]") { break }
            try { $j = $d | ConvertFrom-Json } catch { continue }
            if ($j.usage) { $usage = $j.usage }
            $c = $null; try { $c = $j.choices[0].delta.content } catch {}
            if ($c) { if ($null -eq $ttft) { $ttft = $swC.Elapsed.TotalSeconds }; $deltas++; $tLast = $swC.Elapsed.TotalSeconds; [void]$allText.Append($c) }
        }
        $chat.total_s = [math]::Round($swC.Elapsed.TotalSeconds, 1)
    } catch { $chat.error = $_.Exception.Message }
    $tC1 = Get-Date
    $chat.ttft_s = if ($ttft) { [math]::Round($ttft, 1) } else { $null }
    $chat.stream_chunks = $deltas
    if ($deltas -gt 1 -and $tLast -gt $ttft) { $chat.tokens_per_s_by_chunks = [math]::Round(($deltas - 1) / ($tLast - $ttft), 1) }
    if ($usage) { $chat.usage = ($usage | ConvertTo-Json -Compress) }
    $ans = $allText.ToString()
    $chat.answer_chars = $ans.Length
    $chat.answer_mentions_expected_source = ($ans -match [regex]::Escape($target.Id)) -or ($ans -match ("doc{0:D4}" -f $target.Index))
    $chat.answer_excerpt = ($ans.Substring(0, [math]::Min(400, $ans.Length)) -replace '\s+', ' ')
    $chat.raw_stream_head = ($raw.ToString().Substring(0, [math]::Min(1200, $raw.Length)) -replace '\s+', ' ')
    $chat.stats = Fmt-Stats (Get-SampleStats $tC0 $tC1)
    $R.chat = $chat
    Say "Chat: ttft $($chat.ttft_s)s, chunks $deltas, tps $($chat.tokens_per_s_by_chunks), total $($chat.total_s)s, status $($chat.http_status)"
    Say "Chat resources: $($chat.stats)"

    # memory/CPU steady state after chat (model loaded) and after a pause
    Start-Sleep 10
    $R.steady_after_chat = Fmt-Stats (Get-SampleStats (Get-Date).AddSeconds(-8) (Get-Date))
    Say "Steady after chat: $($R.steady_after_chat)"
}
catch {
    $R.FATAL = $_.Exception.Message + " @ " + $_.ScriptStackTrace
    Say "FATAL: $($R.FATAL)"
}
finally {
    # ------------------------------------------------------------------------------------------------------------------------
    # 6. Whole-run resources, connections, shutdown
    # ------------------------------------------------------------------------------------------------------------------------
    try { if ($lockStream) { $lockStream.Dispose() } } catch {}
    try {
        if (Test-Path $script:sampOut) { $R.whole_run_stats = Fmt-Stats (Get-SampleStats ([datetime]"2000-01-01") (Get-Date).AddMinutes(1)) }
        $names = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($extract, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { "$($_.ProcessName)#$($_.Id)" })
        $R.processes_before_shutdown = $names -join ", "
        function Get-Ours { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($extract, [StringComparison]::OrdinalIgnoreCase) }) }
        # (a) window close: by design PaRuX hides to the tray, so the processes are expected to stay.
        if ($appProc -and -not $appProc.HasExited) { $null = $appProc.CloseMainWindow(); Start-Sleep 4; $R.processes_after_window_close = (Get-Ours | ForEach-Object { $_.ProcessName }) -join ", " }
        # (b) the real exit: tray menu "Exit" through UI Automation.
        Say "Exiting through the tray menu ..."
        $R.tray_exit = Invoke-TrayExit
        $dl = (Get-Date).AddSeconds(40); while ((Get-Date) -lt $dl -and (Get-Ours).Count -gt 0) { Start-Sleep 1 }
        $left = Get-Ours
        $R.processes_left_after_tray_exit = if ($left.Count) { ($left | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join ", " } else { "none" }
        if ($left.Count -gt 0 -and $appProc -and -not $appProc.HasExited) {
            # Tray automation unavailable: at least learn whether the Core and llama-server follow the app when it dies.
            Stop-Process -Id $appProc.Id -Force -ErrorAction SilentlyContinue
            $dl = (Get-Date).AddSeconds(30); while ((Get-Date) -lt $dl -and (Get-Ours).Count -gt 0) { Start-Sleep 1 }
            $left = Get-Ours
            $R.processes_left_30s_after_app_killed = if ($left.Count) { ($left | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join ", " } else { "none" }
        }
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
    } catch { $R.shutdown_error = $_.Exception.Message }
    "stop" | Set-Content $stopFile; Start-Sleep 2
    try {
        $conns = @(Import-Csv $connOut)
        $out = @($conns | Where-Object { $_.state -ne "Listen" }); $wide = @($conns | Where-Object { $_.state -eq "Listen" })
        $R.nonloopback_outbound_connections = $out.Count
        $R.nonloopback_outbound_detail = @($out | Select-Object -First 10 | ForEach-Object { "$($_.name) $($_.local)->$($_.remote) $($_.state)" })
        $R.listeners_on_non_loopback = @($wide | Group-Object { "$($_.name) $($_.local)" } | ForEach-Object { $_.Name })
    } catch { $R.conn_error = $_.Exception.Message }
    Remove-Item Env:PARUX_CORPORATE, Env:PARUX_DATA_ROOT, Env:PARUX_LOCALAI_STATE_DIR -ErrorAction SilentlyContinue

    # An ordinary measurement writes diagnostic evidence even on failure. The explicit acceptance
    # mode must reject false-green packages instead of treating HTTP 200 or a generated report as DONE.
    $acceptanceFailures = New-Object System.Collections.Generic.List[string]
    if ($EnforceAcceptance) {
        if ($R.index_final_state -ne "done" -or [int]$R.index_chunks -lt 1) {
            $acceptanceFailures.Add("Document indexing did not finish with searchable chunks.")
        }
        if ($R.search_exact_id_exact_mode.errors -gt 0 -or
            $R.search_exact_id_exact_mode.hits -lt [math]::Ceiling(0.95 * $R.search_exact_id_exact_mode.n)) {
            $acceptanceFailures.Add("Exact document lookup recall is below 95% or returned HTTP errors.")
        }
        if ($R.search_semantic.errors -gt 0 -or $R.search_semantic.hits -lt 1) {
            $acceptanceFailures.Add("Local semantic retrieval did not demonstrate a nonempty result.")
        }
        if ($R.chat.http_status -ne 200 -or $R.chat.answer_chars -lt 1 -or -not $R.chat.answer_mentions_expected_source) {
            $acceptanceFailures.Add("Local chat failed to return a source-grounded response.")
        }
        if ($R.nonloopback_outbound_connections -gt 0 -or @($R.listeners_on_non_loopback).Count -gt 0) {
            $acceptanceFailures.Add("Corporate package had observed non-loopback traffic or listeners.")
        }
    }
    $R.acceptance = [ordered]@{
        enforced = [bool]$EnforceAcceptance
        passed = ([bool]$EnforceAcceptance -and $acceptanceFailures.Count -eq 0)
        failures = @($acceptanceFailures.ToArray())
        note = $(if ($EnforceAcceptance) { "Strict offline smoke acceptance; not 30,000-page or clean-machine certification." }
                 else { "Measurements only; not an acceptance pass." })
    }

    # ---- write the results file ----
    $stamp = Get-Date -Format "yyyyMMdd-HHmm"
    $resFile = Join-Path $ResultsDir "corporate-lite-measurements-$stamp.md"
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine("# Corporate Lite measurements ($stamp)")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Machine: $cpuName, $cores logical cores, $totalRamGb GB RAM (Windows 11). Synthetic corpus only. Package: $(Split-Path $ZipPath -Leaf).")
    [void]$sb.AppendLine("")
    function Add-Obj($o, $indent = "") {
        foreach ($k in $o.Keys) {
            $v = $o[$k]
            if ($v -is [System.Collections.IDictionary]) { [void]$sb.AppendLine("$indent- **$k**"); Add-Obj $v ($indent + "  ") }
            elseif ($v -is [array]) { [void]$sb.AppendLine("$indent- **$k**: " + (($v | ForEach-Object { "$_" }) -join " | ")) }
            else { [void]$sb.AppendLine("$indent- **$k**: $v") }
        }
    }
    Add-Obj $R
    [void]$sb.AppendLine(""); [void]$sb.AppendLine("## Log"); [void]$sb.AppendLine('```'); $log | ForEach-Object { [void]$sb.AppendLine($_) }; [void]$sb.AppendLine('```')
    Set-Content $resFile $sb.ToString() -Encoding UTF8
    Copy-Item $script:sampOut (Join-Path $ResultsDir "corporate-lite-samples-$stamp.csv") -ErrorAction SilentlyContinue
    Say "Results: $resFile"
    if ($EnforceAcceptance -and $acceptanceFailures.Count -eq 0) { Say "Corporate offline synthetic-corpus acceptance PASS." }
    if (-not $KeepWorkDir) {
        Start-Sleep 2; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($EnforceAcceptance -and $acceptanceFailures.Count -gt 0) {
        throw ("Corporate offline acceptance FAILED; evidence: $resFile; " + ($acceptanceFailures -join " | "))
    }
}
