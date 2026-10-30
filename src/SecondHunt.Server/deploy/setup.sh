#!/usr/bin/env bash
# One-time setup of a fresh Ubuntu 24.04 server for SecondHuntServer (docs/ONLINE_PLAY.md O4; DEPLOY.md). Run as root
# from the folder deploy.ps1 -Setup copied over:
#   DOMAIN=play.example.com bash setup.sh     HTTPS on that name (its DNS record must point at this server)
#   bash setup.sh                             no domain yet: plain ws:// on port 80 of the server's IP, for tests
set -euo pipefail
cd "$(dirname "$0")"
DOMAIN="${DOMAIN:-}"

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get -y upgrade
# caddy: the web front (HTTPS certificates on its own); sqlite3: the nightly backup; unattended-upgrades: security fixes
apt-get -y install caddy sqlite3 ufw unattended-upgrades curl

id secondhunt >/dev/null 2>&1 || useradd --system --home-dir /var/lib/secondhunt --shell /usr/sbin/nologin secondhunt
install -d -m 755 /opt/secondhunt
install -d -o secondhunt -g secondhunt -m 750 /var/lib/secondhunt /var/lib/secondhunt/backups /var/lib/secondhunt/.net
[ -f /opt/secondhunt/words.txt ] || touch /opt/secondhunt/words.txt # the name filter list (open decision O-3)
install -d -m 700 /etc/secondhunt # turn.env (the Cloudflare TURN key) goes here by hand, chmod 600 (DEPLOY.md)

install -m 644 secondhunt.service /etc/systemd/system/secondhunt.service
if [ -n "$DOMAIN" ]; then
    sed "s/{SITE}/$DOMAIN/" Caddyfile > /etc/caddy/Caddyfile
else
    sed "s/{SITE}/:80/" Caddyfile > /etc/caddy/Caddyfile
fi

# firewall: SSH, HTTP (certificate checks) + HTTPS, and the match relay (UDP)
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 47780/udp
ufw --force enable

# SSH: keys only (deploy.ps1 is already in with the deploy key by now); the root password still works on Linode's
# LISH console. 00- sorts before the image's own drop-ins (sshd keeps the first value it reads)
install -d /etc/ssh/sshd_config.d
printf 'PasswordAuthentication no\nKbdInteractiveAuthentication no\n' > /etc/ssh/sshd_config.d/00-secondhunt.conf
sshd -t
systemctl try-reload-or-restart ssh

# nightly database copy at 04:30 server time, the last 14 kept
install -m 755 backup.sh /usr/local/bin/secondhunt-backup
echo "30 4 * * * secondhunt /usr/local/bin/secondhunt-backup" > /etc/cron.d/secondhunt-backup

systemctl daemon-reload
systemctl enable secondhunt
systemctl restart caddy
if [ -x /opt/secondhunt/SecondHuntServer ]; then
    systemctl restart secondhunt
fi
echo "setup done${DOMAIN:+ for $DOMAIN}; deploy.ps1 puts the server on and starts it"
