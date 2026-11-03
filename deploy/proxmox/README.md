# Onkel Bummel auf Proxmox (LXC)

`create-lxc.sh` legt auf dem Proxmox-Host einen unprivilegierten Debian-LXC an. Darin laufen Docker und Tailscale, der Server als Docker-Container und ein Timer, der neue Commits auf `main` selbst einspielt. Danach ist Onkel Bummel unter `https://onkel-bummel.<tailnet>.ts.net` erreichbar, nur im Tailnet.

## Vorher

- Im Tailnet MagicDNS und HTTPS-Zertifikate einschalten (Admin-Konsole > DNS).
- Einen Auth Key anlegen (Settings > Keys > Generate auth key). Ohne Key zeigt das Skript einen Login-Link.
- Nur bei einem privaten Fork: Fine-grained Token mit „Contents: Read-only“ als `GIT_TOKEN`.

## Anlegen

```bash
# in der Proxmox-Shell als root
TS_AUTHKEY=tskey-auth-... bash <(curl -fsSL https://raw.githubusercontent.com/Marcel-B/onkel-bummel/main/deploy/proxmox/create-lxc.sh)
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

## Publish-Tokens

Sender brauchen einen Token. Am einfachsten legt man ihn in der Web-Oberfläche unter Verwaltung → Publish-Tokens an, oder im LXC auf der Kommandozeile; die Topics kommagetrennt, `'*'` für alle:

```bash
pct exec <CTID> -- docker compose -f /opt/onkel-bummel/deploy/docker-compose.yml exec onkel-bummel \
  dotnet OnkelBummel.Api.dll --create-token grafana alarme
```

`--list-tokens` zeigt alle Tokens mit ihrer ID und wann sie zuletzt benutzt wurden, `--delete-token <id>` löscht einen.

## Sicherung in die Nextcloud

Jede Nacht um `Backup__At` (Standard 03:30, Zeit des Containers) kopiert der Server die Datenbank über SQLites Online-Backup, packt sie mit allen Anhängen als ZIP und lädt sie per WebDAV in einen Nextcloud-Ordner, wie `identity-backup` beim Identity-Server. Es entstehen `onkel-bummel-mon.zip` … `onkel-bummel-sun.zip` und `onkel-bummel-2026-10.zip` (der letzte Stand jedes Monats); in der Nextcloud wird nichts gelöscht. Die Metrik `onkelbummel_backup_last_success_timestamp_seconds` speist den Alarm „Sicherung zu alt“.

Einrichten: in der Nextcloud unter Einstellungen > Sicherheit > Geräte & Sitzungen ein App-Passwort anlegen und in `/etc/onkel-bummel/onkel-bummel.env` eintragen, danach `docker compose up -d`:

```bash
Backup__NextcloudWebDavUrl=https://cloud.example.de/remote.php/dav/files/<benutzer>/Backups/onkel-bummel
Backup__NextcloudUserName=<benutzer>
Backup__NextcloudAppPassword=<app-passwort>
Backup__Passphrase=<optional, verschlüsselt die Archive mit AES-256>
```

Der Ordner `onkel-bummel` wird angelegt, der darüber (`Backups`) muss existieren. Ohne diese Werte bleibt die Sicherung aus. Die Passphrase außerhalb des LXC aufheben, etwa im Passwortmanager.

Zurückspielen:

```bash
# verschlüsselte Archive zuerst entschlüsseln
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in onkel-bummel-mon.zip.enc -out onkel-bummel-mon.zip
# im LXC: Server anhalten, Daten ersetzen, wieder starten
docker compose -f /opt/onkel-bummel/deploy/docker-compose.yml stop
cd /var/lib/onkel-bummel && rm -rf attachments onkel-bummel.db onkel-bummel.db-wal onkel-bummel.db-shm
unzip /root/onkel-bummel-mon.zip -d /var/lib/onkel-bummel && chown -R 1654:1654 /var/lib/onkel-bummel
docker compose -f /opt/onkel-bummel/deploy/docker-compose.yml start
```
