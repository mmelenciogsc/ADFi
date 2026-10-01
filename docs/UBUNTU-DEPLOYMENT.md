# Ubuntu deployment

Ubuntu is a deployment target, not a second source tree. Build/publish from `D:\ADFi`, then use `deploy\windows\deploy-ubuntu.cmd` from legacy Command Prompt. The script uses Windows OpenSSH and never accepts a password argument. Prefer a dedicated SSH key after initial bootstrap.

The target layout is `/opt/adfi/releases/<release-id>` with `/opt/adfi/current` as an atomic symlink, `/etc/adfi/adfi.env` as a root-readable secret file, and `adfi-api.service` under systemd. PostgreSQL is local to Ubuntu unless documented otherwise. Migrations run once from the published release before the service switches. Health must pass before an old release is removed. Backups must cover PostgreSQL plus public metadata; private Windows media is not copied to Ubuntu by default.

