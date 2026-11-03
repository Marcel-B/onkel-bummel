# Onkel Bummel auf Proxmox (LXC)

`create-lxc.sh` legt auf dem Proxmox-Host einen unprivilegierten Debian-LXC an. Darin laufen Docker und Tailscale, der Server als Docker-Container und ein Timer, der neue Commits auf `main` selbst einspielt. Danach ist Onkel Bummel unter `https://onkel-bummel.<tailnet>.ts.net` erreichbar, nur im Tailnet.

## Vorher

- Im Tailnet MagicDNS und HTTPS-Zertifikate einschalten (Admin-Konsole > DNS).
- Einen Auth Key anlegen (Settings > Keys > Generate auth key). Ohne Key zeigt das Skript einen Login-Link.
- Bei privatem Repository: Fine-grained Token auf GitHub mit Zugriff nur auf `onkel-bummel` und „Contents: Read-only“.

## Anlegen

```bash
# auf dem Mac
scp deploy/proxmox/create-lxc.sh root@pve:
```

```bash
# in der Proxmox-Shell als root
TS_AUTHKEY=tskey-auth-... GIT_TOKEN=github_pat_... bash create-lxc.sh
```

Das Skript holt das Repository in den LXC und ruft dort `setup.sh` auf. Am Ende stehen die Adressen da. Bricht es ab, mit derselben `CTID=…` erneut aufrufen: Es macht dort weiter, Einstellungen und Daten bleiben.

| Variable | Standard | Bedeutung |
|---|---|---|
| `CTID` | nächste freie ID | Container-ID |
| `CT_HOSTNAME` | `onkel-bummel` | Hostname, auch der Name im Tailnet |
| `STORAGE` / `TEMPLATE_STORAGE` | `local-lvm` / `local` | Speicher für Disk und Debian-Vorlage |
| `BRIDGE`, `IP`, `GATEWAY` | `vmbr0`, `dhcp` | z. B. `IP=192.168.1.50/24 GATEWAY=192.168.1.1` |
| `CORES` / `MEMORY` / `DISK` | `2` / `2048` / `12` | Ressourcen; der Build braucht etwa 1,5 GiB RAM |
| `SSH_PUBLIC_KEY_FILE` | – | optional, für SSH als root |
| `TS_AUTHKEY` | – | Auth Key fürs Tailnet (`tskey-auth-…`) |
| `REPO_URL` / `REPO_BRANCH` | `https://github.com/Marcel-B/onkel-bummel.git` / `main` | Quelle der Updates |
| `GIT_TOKEN` | – | nur bei privatem Repository |
| `AUTO_UPDATE` / `UPDATE_INTERVAL` | `1` / `5min` | neue Commits automatisch einspielen, wie oft nachsehen |
| `OIDC_ISSUER` | `https://identity.<tailnet>.ts.net` | Identity-Server |
| `OIDC_WEB_CLIENT_SECRET` | – | Secret von `onkel-bummel-web`, geht auch später |
| `APNS_KEY_FILE`, `APNS_KEY_ID`, `APNS_TEAM_ID` | – | APNs-Schlüssel (Pfad auf dem Proxmox-Host), geht auch später |
| `LOKI_URL` | – | z. B. `http://monitoring:3100` |

## Im LXC

| Was | Wo |
|---|---|
| Einstellungen (`Abschnitt__Schlüssel`) | `/etc/onkel-bummel/onkel-bummel.env` |
| APNs-Schlüssel | `/etc/onkel-bummel/secrets/apns.p8` (`chown root:1654`, `chmod 640`) |
| Repository und Token für Updates | `/etc/onkel-bummel/source.env` (nur root) |
| Datenbank und Anhänge | `/var/lib/onkel-bummel` |
| Checkout | `/opt/onkel-bummel` |

Nach einer Änderung an den Einstellungen:

```bash
pct exec <CTID> -- docker compose -f /opt/onkel-bummel/deploy/docker-compose.yml up -d
```

## Updates

`onkel-bummel-update.timer` sieht alle 5 Minuten nach, ob es auf `main` einen neuen Commit gibt. GitHub muss den LXC dafür nicht erreichen. Für einen neuen Commit:

1. Commit holen und die Tests im Docker-Build ausführen (Stage `test` im `backend/Dockerfile`). Scheitern sie, läuft die bisherige Version weiter.
2. Image bauen. Scheitert der Build, läuft die bisherige Version weiter.
3. Datenbank sichern (SQLite-Online-Backup) und den Container neu starten.
4. Antwortet `/health` nach 90 Sekunden nicht, kommen das vorige Image und die gesicherte Datenbank zurück. Nachrichten aus diesen Minuten gehen dabei verloren.

Ein gescheiterter Commit wird nicht bei jedem Lauf neu versucht, erst der nächste Commit oder ein Aufruf von Hand.

```bash
pct exec <CTID> -- onkel-bummel-update                        # jetzt einspielen
pct exec <CTID> -- journalctl -u onkel-bummel-update -n 80    # Log der letzten Updates
pct exec <CTID> -- systemctl list-timers onkel-bummel-update  # nächster Lauf
pct exec <CTID> -- systemctl disable --now onkel-bummel-update.timer   # automatische Updates aus
```
