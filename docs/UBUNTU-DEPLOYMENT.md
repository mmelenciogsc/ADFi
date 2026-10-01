# Ubuntu deployment

Ubuntu is a deployment target, not a second source tree. Build/publish from `D:\ADFi`, then use `deploy\windows\deploy-ubuntu.cmd` from legacy Command Prompt. The script uses Windows OpenSSH and never accepts a password argument. Prefer a dedicated SSH key after initial bootstrap.

The target layout is `/opt/adfi/releases/<release-id>` with `/opt/adfi/current` as an atomic symlink, `/etc/adfi/adfi.env` as a root-owned, `adfi`-group-readable secret file, and `adfi-api.service` under systemd. PostgreSQL is local to Ubuntu unless documented otherwise. Migrations run as the unprivileged `adfi` account from the staged release before the current symlink switches. A failed readiness check restores and restarts the prior release when one exists. Backups must cover PostgreSQL plus public metadata; private Windows media is not copied to Ubuntu by default.

Required `/etc/adfi/adfi.env` values include `ASPNETCORE_URLS=http://127.0.0.1:5080`, `ConnectionStrings__ADFi`, `PublicBaseUrl`, SMTP settings and server-only PayPal settings. The public reverse proxy must terminate HTTPS; never expose port 5080 directly. The deployment command intentionally has no password argument and should use an authorized Windows OpenSSH key after one-time host bootstrap.
