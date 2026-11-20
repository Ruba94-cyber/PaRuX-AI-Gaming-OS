<#
.SYNOPSIS
    Builds the PaRuX XInput shim (native/xinput-shim) for x64 and x86 into artifacts\xinput-shim\{x64,x86}\parux_xinput.dll.
.DESCRIPTION
    Uses the Visual Studio C++ tools found by vswhere (no extra install). The shim is a plain user-mode DLL: no driver, no signing
    of kernel code. The Core copies it into a game folder under the names the game asks for (xinput1_3, xinput1_4, xinput9_1_0).
    Sign the DLLs with the release certificate when packaging; this script only compiles them.
#>
[CmdletBinding()]
param([string]$OutputDirectory = "")
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo "native\xinput-shim\parux_xinput.c"
$def = Join-Path $repo "native\xinput-shim\parux_xinput.def"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repo "artifacts\xinput-shim" }

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found; install the Visual Studio C++ build tools." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "No Visual Studio installation with the C++ x86/x64 tools was found." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvarsall.bat"

foreach ($arch in @("x64", "x86")) {
    $out = Join-Path $OutputDirectory $arch
    New-Item -ItemType Directory -Force $out | Out-Null
    $obj = Join-Path $out "obj"
    New-Item -ItemType Directory -Force $obj | Out-Null
    $dll = Join-Path $out "parux_xinput.dll"
    $cmd = "`"$vcvars`" $arch >nul && cl /nologo /O2 /GS /W3 /WX /LD /Fo`"$obj\\`" `"$src`" /link /NOLOGO /DEF:`"$def`" /IMPLIB:`"$obj\parux_xinput.lib`" /OUT:`"$dll`" kernel32.lib"
    cmd.exe /c $cmd
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll)) { throw "Build failed for $arch." }
    Remove-Item $obj -Recurse -Force -ErrorAction SilentlyContinue
    $hash = (Get-FileHash $dll -Algorithm SHA256).Hash
    "$arch  $dll  SHA-256 $hash"
}
