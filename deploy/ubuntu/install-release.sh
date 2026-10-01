#!/usr/bin/env bash
set -euo pipefail

source_directory="${1:?Published source directory is required}"
service_file="${2:-}"
release_id="$(date -u +%Y%m%dT%H%M%SZ)"
release_directory="/opt/adfi/releases/${release_id}"

if [[ "${EUID}" -ne 0 ]]; then
  printf '%s\n' 'This installer must run through sudo.' >&2
  exit 1
fi

install -d -o root -g root -m 0755 /opt/adfi/releases
install -d -o adfi -g adfi -m 0750 /var/lib/adfi
install -d -o root -g adfi -m 0750 /etc/adfi
install -d -o root -g root -m 0755 "${release_directory}"
cp -a "${source_directory}/." "${release_directory}/"
chown -R root:root "${release_directory}"
find "${release_directory}" -type d -exec chmod 0755 {} +
find "${release_directory}" -type f -exec chmod 0644 {} +

if [[ ! -f /etc/adfi/adfi.env ]]; then
  printf '%s\n' 'Missing /etc/adfi/adfi.env; create the protected server configuration before activation.' >&2
  exit 1
fi

if [[ -n "${service_file}" ]]; then
  install -o root -g root -m 0644 "${service_file}" /etc/systemd/system/adfi-api.service
fi

set -a
# The protected file is administered on the server and must contain shell-compatible KEY=VALUE lines.
source /etc/adfi/adfi.env
set +a
runuser -u adfi --preserve-environment -- /usr/bin/dotnet "${release_directory}/ADFi.Api.dll" --migrate-only

previous_release="$(readlink -f /opt/adfi/current 2>/dev/null || true)"
ln -sfn "${release_directory}" /opt/adfi/current
systemctl daemon-reload
systemctl restart adfi-api.service
ready=false
for _ in {1..15}; do
  if systemctl is-active --quiet adfi-api.service \
      && curl --fail --silent --show-error http://127.0.0.1:5080/health/ready >/dev/null; then
    ready=true
    break
  fi
  sleep 2
done

if [[ "${ready}" != true ]]; then
  if [[ -n "${previous_release}" && -d "${previous_release}" ]]; then
    ln -sfn "${previous_release}" /opt/adfi/current
    systemctl restart adfi-api.service
  fi
  printf '%s\n' 'New release failed readiness; the prior release was restored when available.' >&2
  exit 1
fi

printf 'Activated ADFi release %s\n' "${release_id}"
