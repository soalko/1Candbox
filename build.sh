#!/usr/bin/env bash
# Сборка песочницы 1С: движок OneScript под WebAssembly + страница.
# Нужны: .NET SDK 8 или новее, Node.js 18+, git, python3, доступ к github.com, nuget.org и npmjs.org.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

if [ ! -d onescript ]; then
  git clone --depth 1 https://github.com/EvilBeaver/OneScript.git onescript
fi
python3 scripts/patch_onescript.py onescript

dotnet publish browser/OneScriptWasm.csproj -c Release -o out

(cd page && npm install --no-audit --no-fund && npx esbuild src/app.js --bundle --format=esm --minify --target=es2020 --outfile=public/app.js)

rm -rf site
cp -R page/public site
cp -R out/wwwroot/_framework site/_framework
echo
echo "Готово: папка site/. Запуск локально:  python3 -m http.server 8080 --directory site  →  http://localhost:8080"
