#!/usr/bin/env bash
# Richtet Onkel Bummel im LXC ein; create-lxc.sh ruft es als root im LXC auf. Mehrmals aufrufen schadet nicht:
# /etc/onkel-bummel/onkel-bummel.env und die Daten in /var/lib/onkel-bummel bleiben, wie sie sind.
#   bash /opt/onkel-bummel/deploy/proxmox/setup.sh
# Einstellungen über dieselben Umgebungsvariablen wie create-lxc.sh.
set -euo pipefail

# Alles steckt in main, damit bash das Skript ganz gelesen hat, bevor das erste Update den Checkout ändert
main() {
    REPO_DIR="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
    CONF_DIR=/etc/onkel-bummel
    DATA_DIR=/var/lib/onkel-bummel
    APP_UID=1654   # Benutzer "app" im aspnet-Image
    CT_HOSTNAME="${CT_HOSTNAME:-$(hostname)}"
    TS_AUTHKEY="${TS_AUTHKEY:-}"
    REPO_URL="${REPO_URL:-$(git -C "$REPO_DIR" remote get-url origin)}"
    REPO_BRANCH="${REPO_BRANCH:-main}"
    GIT_TOKEN="${GIT_TOKEN:-}"
    # Leer: der LXC baut das Image selbst; nicht gesetzt: wie bisher in source.env, sonst die Registry von GitHub Actions
    if [[ -z ${IMAGE_REPO+set} ]]; then
        if grep -q '^IMAGE_REPO=' "$CONF_DIR/source.env" 2>/dev/null; then
            IMAGE_REPO="$(sed -n 's/^IMAGE_REPO=//p' "$CONF_DIR/source.env")"
        else
            IMAGE_REPO=ghcr.io/marcel-b/onkel-bummel
        fi
    fi
    AUTO_UPDATE="${AUTO_UPDATE:-1}"
    UPDATE_INTERVAL="${UPDATE_INTERVAL:-5min}"

    [[ $EUID -eq 0 ]] || { echo "Bitte als root ausführen." >&2; exit 1; }
    export DEBIAN_FRONTEND=noninteractive

    info "Pakete installieren"
    apt-get update -qq
    apt-get install -y -qq ca-certificates curl git jq sqlite3 >/dev/null
    if ! command -v docker >/dev/null; then
        curl -fsSL https://get.docker.com | sh >/dev/null
    fi

    info "Tailscale einrichten"
    if ! command -v tailscale >/dev/null; then
        curl -fsSL https://tailscale.com/install.sh | sh >/dev/null
    fi
    systemctl enable --now tailscaled
    if ! tailscale status &>/dev/null; then
        if [[ -z $TS_AUTHKEY ]] || ! tailscale up --authkey "$TS_AUTHKEY" --hostname "$CT_HOSTNAME"; then
            [[ -n $TS_AUTHKEY ]] && echo "Tailscale lehnt den Auth Key ab (abgelaufen, verbraucht, widerrufen?)."
            echo "Zum Anmelden im Tailnet den folgenden Link öffnen:"
            tailscale up --hostname "$CT_HOSTNAME"
        fi
    fi
    TS_NAME="$(tailscale status --json | jq -r '.Self.DNSName' | sed 's/\.$//')"
    [[ -n $TS_NAME && $TS_NAME != null ]] || { echo "Der Name im Tailnet ließ sich nicht lesen." >&2; exit 1; }
    TAILNET="${TS_NAME#*.}"   # z. B. tail1234.ts.net
    OIDC_ISSUER="${OIDC_ISSUER:-https://identity.$TAILNET}"

    # Der Server löst Namen im Tailnet über MagicDNS auf. Startet Docker beim Booten vor Tailscale, bekommt der
    # Container die resolv.conf ohne 100.100.100.100 und findet weder Identity-Server noch Loki.
    install -d /etc/systemd/system/docker.service.d
    cat >/etc/systemd/system/docker.service.d/after-tailscale.conf <<'EOF'
[Unit]
After=tailscaled.service
Wants=tailscaled.service

[Service]
ExecStartPre=-/usr/bin/timeout 60 /bin/sh -c 'until tailscale status >/dev/null 2>&1; do sleep 2; done'
EOF
    systemctl daemon-reload

    info "HTTPS über tailscale serve"
    # Braucht MagicDNS und HTTPS-Zertifikate im Tailnet; fehlen sie, zeigt Tailscale einen Link zum Einschalten.
    tailscale serve --bg http://127.0.0.1:8080
    tailscale serve --bg --https=9464 http://127.0.0.1:9464

    info "Einstellungen schreiben"
    install -d -m 700 "$CONF_DIR"
    install -d -m 750 -g "$APP_UID" "$CONF_DIR/secrets"
    find "$CONF_DIR/secrets" -type f -exec chown "root:$APP_UID" {} + -exec chmod 640 {} +
    install -d -m 750 -o "$APP_UID" -g "$APP_UID" "$DATA_DIR"

    if [[ -f $CONF_DIR/onkel-bummel.env ]]; then
        echo "$CONF_DIR/onkel-bummel.env gibt es schon und bleibt, wie sie ist."
    else
        install -m 600 /dev/null "$CONF_DIR/onkel-bummel.env"
        {
            echo "# Einstellungen für Onkel Bummel als Abschnitt__Schlüssel (siehe docs/SPEC.md, Abschnitt Betrieb)."
            echo "# Nach Änderungen: cd $REPO_DIR/deploy && docker compose up -d"
            echo "# Werte werden von Docker Compose interpoliert: ein \$ im Wert als \$\$ schreiben."
            setting Server__PublicUrl "https://$TS_NAME"
            setting Oidc__Issuer "$OIDC_ISSUER"
            setting Oidc__WebClientSecret "${OIDC_WEB_CLIENT_SECRET:-}"
            echo
            echo "# APNs: Schlüssel als $CONF_DIR/secrets/apns.p8 ablegen (chown root:$APP_UID, chmod 640)"
            apns_key=""
            [[ -f $CONF_DIR/secrets/apns.p8 ]] && apns_key=/run/secrets/apns.p8
            setting Apns__KeyPath "$apns_key" /run/secrets/apns.p8
            setting Apns__KeyId "${APNS_KEY_ID:-}"
            setting Apns__TeamId "${APNS_TEAM_ID:-}"
            echo
            echo "# Monitoring: Logs zusätzlich an Loki (nur über das Tailnet erreichbar)"
            setting Monitoring__Loki__Url "${LOKI_URL:-}" http://monitoring:3100
            setting Monitoring__Loki__Host "$CT_HOSTNAME"
            echo
            echo "# Nächtliche Sicherung in die Nextcloud"
            setting Backup__NextcloudWebDavUrl "" "https://cloud.example.de/remote.php/dav/files/<benutzer>/Backups/onkel-bummel"
            setting Backup__NextcloudUserName ""
            setting Backup__NextcloudAppPassword ""
            setting Backup__Passphrase ""
        } >"$CONF_DIR/onkel-bummel.env"
    fi

    # Root only: enthält bei privatem Repository den Token, mit dem der Timer holt
    if [[ -z $GIT_TOKEN && -f $CONF_DIR/source.env ]]; then
        GIT_TOKEN="$(sed -n 's/^GIT_TOKEN=//p' "$CONF_DIR/source.env")"
    fi
    install -m 600 /dev/null "$CONF_DIR/source.env.new"
    cat >"$CONF_DIR/source.env.new" <<EOF
REPO_URL=$REPO_URL
REPO_BRANCH=$REPO_BRANCH
GIT_TOKEN=$GIT_TOKEN
SRC_DIR=$REPO_DIR
IMAGE_REPO=$IMAGE_REPO
EOF
    mv "$CONF_DIR/source.env.new" "$CONF_DIR/source.env"

    info "Update-Befehl und Timer einrichten"
    install -m 755 "$REPO_DIR/deploy/proxmox/update.sh" /usr/local/bin/onkel-bummel-update
    cat >/etc/systemd/system/onkel-bummel-update.service <<'EOF'
[Unit]
Description=Onkel Bummel: neuen Stand des Branches einspielen
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/onkel-bummel-update --if-changed
TimeoutStartSec=30min
EOF
    cat >/etc/systemd/system/onkel-bummel-update.timer <<EOF
[Unit]
Description=Onkel Bummel: regelmäßig nach neuen Commits sehen

[Timer]
OnBootSec=5min
OnUnitActiveSec=$UPDATE_INTERVAL
RandomizedDelaySec=30s

[Install]
WantedBy=timers.target
EOF
    systemctl daemon-reload

    if [[ -n $IMAGE_REPO ]]; then
        info "Server aus $IMAGE_REPO holen und starten"
    else
        info "Server bauen und starten (beim ersten Mal einige Minuten)"
    fi
    /usr/local/bin/onkel-bummel-update

    if [[ $AUTO_UPDATE == 1 ]]; then
        systemctl enable --now onkel-bummel-update.timer
    else
        systemctl disable --now onkel-bummel-update.timer 2>/dev/null || true
    fi

    info "Fertig"
    echo "  App:            https://$TS_NAME"
    echo "  Metriken:       https://$TS_NAME:9464/metrics"
    echo "  Identity:       $(sed -n 's/^Oidc__Issuer=//p' "$CONF_DIR/onkel-bummel.env")"
    echo "  Einstellungen:  $CONF_DIR/onkel-bummel.env"
    if [[ $AUTO_UPDATE == 1 ]]; then
        echo "  Updates:        alle $UPDATE_INTERVAL von $REPO_BRANCH, Log: journalctl -u onkel-bummel-update"
        echo "  Images:         ${IMAGE_REPO:-im LXC gebaut}"
    else
        echo "  Updates:        von Hand mit onkel-bummel-update"
    fi
}

info() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }

# Schreibt KEY=value; ohne Wert als auskommentierte Zeile (mit Beispiel $3), damit die App keinen leeren String bekommt
setting() {
    if [[ -n $2 ]]; then echo "$1=$2"; else echo "#$1=${3:-}"; fi
}

main "$@"
