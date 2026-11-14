# Regenerates the hash-locked requirement sets shipped in src/PaRuX.Server/managed-locks (SEC-F02).
# Needs uv and network access to PyPI / download.pytorch.org. Review the diff before committing; never hand-edit a lock.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here '..\..\src\PaRuX.Server\managed-locks'
# A fixed header keeps the lock diffs free of machine-specific absolute paths.
$common = @('--python-version', '3.11', '--python-platform', 'windows', '--generate-hashes',
    '--custom-compile-command', 'tools/managed-locks/generate.ps1')
Push-Location $here
try {
    # Existing lock files are passed as -o targets, so uv keeps their pins (preferences) and only changes what must change.
    # 1. The PyTorch closures first: every engine venv installs one of them, so it is the reference for shared packages.
    uv pip compile torch.in @common --index-url https://download.pytorch.org/whl/cu128 -o "$out\torch-cu128.lock.txt"
    uv pip compile torch.in @common --index-url https://download.pytorch.org/whl/cpu -o "$out\torch-cpu.lock.txt"

    # 2. The packages of the torch closure (torch itself excluded) become constraints for every engine lock, so a second
    #    install into the same venv can never replace a package of the first (final venv = one consistent hashed closure).
    function Get-Pins($file) {
        $pins = [ordered]@{}
        foreach ($line in Get-Content $file) {
            if ($line -match '^([A-Za-z0-9][A-Za-z0-9._-]*)==([^\s;\\]+)') {
                $name = $Matches[1].ToLowerInvariant().Replace('_', '-')
                if ($name -ne 'torch') { $pins[$name] = $Matches[2] }
            }
        }
        $pins
    }
    $cpu = Get-Pins "$out\torch-cpu.lock.txt"
    $cuda = Get-Pins "$out\torch-cu128.lock.txt"
    foreach ($name in $cpu.Keys) {
        if ($cuda.Contains($name) -and $cuda[$name] -ne $cpu[$name]) { throw "torch-cpu and torch-cu128 disagree on $name ($($cpu[$name]) vs $($cuda[$name]))" }
    }
    # Relative name on purpose: uv records it in the lock's "# via" comments. Removed again at the end.
    $closure = 'torch-closure.constraints'
    try {
        ($cpu.GetEnumerator() | ForEach-Object { "$($_.Key)==$($_.Value)" }) | Set-Content -Encoding ascii $closure

        # 3. Engine locks, constrained to the torch closure.
        uv pip compile cutout.in @common -o "$out\cutout.lock.txt"
        foreach ($set in 'lora-convert', 'threed', 'diffusers-engine', 'speech') {
            uv pip compile "$set.in" @common --no-emit-package torch -c $closure -o "$out\$set.lock.txt"
        }
        # avatar: ONE distribution per import package under --no-deps. LatentSync pins opencv-python, onnxruntime-gpu and
        # insightface==0.7.3, so the alternative builds that transitive dependencies would add (and that overwrite
        # cv2/onnxruntime) are dropped.
        uv pip compile avatar.in @common --no-emit-package torch --no-emit-package onnxruntime `
            --no-emit-package opencv-contrib-python --no-emit-package opencv-python-headless -c $closure -o "$out\avatar.lock.txt"
    } finally { Remove-Item $closure -ErrorAction SilentlyContinue }

    # 4. Hashed build requirements for source distributions (passed as --build-constraints by ManagedRequirementLocks).
    uv pip compile build-constraints.in @common -o "$out\build-constraints.lock.txt"
} finally { Pop-Location }
