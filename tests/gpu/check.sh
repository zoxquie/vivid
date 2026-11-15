#!/usr/bin/env bash
# Runs the black holo's GPU passes through Direct3D 11 (Windows, or Wine on Linux) and compares them with the
# CPU matcher. Needs mono, wine64, x86_64-w64-mingw32-g++ and xvfb-run on Linux.
set -euo pipefail
cd "$(dirname "$0")/../.."
REF=${REF:-/usr/lib/mono/4.8-api}
CSC=${CSC:-"dotnet $( (ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll /usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null || true) | sort -V | tail -1)"}
mkdir -p bin/holo-dump
$CSC -nologo -noconfig -nostdlib -unsafe+ -langversion:latest -target:exe -out:bin/HoloDump.exe -main:Vivid.HoloDump \
  -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Drawing.dll \
  -r:$REF/System.Windows.Forms.dll -r:$REF/System.Web.Extensions.dll -r:$REF/Facades/System.ValueTuple.dll \
  -resource:assets/brand/window.ico,brand.icon.ico -resource:assets/brand/mark.png,brand.mark.png \
  src/*.cs tests/gpu/HoloDump.cs
mono bin/HoloDump.exe bin/holo-dump
x86_64-w64-mingw32-g++ -O1 -static -o bin/holo_gpu.exe tests/gpu/holo_gpu.cpp -ld3d11 -ld3dcompiler_47
WINE=${WINE:-$(command -v wine64 || echo /usr/lib/wine/wine64)}
WINEDEBUG=-all WINEPREFIX=${WINEPREFIX:-/tmp/holo-wine} xvfb-run -a -s "-screen 0 640x480x24" "$WINE" bin/holo_gpu.exe bin/holo-dump
python3 tests/gpu/compare.py bin/holo-dump
