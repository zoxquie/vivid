#!/usr/bin/env bash
# Builds Fjord Vision (bin/Vivid.exe; Windows x64, .NET Framework 4.8 — already part of Windows 10/11).
# The file and assembly keep the name Vivid so copies installed before the rebrand keep auto-updating.
# Needs: a C# compiler (Roslyn csc) and .NET Framework 4.8 reference assemblies.
#  - On Linux: apt install dotnet-sdk-8.0 mono-devel   (uses Mono's 4.8-api reference assemblies)
#  - On GitHub: .github/workflows/release.yml runs this when the version in src/AppInfo.cs changes.
set -euo pipefail
cd "$(dirname "$0")"

REF=${REF:-/usr/lib/mono/4.8-api}
CSC=${CSC:-"dotnet $( (ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll /usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null || true) | sort -V | tail -1)"}
mkdir -p bin

# Icon and logo badge come from assets/brand/fjord-logo.jpg (regenerate: python3 tools/make_icon.py)
[ -f assets/fjord-vision.ico ] && [ -f assets/brand/window.ico ] && [ -f assets/brand/mark.png ] || python3 tools/make_icon.py

# Optional: screenshots in assets/presets (grass.jpg, winter.jpg, desert.jpg, night-vision.jpg,
# custom.jpg, preview.jpg) are built into the exe as default card / preview images.
RES=("-resource:assets/brand/window.ico,brand.icon.ico" "-resource:assets/brand/mark.png,brand.mark.png")
if [ -d assets/presets ]; then
  for f in assets/presets/*; do
    case "${f,,}" in *.jpg|*.jpeg|*.png|*.bmp) RES+=("-resource:$f,preset.$(basename "$f")");; esac
  done
fi

$CSC -nologo -noconfig -nostdlib -unsafe+ -langversion:latest -optimize+ -warn:4 \
  -platform:x64 -target:winexe -out:bin/Vivid.exe \
  -win32icon:assets/fjord-vision.ico -win32manifest:src/app.manifest \
  -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll \
  -r:$REF/System.Drawing.dll -r:$REF/System.Windows.Forms.dll \
  -r:$REF/System.Web.Extensions.dll -r:$REF/Facades/System.ValueTuple.dll \
  "${RES[@]}" src/*.cs

echo "Built bin/Vivid.exe (Fjord Vision)"
