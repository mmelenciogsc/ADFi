#!/usr/bin/env bash
set -euo pipefail

source_directory="${1:?Published source directory is required}"
release_id="$(date -u +%Y%m%dT%H%M%SZ)"
release_directory="/opt/adfi/releases/${release_id}"

if [[ "${EUID}" -ne 0 ]]; then
  printf '%s\n' 'This installer must run through sudo.' >&2
  exit 1
fi

install -d -o root -g root -m 0755 /opt/adfi/releases
install -d -o root -g root -m 0755 "${release_directory}"
cp -a "${source_directory}/." "${release_directory}/"
chown -R root:root "${release_directory}"
find "${release_directory}" -type d -exec chmod 0755 {} +
find "${release_directory}" -type f -exec chmod 0644 {} +

if [[ ! -f /etc/adfi/adfi.env ]]; then
  printf '%s\n' 'Missing /etc/adfi/adfi.env; create the protected server configuration before activation.' >&2
  exit 1
fi

ln -sfn "${release_directory}" /opt/adfi/current
systemctl daemon-reload
systemctl restart adfi-api.service
systemctl is-active --quiet adfi-api.service
curl --fail --silent --show-error http://127.0.0.1:5080/health/ready >/dev/null
printf 'Activated ADFi release %s\n' "${release_id}"

