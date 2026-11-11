<#
 Reviewed llama.cpp CPU archive pins (release tag -> archive file name -> SHA-256).
 Defines functions only. The pins live in packaging/llama-cpu-archive.lock.json so the
 archive digest is NOT trust-on-first-use from the same GitHub API response as the download.
#>
function Get-PaRuXLlamaCpuPin {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$ArchiveName,
        [string]$LockPath = "",
        [switch]$AllowUnpinned
    )
    if ([string]::IsNullOrWhiteSpace($LockPath)) {
        $LockPath = Join-Path (Split-Path -Parent $PSScriptRoot) "packaging\llama-cpu-archive.lock.json"
    }
    if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) { throw "llama.cpp CPU archive lock file is missing: $LockPath" }
    $lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $hit = @($lock.pins | Where-Object { $_.tag -ceq $Tag -and $_.archiveName -ceq $ArchiveName })
    if ($hit.Count -gt 1) { throw "Ambiguous llama.cpp CPU pin for $Tag / $ArchiveName." }
    if ($hit.Count -eq 1) {
        $sha = ([string]$hit[0].sha256).ToLowerInvariant()
        if ($sha -notmatch '^[0-9a-f]{64}$') { throw "Invalid SHA-256 pin for $Tag / $ArchiveName." }
        return $sha
    }
    if ($AllowUnpinned) {
        Write-Warning "UNPINNED llama.cpp CPU runtime $Tag ($ArchiveName): trusting the upstream release metadata (-AllowUnpinnedLlamaCpuRuntime). Review and add a pin before distributing."
        return $null
    }
    throw "No reviewed SHA-256 pin for llama.cpp $Tag / $ArchiveName in $LockPath. Review the release, add a pin, or pass -AllowUnpinnedLlamaCpuRuntime explicitly."
}

function Assert-PaRuXLlamaCpuArchivePin {
    param([Parameter(Mandatory)][string]$File, [Parameter(Mandatory)][string]$PinnedSha256, [string]$UpstreamDigest = "")
    if ($UpstreamDigest -match '^sha256:([0-9A-Fa-f]{64})$' -and $Matches[1].ToLowerInvariant() -ne $PinnedSha256.ToLowerInvariant()) {
        throw "Upstream release digest differs from the reviewed pin for $File. Re-review the release before updating the pin."
    }
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Missing llama.cpp CPU archive: $File" }
    $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $PinnedSha256.ToLowerInvariant()) { throw "llama.cpp CPU archive does not match the reviewed pin: $File (expected $PinnedSha256, got $actual)" }
}
