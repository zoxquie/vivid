#!/usr/bin/env bash
# Compiles the app sources with the test entry point and runs them under Mono.
set -euo pipefail
cd "$(dirname "$0")/.."
REF=${REF:-/usr/lib/mono/4.8-api}
CSC=${CSC:-"dotnet $( (ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll /usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null || true) | sort -V | tail -1)"}
mkdir -p bin
$CSC -nologo -noconfig -nostdlib -unsafe+ -langversion:latest -target:exe -out:bin/VividTests.exe -main:VividTests \
  -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Drawing.dll \
  -r:$REF/System.Windows.Forms.dll -r:$REF/System.Web.Extensions.dll -r:$REF/Facades/System.ValueTuple.dll \
  -resource:assets/brand/window.ico,brand.icon.ico -resource:assets/brand/mark.png,brand.mark.png \
  src/*.cs tests/Tests.cs
# WinForms timers need a display under Mono; use a virtual one when there isn't one
if [ -z "${DISPLAY:-}" ] && command -v xvfb-run >/dev/null; then xvfb-run -a mono bin/VividTests.exe; else mono bin/VividTests.exe; fi
