<#
.SYNOPSIS
  Fetches the third-party sources the client build needs into
  MuMain-099B\src\ThirdParty\. Safe to re-run.

.PARAMETER WithEditor
  Also fetch imgui (only needed for the *-mueditor presets).

.NOTES
  The version pair matters: SDL_mixer 3.2.x uses SDL_ALIGNED(16), a macro that
  only exists from SDL 3.4 on. SDL 3.2.x + SDL_mixer 3.2.x fails to compile.
#>
param([switch]$WithEditor)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tp   = Join-Path $root 'MuMain-099B\src\ThirdParty'

function Get-Dep($name, $url, $branch) {
    $dir = Join-Path $tp $name
    if (Test-Path (Join-Path $dir 'CMakeLists.txt')) {
        Write-Host "[skip] $name already present"
        return
    }
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    git clone --depth 1 --branch $branch $url $dir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed for $name" }
}

Get-Dep 'SDL'       'https://github.com/libsdl-org/SDL.git'       'release-3.4.x'
Get-Dep 'SDL_mixer' 'https://github.com/libsdl-org/SDL_mixer.git' 'release-3.2.x'
if ($WithEditor) { Get-Dep 'imgui' 'https://github.com/ocornut/imgui.git' 'master' }

Write-Host "Third-party sources ready in $tp"
