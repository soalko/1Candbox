#!/usr/bin/env bash
# Сборка 1Candbox: движок OneScript под WebAssembly + страница.
# Нужны: .NET SDK 8 или новее, Node.js 18+, git, python3, доступ к github.com, nuget.org и npmjs.org.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

if [ ! -d onescript ]; then
  git clone --depth 1 https://github.com/EvilBeaver/OneScript.git onescript
fi
python3 scripts/patch_onescript.py onescript

dotnet publish browser/OneScriptWasm.csproj -c Release -o out

# Фронтенд. app.js — главная, embed-app.js — виджет embed.html (общий код в chunks/),
# embed.js — загрузчик веб-компонента <bsl-sandbox> для чужих страниц (один файл без модулей).
# .bsl — заглушки БСП: rules.js берёт из них список функций, чтобы не предупреждать о них.
(cd page && npm install --no-audit --no-fund \
  && rm -rf dist \
  && npx esbuild src/app.js src/embed-app.js --bundle --splitting --format=esm --minify --target=es2022 \
       --loader:.bsl=text --metafile=meta.json --outdir=dist '--chunk-names=chunks/[name]-[hash]' \
  && npx esbuild src/embed.js --bundle --format=iife --minify --target=es2020 --outfile=dist/embed.js)

rm -rf site
cp -R page/public site
cp -R page/dist/. site/
cp -R out/wwwroot/_framework site/_framework
# Уведомления о лицензиях и страница «Лицензии» (требование MPL-2.0 и MIT)
python3 scripts/collect_licenses.py
# Страницы примеров, общий подвал (главная, примеры, «Лицензии»), sitemap.xml и robots.txt.
# Адрес сайта для canonical и sitemap: SITE_URL=https://… ./build.sh
node page/build-pages.mjs site
echo
echo "Готово: папка site/. Запуск локально:  python3 -m http.server 8080 --directory site  →  http://localhost:8080"
