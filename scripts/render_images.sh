#!/usr/bin/env bash
# Растровые иконки и картинка для превью ссылок из исходников:
#   page/public/favicon.svg     → favicon.ico (16, 32, 48), favicon-120.png (Яндекс), apple-touch-icon.png (180)
#   page/assets/og-image.html   → og-image.png (1200×630, og:image)
# Результат кладётся в page/public/ и коммитится: при обычной сборке (build.sh) этот скрипт не нужен.
# Нужен Google Chrome (headless) и python3. Шрифты для og-image грузятся с Google Fonts.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUB="$ROOT/page/public"
CHROME="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
[ -x "$CHROME" ] || CHROME="$(command -v google-chrome || command -v chromium || true)"
[ -n "$CHROME" ] || { echo "Не найден Google Chrome (задайте CHROME=/путь/к/chrome)"; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

shot() { # shot <html> <png> <ширина> <высота>
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
    --default-background-color=00000000 --virtual-time-budget=8000 \
    --window-size="$3,$4" --screenshot="$2" "file://$1" >/dev/null 2>&1
}

# Иконка во весь кадр. bg — фон под скруглёнными углами: прозрачный для вкладки,
# сплошной для apple-touch-icon (iOS сам скругляет углы и закрашивает прозрачное чёрным).
# Размер картинки задаётся в пикселях, а не 100vw: headless Chrome не делает окно уже ~500 px,
# снимок обрезается по --window-size.
icon() { # icon <png> <размер> <фон>
  cat > "$TMP/icon.html" <<EOF
<!DOCTYPE html><html><body style="margin:0;background:$3;width:$2px;height:$2px">
<img src="file://$PUB/favicon.svg" style="display:block;width:$2px;height:$2px"></body></html>
EOF
  shot "$TMP/icon.html" "$1" "$2" "$2"
}
for s in 16 32 48; do icon "$TMP/icon-$s.png" $s transparent; done
icon "$PUB/favicon-120.png" 120 transparent
icon "$PUB/apple-touch-icon.png" 180 '#1f6f5c'
shot "$ROOT/page/assets/og-image.html" "$PUB/og-image.png" 1200 630

# favicon.ico: контейнер ICO с PNG внутри (поддерживают все браузеры с Windows Vista)
python3 - "$PUB/favicon.ico" "$TMP/icon-16.png" "$TMP/icon-32.png" "$TMP/icon-48.png" <<'PY'
import struct, sys
out, *pngs = sys.argv[1:]
data = [open(p, 'rb').read() for p in pngs]
head = struct.pack('<HHH', 0, 1, len(data))
offset = 6 + 16 * len(data)
entries = b''
for blob in data:
    w, h = struct.unpack('>II', blob[16:24])          # размеры из заголовка IHDR
    entries += struct.pack('<BBBBHHII', w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
    offset += len(blob)
open(out, 'wb').write(head + entries + b''.join(data))
PY

ls -l "$PUB"/favicon.ico "$PUB"/favicon-120.png "$PUB"/apple-touch-icon.png "$PUB"/og-image.png
