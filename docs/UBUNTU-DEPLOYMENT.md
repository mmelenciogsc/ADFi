# Ubuntu deployment

Ubuntu is a deployment target, not a second source tree. Build/publish from `D:\ADFi`, then use `deploy\windows\deploy-ubuntu.cmd` from legacy Command Prompt. The script uses Windows OpenSSH and never accepts a password argument. Prefer a dedicated SSH key after initial bootstrap.

For a clean Ubuntu 24.04 host, copy `deploy/ubuntu/provision-host.sh` and `deploy/ubuntu/adfi-nginx.conf` to the same server directory and run the script through `sudo`. It installs ASP.NET Core Runtime 10, PostgreSQL and nginx, creates the unprivileged service/database identity, generates a database password directly into the protected server environment file, and creates a LAN-only self-signed TLS certificate. If UFW is active, only HTTPS from `192.168.100.0/24` is added. Replace the certificate, LAN restriction and `PublicBaseUrl` with production values before public launch. The script retains an existing environment file rather than overwriting provider secrets.

The target layout is `/opt/adfi/releases/<release-id>` with `/opt/adfi/current` as an atomic symlink, `/etc/adfi/adfi.env` as a root-owned, `adfi`-group-readable secret file, and `adfi-api.service` under systemd. PostgreSQL is local to Ubuntu unless documented otherwise. Migrations run as the unprivileged `adfi` account from the staged release before the current symlink switches. A failed readiness check restores and restarts the prior release when one exists. Backups must cover PostgreSQL plus public metadata; private Windows media is not copied to Ubuntu by default.

The desktop never bypasses TLS certificate validation. On a LAN development workstation only, copy
the exact public certificate from the server, verify its SHA-256 fingerprint, and explicitly add it
to the current Windows user's trust store:

```cmd
deploy\windows\trust-development-certificate.cmd path\to\adfi.crt https://BLACKOUT/health/ready
```

This is development trust for one certificate, not a production deployment mechanism. Remove it
from the current-user Trusted Root store when the development server is retired or the certificate
is replaced. A production deployment requires a publicly trusted certificate.

Required `/etc/adfi/adfi.env` values include `ASPNETCORE_URLS=http://127.0.0.1:5080`, `ConnectionStrings__ADFi`, `PublicBaseUrl`, SMTP settings and server-only PayPal settings. The public reverse proxy must terminate HTTPS; never expose port 5080 directly. The deployment command intentionally has no password argument and should use an authorized Windows OpenSSH key after one-time host bootstrap.
