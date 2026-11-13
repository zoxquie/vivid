#!/usr/bin/env bash
# Builds tests/gpu/GpuSmoke.exe (Windows x64). Run it on Windows or under Wine with Wine Mono.
set -euo pipefail
cd "$(dirname "$0")/../.."
REF=${REF:-/usr/lib/mono/4.8-api}
CSC=${CSC:-"dotnet $( (ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll /usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null || true) | sort -V | tail -1)"}
mkdir -p bin
$CSC -nologo -noconfig -nostdlib -unsafe+ -langversion:latest -platform:x64 -target:exe -out:bin/GpuSmoke.exe -main:Vivid.GpuSmoke \
  -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Drawing.dll \
  -r:$REF/System.Windows.Forms.dll -r:$REF/System.Web.Extensions.dll -r:$REF/Facades/System.ValueTuple.dll \
  -resource:assets/brand/window.ico,brand.icon.ico -resource:assets/brand/mark.png,brand.mark.png \
  $(ls src/*.cs | grep -v Program.cs) src/Program.cs tests/gpu/GpuSmoke.cs
echo built bin/GpuSmoke.exe
