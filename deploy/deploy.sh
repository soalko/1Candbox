#!/usr/bin/env bash
# Выкладка 1Candbox на сервер (запускать на Mac из корня проекта или откуда угодно):
#
#   ./deploy/deploy.sh              # сборка с SITE_URL → новый релиз на сервере → переключение
#   ./deploy/deploy.sh --no-build   # выложить уже собранную site/ (собранную с тем же SITE_URL)
#   ./deploy/deploy.sh --rollback   # вернуть предыдущий релиз
#
# Настройки берутся из deploy/deploy.env (не в git, образец: deploy/deploy.env.example) или из окружения:
#   DEPLOY_HOST  пользователь@сервер, например deploy@203.0.113.10
#   SITE_URL     адрес сайта, например https://1candbox.ru (нужен для canonical, sitemap, og:image)
#   APP_DIR      папка сайта на сервере (по умолчанию /var/www/1candbox)
#   KEEP         сколько релизов хранить (по умолчанию 5)
#
# Каждый релиз — отдельная папка releases/ГГГГММДД-ЧЧММСС, сайт отдаётся из симлинка current.
# Симлинк переключается одной операцией, поэтому посетитель не получит половину старых
# и половину новых файлов. Неизменённые файлы (а это почти весь _framework) не передаются
# по сети заново: rsync делает на них жёсткие ссылки из прошлого релиза, с прежней датой,
# и у браузеров остаются в кэше (ETag не меняется).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

[ -f deploy/deploy.env ] && . deploy/deploy.env
: "${DEPLOY_HOST:?Задайте DEPLOY_HOST=пользователь@сервер (или в deploy/deploy.env)}"
APP_DIR="${APP_DIR:-/var/www/1candbox}"
KEEP="${KEEP:-5}"

MODE="${1:-build}"
case "$MODE" in
  build|--no-build|--rollback) ;;
  *) echo "Неизвестный параметр: $MODE (есть --no-build и --rollback)"; exit 1 ;;
esac

if [ "$MODE" = --rollback ]; then
  ssh "$DEPLOY_HOST" bash -s -- "$APP_DIR" <<'EOF'
set -euo pipefail
app=$1
cur=$(basename "$(readlink "$app/current")")
prev=$(ls -1 "$app/releases" | sort | awk -v c="$cur" '$0 < c' | tail -n 1)
[ -n "$prev" ] || { echo "Нет релиза раньше $cur"; exit 1; }
ln -sfn "releases/$prev" "$app/current.new" && mv -T "$app/current.new" "$app/current"
echo "Откат: $cur → $prev"
EOF
  exit 0
fi

: "${SITE_URL:?Задайте SITE_URL=https://ваш-домен (или в deploy/deploy.env)}"
SITE_URL="${SITE_URL%/}"

if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
  echo "Внимание: в рабочей копии есть незакоммиченные изменения, на сервер попадёт именно они."
fi

if [ "$MODE" = build ]; then
  SITE_URL="$SITE_URL" ./build.sh
fi

# Защита от выкладки сборки без адреса сайта или с чужим адресом: без SITE_URL нет canonical,
# sitemap.xml и og:image, и поисковики видят сайт хуже
[ -f site/index.html ] || { echo "Нет site/index.html: сначала соберите сайт (./build.sh)"; exit 1; }
grep -q "<loc>$SITE_URL/</loc>" site/sitemap.xml 2>/dev/null \
  || { echo "site/ собран не с SITE_URL=$SITE_URL. Запустите без --no-build."; exit 1; }

REL="$(date +%Y%m%d-%H%M%S)"
echo "== Релиз $REL → $DEPLOY_HOST:$APP_DIR"

HAS_CURRENT=$(ssh "$DEPLOY_HOST" "mkdir -p '$APP_DIR/releases' && test -e '$APP_DIR/current' && echo yes || echo no")
LINK=()
[ "$HAS_CURRENT" = yes ] && LINK=(--link-dest="$APP_DIR/current/")

# -r -l: папки и симлинки; -c: сравнение по содержимому (даты у новой сборки всегда новые);
# без -t: дата файла на сервере — время первой выкладки этого содержимого, она и даёт ETag.
# Флаги подходят и для rsync из Homebrew, и для системного openrsync в macOS (он не знает --chmod).
STATS="$(rsync -rlcz ${LINK[@]+"${LINK[@]}"} --stats site/ "$DEPLOY_HOST:$APP_DIR/releases/$REL/")"
grep -E '^(Number of regular files transferred|Total bytes sent)' <<<"$STATS" || true

ssh "$DEPLOY_HOST" bash -s -- "$APP_DIR" "$REL" "$KEEP" <<'EOF'
set -euo pipefail
app=$1 rel=$2 keep=$3
test -f "$app/releases/$rel/index.html"
# Caddy работает от своего пользователя: файлы должны читаться всеми, независимо от прав на Mac
chmod -R u+rwX,go+rX,go-w "$app/releases/$rel"
ln -sfn "releases/$rel" "$app/current.new" && mv -T "$app/current.new" "$app/current"
# Старые релизы: оставить последние $keep (текущий — самый новый, он не удаляется)
cd "$app/releases" && ls -1 | sort | head -n -"$keep" | xargs -r rm -rf --
echo "Сейчас на сайте: $rel; релизов на сервере: $(ls -1 | wc -l)"
EOF

echo "Готово: $SITE_URL"
echo "Проверка сжатия: curl -sI -H 'Accept-Encoding: br' $SITE_URL/_framework/dotnet.native.wasm | grep -i -E 'content-(encoding|type)'"
