#!/usr/bin/env bash
# Fetches the third-party sources the client build needs into
# MuMain-099B/src/ThirdParty/. Safe to re-run.
#
# The version pair matters: SDL_mixer 3.2.x uses SDL_ALIGNED(16), a macro that
# only exists from SDL 3.4 on. SDL 3.2.x + SDL_mixer 3.2.x fails to compile.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TP="$ROOT/MuMain-099B/src/ThirdParty"

clone() { # name url branch
  local name="$1" url="$2" branch="$3"
  if [ -f "$TP/$name/CMakeLists.txt" ]; then
    echo "[skip] $name already present"
    return
  fi
  rm -rf "$TP/$name"
  git clone --depth 1 --branch "$branch" "$url" "$TP/$name"
}

clone SDL       https://github.com/libsdl-org/SDL.git       release-3.4.x
clone SDL_mixer https://github.com/libsdl-org/SDL_mixer.git release-3.2.x

# imgui is only needed for editor builds (-DENABLE_EDITOR=ON / *-mueditor presets).
if [ "${WITH_EDITOR:-0}" = "1" ]; then
  clone imgui https://github.com/ocornut/imgui.git master
fi

echo "Third-party sources ready in $TP"
