# Regenerates the hash-locked requirement sets shipped in src/PaRuX.Server/managed-locks (SEC-F02).
# Needs uv and network access to PyPI / download.pytorch.org. Review the diff before committing; never hand-edit a lock.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here '..\..\src\PaRuX.Server\managed-locks'
$common = @('--python-version', '3.11', '--python-platform', 'windows', '--generate-hashes')
Push-Location $here
try {
    uv pip compile cutout.in @common -o "$out\cutout.lock.txt"
    foreach ($set in 'lora-convert', 'threed', 'diffusers-engine', 'speech') {
        uv pip compile "$set.in" @common --no-emit-package torch -o "$out\$set.lock.txt"
    }
    # avatar: ONE distribution per import package under --no-deps. LatentSync pins opencv-python, onnxruntime-gpu and
    # insightface==0.7.3, so the alternative builds that transitive dependencies would add (and that overwrite
    # cv2/onnxruntime) are dropped.
    uv pip compile avatar.in @common --no-emit-package torch --no-emit-package onnxruntime `
        --no-emit-package opencv-contrib-python --no-emit-package opencv-python-headless -o "$out\avatar.lock.txt"
    uv pip compile torch.in @common --index-url https://download.pytorch.org/whl/cu128 -o "$out\torch-cu128.lock.txt"
    uv pip compile torch.in @common --index-url https://download.pytorch.org/whl/cpu -o "$out\torch-cpu.lock.txt"
} finally { Pop-Location }
