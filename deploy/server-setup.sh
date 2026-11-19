#!/usr/bin/env bash
# Однократная настройка чистого сервера Ubuntu 22.04/24.04 под 1Candbox. Запускать от root:
#
#   scp deploy/server-setup.sh deploy/Caddyfile root@СЕРВЕР:/root/
#   ssh root@СЕРВЕР 'bash /root/server-setup.sh 1candbox.ru'
#
# Что делает (повторный запуск безопасен):
#   - обновляет систему, включает автоматические обновления безопасности;
#   - ставит Caddy из официального репозитория и rsync;
#   - создаёт пользователя deploy без sudo: он может только писать в /var/www/1candbox;
#     ключи SSH копируются от root;
#   - запрещает вход по паролю (только если у root уже есть ключ, иначе можно потерять доступ);
#   - включает файрвол: SSH, 80, 443 (и 443/udp для HTTP/3);
#   - кладёт Caddyfile с вашим доменом и перезапускает Caddy.
#
# До запуска: A-запись домена (и www) должна указывать на IP сервера, иначе Caddy не получит
# сертификат сразу. Он будет повторять попытки сам, поэтому DNS можно поправить и после.
set -euo pipefail

DOMAIN="${1:?Укажите домен: bash server-setup.sh example.ru}"
DOMAIN="${DOMAIN#https://}"; DOMAIN="${DOMAIN#http://}"; DOMAIN="${DOMAIN#www.}"; DOMAIN="${DOMAIN%%/*}"
DEPLOY_USER="${DEPLOY_USER:-deploy}"
SSH_PORT="${SSH_PORT:-22}"
APP_DIR=/var/www/1candbox
HERE="$(cd "$(dirname "$0")" && pwd)"

[ "$(id -u)" = 0 ] || { echo "Запускайте от root"; exit 1; }
[ -f "$HERE/Caddyfile" ] || { echo "Рядом со скриптом нет Caddyfile (скопируйте deploy/Caddyfile)"; exit 1; }

echo "== Обновление системы"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get -yq upgrade
apt-get install -yq debian-keyring debian-archive-keyring apt-transport-https curl gnupg \
  ufw unattended-upgrades rsync

echo "== Автоматические обновления безопасности"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF

echo "== Caddy"
if ! command -v caddy >/dev/null; then
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
    | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
    > /etc/apt/sources.list.d/caddy-stable.list
  chmod o+r /usr/share/keyrings/caddy-stable-archive-keyring.gpg /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -q
  apt-get install -yq caddy
fi
caddy version

echo "== Пользователь $DEPLOY_USER и папка сайта"
if ! id "$DEPLOY_USER" >/dev/null 2>&1; then
  adduser --disabled-password --gecos "" "$DEPLOY_USER"
fi
install -d -m 700 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "/home/$DEPLOY_USER/.ssh"
if [ ! -s "/home/$DEPLOY_USER/.ssh/authorized_keys" ] && [ -s /root/.ssh/authorized_keys ]; then
  install -m 600 -o "$DEPLOY_USER" -g "$DEPLOY_USER" /root/.ssh/authorized_keys "/home/$DEPLOY_USER/.ssh/authorized_keys"
fi
install -d -m 755 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "$APP_DIR" "$APP_DIR/releases"

echo "== SSH: только по ключу"
if [ -s /root/.ssh/authorized_keys ]; then
  # 10- раньше 50-cloud-init.conf: sshd берёт первое значение параметра
  cat > /etc/ssh/sshd_config.d/10-1candbox.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
EOF
  sshd -t && systemctl reload ssh
else
  echo "!! У root нет ключа в /root/.ssh/authorized_keys: вход по паролю оставлен включённым."
  echo "!! Добавьте ключ (ssh-copy-id root@СЕРВЕР) и запустите скрипт ещё раз."
fi

echo "== Файрвол"
ufw allow "$SSH_PORT/tcp"
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp
ufw --force enable
ufw status

echo "== Caddyfile для $DOMAIN"
sed "s/__DOMAIN__/$DOMAIN/g" "$HERE/Caddyfile" > /etc/caddy/Caddyfile
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
systemctl enable --now caddy
systemctl reload caddy

echo
echo "Готово. Теперь с Mac:  DEPLOY_HOST=$DEPLOY_USER@$(hostname -I | awk '{print $1}') SITE_URL=https://$DOMAIN ./deploy/deploy.sh"
