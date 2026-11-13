# Regenerates the hash-locked requirement sets shipped in src/PaRuX.Server/managed-locks (SEC-F02).
# Needs uv and network access to PyPI / download.pytorch.org. Review the diff before committing; never hand-edit a lock.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here '..\..\src\PaRuX.Server\managed-locks'
$common = @('--python-version', '3.11', '--python-platform', 'windows', '--generate-hashes')
Push-Location $here
try {
    uv pip compile cutout.in @common -o "$out\cutout.lock.txt"
    foreach ($set in 'lora-convert', 'threed', 'diffusers-engine', 'speech', 'avatar') {
        uv pip compile "$set.in" @common --no-emit-package torch -o "$out\$set.lock.txt"
    }
    uv pip compile torch.in @common --index-url https://download.pytorch.org/whl/cu128 -o "$out\torch-cu128.lock.txt"
    uv pip compile torch.in @common --index-url https://download.pytorch.org/whl/cpu -o "$out\torch-cpu.lock.txt"
} finally { Pop-Location }
