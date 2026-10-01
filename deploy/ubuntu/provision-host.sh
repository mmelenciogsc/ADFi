#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  printf '%s\n' 'Run this script through sudo.' >&2
  exit 1
fi
script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends aspnetcore-runtime-10.0 postgresql nginx openssl ca-certificates curl

if ! id adfi >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/adfi --create-home --shell /usr/sbin/nologin adfi
fi
install -d -o root -g root -m 0755 /opt/adfi/releases
install -d -o adfi -g adfi -m 0750 /var/lib/adfi
install -d -o root -g adfi -m 0750 /etc/adfi
install -d -o root -g adfi -m 0750 /etc/adfi/tls

systemctl enable --now postgresql
environment_file="/etc/adfi/adfi.env"
if [[ ! -f "${environment_file}" ]]; then
  database_password="$(openssl rand -hex 24)"
  if runuser -u postgres -- psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='adfi'" | grep -q 1; then
    runuser -u postgres -- psql -v ON_ERROR_STOP=1 -c "ALTER ROLE adfi WITH LOGIN PASSWORD '${database_password}'"
  else
    runuser -u postgres -- psql -v ON_ERROR_STOP=1 -c "CREATE ROLE adfi WITH LOGIN PASSWORD '${database_password}'"
  fi
  umask 0077
  {
    printf '%s\n' 'ASPNETCORE_ENVIRONMENT=Production'
    printf '%s\n' 'ASPNETCORE_URLS=http://127.0.0.1:5080'
    printf "%s\n" "AllowedHosts='BLACKOUT;localhost;127.0.0.1'"
    printf '%s\n' 'PublicBaseUrl=https://BLACKOUT/'
    printf '%s\n' 'Database__ApplyMigrationsAtStartup=false'
    printf "ConnectionStrings__ADFi='Host=127.0.0.1;Port=5432;Database=adfi;Username=adfi;Password=%s;SSL Mode=Prefer'\n" "${database_password}"
  } > "${environment_file}"
  chown root:adfi "${environment_file}"
  chmod 0640 "${environment_file}"
  unset database_password
else
  printf '%s\n' 'Existing /etc/adfi/adfi.env retained; database password was not rewritten.'
  if ! runuser -u postgres -- psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='adfi'" | grep -q 1; then
    printf '%s\n' 'The ADFi environment exists but its PostgreSQL role is missing; refusing an unsafe credential reset.' >&2
    exit 1
  fi
fi
if ! grep -q '^AllowedHosts=' "${environment_file}"; then
  printf "%s\n" "AllowedHosts='BLACKOUT;localhost;127.0.0.1'" >> "${environment_file}"
fi
if ! runuser -u postgres -- psql -tAc "SELECT 1 FROM pg_database WHERE datname='adfi'" | grep -q 1; then
  runuser -u postgres -- createdb --owner=adfi adfi
fi

if [[ ! -f /etc/adfi/tls/adfi.crt || ! -f /etc/adfi/tls/adfi.key ]]; then
  openssl req -x509 -newkey rsa:3072 -sha256 -days 365 -nodes \
    -keyout /etc/adfi/tls/adfi.key -out /etc/adfi/tls/adfi.crt \
    -subj '/CN=BLACKOUT' -addext 'subjectAltName=DNS:BLACKOUT'
  chown root:root /etc/adfi/tls/adfi.crt /etc/adfi/tls/adfi.key
  chmod 0644 /etc/adfi/tls/adfi.crt
  chmod 0600 /etc/adfi/tls/adfi.key
fi

install -o root -g root -m 0644 "${script_directory}/adfi-nginx.conf" /etc/nginx/sites-available/adfi
ln -sfn /etc/nginx/sites-available/adfi /etc/nginx/sites-enabled/adfi
rm -f /etc/nginx/sites-enabled/default
nginx -t
systemctl enable nginx
systemctl restart nginx
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
  ufw allow from 192.168.100.0/24 to any port 443 proto tcp comment 'ADFi HTTPS LAN'
fi

printf '%s\n' 'ADFi host prerequisites are provisioned. The LAN certificate is self-signed until a public domain is configured.'
