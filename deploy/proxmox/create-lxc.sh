#!/usr/bin/env bash
# Legt auf einem Proxmox-Host einen LXC für Onkel Bummel an und startet ihn: Debian, Docker, Tailscale im LXC,
# der Server als Docker-Container hinter "tailscale serve" (https://<CT_HOSTNAME>.<tailnet>.ts.net) und ein
# systemd-Timer, der neue Commits auf main selbst einspielt (deploy/proxmox/update.sh).
#
# In der Proxmox-Shell als root:
#   TS_AUTHKEY=tskey-auth-... bash create-lxc.sh
# Privates Repository: zusätzlich GIT_TOKEN=github_pat_... (Fine-grained Token, nur "Contents: Read").
#
# Alle Einstellungen unten lassen sich über Umgebungsvariablen setzen. Nochmal mit derselben CTID aufrufen
# setzt eine abgebrochene Einrichtung fort; Einstellungen und Daten im LXC bleiben.
set -euo pipefail

CTID="${CTID:-}"                                  # leer: nächste freie ID
CT_HOSTNAME="${CT_HOSTNAME:-onkel-bummel}"        # auch der Name im Tailnet
STORAGE="${STORAGE:-local-lvm}"                   # Speicher für die Disk
TEMPLATE_STORAGE="${TEMPLATE_STORAGE:-local}"     # Speicher für die Debian-Vorlage
BRIDGE="${BRIDGE:-vmbr0}"
IP="${IP:-dhcp}"                                  # oder z. B. 192.168.1.50/24
GATEWAY="${GATEWAY:-}"                            # nur bei fester IP nötig
CORES="${CORES:-2}"
MEMORY="${MEMORY:-2048}"                          # MiB; der Build braucht etwa 1,5 GiB
DISK="${DISK:-12}"                                # GiB; Images und Build-Cache brauchen etwa 4 GiB
SSH_PUBLIC_KEY_FILE="${SSH_PUBLIC_KEY_FILE:-}"    # optional, für SSH als root

TS_AUTHKEY="${TS_AUTHKEY:-}"                      # tskey-auth-...; leer: Anmeldung über den angezeigten Link

REPO_URL="${REPO_URL:-https://github.com/Marcel-B/onkel-bummel.git}"
REPO_BRANCH="${REPO_BRANCH:-main}"
GIT_TOKEN="${GIT_TOKEN:-}"                        # nur bei privatem Repository
AUTO_UPDATE="${AUTO_UPDATE:-1}"                   # 1: neue Commits auf REPO_BRANCH automatisch einspielen
UPDATE_INTERVAL="${UPDATE_INTERVAL:-5min}"        # wie oft nachsehen (systemd-Zeitspanne)

OIDC_ISSUER="${OIDC_ISSUER:-}"                    # leer: https://identity.<tailnet>.ts.net
OIDC_WEB_CLIENT_SECRET="${OIDC_WEB_CLIENT_SECRET:-}" # Secret des Clients onkel-bummel-web, geht auch später
APNS_KEY_FILE="${APNS_KEY_FILE:-}"                # Pfad zur AuthKey_XXXXXXXXXX.p8 auf dem Proxmox-Host, geht auch später
APNS_KEY_ID="${APNS_KEY_ID:-}"
APNS_TEAM_ID="${APNS_TEAM_ID:-}"
LOKI_URL="${LOKI_URL:-}"                          # z. B. http://monitoring:3100; leer: Logs nur im Container

info() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[1;31mFehler: %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || fail "Bitte als root auf dem Proxmox-Host ausführen."
command -v pct >/dev/null || fail "pct fehlt. Das Skript muss auf einem Proxmox-VE-Host laufen."

case $TS_AUTHKEY in
    "" | tskey-auth-*) ;;
    tskey-api-*) fail "TS_AUTHKEY ist ein API-Token. Gebraucht wird ein Auth Key: Tailscale-Admin > Settings > Keys > Generate auth key (tskey-auth-...)." ;;
    *) fail "TS_AUTHKEY muss mit tskey-auth- beginnen." ;;
esac
[[ -z $APNS_KEY_FILE || -f $APNS_KEY_FILE ]] || fail "APNS_KEY_FILE=$APNS_KEY_FILE gibt es nicht."
[[ $AUTO_UPDATE == 0 ]] || systemd-analyze timespan "$UPDATE_INTERVAL" &>/dev/null ||
    fail "UPDATE_INTERVAL=$UPDATE_INTERVAL ist keine Zeitspanne wie 5min."

CTID="${CTID:-$(pvesh get /cluster/nextid)}"
if pct status "$CTID" &>/dev/null; then
    pct config "$CTID" | grep -qx "hostname: $CT_HOSTNAME" ||
        fail "Container $CTID gibt es schon, und er heißt nicht $CT_HOSTNAME. Andere CTID wählen."
    info "Einrichtung von Container $CTID ($CT_HOSTNAME) fortsetzen"
else
    info "Debian-Vorlage suchen"
    pveam update >/dev/null
    # Proxmox listet Vorlagen für amd64 und arm64; ohne Filter gewinnt beim Sortieren arm64
    ARCH="$(dpkg --print-architecture)"
    TEMPLATE=""
    for release in 13 12; do
        TEMPLATE="$(pveam available --section system |
            awk -v r="^debian-$release-standard_" -v a="_$ARCH.tar" '$2 ~ r && index($2, a) {print $2}' | sort -V | tail -n1)"
        [[ -n $TEMPLATE ]] && break
    done
    [[ -n $TEMPLATE ]] || fail "Keine debian-13- oder debian-12-standard-Vorlage für $ARCH gefunden."
    if ! pveam list "$TEMPLATE_STORAGE" | grep -q "$TEMPLATE"; then
        info "Vorlage $TEMPLATE herunterladen"
        pveam download "$TEMPLATE_STORAGE" "$TEMPLATE"
    fi

    info "Container $CTID ($CT_HOSTNAME) anlegen"
    NET="name=eth0,bridge=$BRIDGE,ip=$IP"
    [[ -n $GATEWAY ]] && NET+=",gw=$GATEWAY"
    # shellcheck disable=SC2054 # Kommas gehören zu den Werten von --features
    CREATE_ARGS=(
        --hostname "$CT_HOSTNAME"
        --cores "$CORES" --memory "$MEMORY" --swap 512
        --rootfs "$STORAGE:$DISK"
        --net0 "$NET"
        --ostype debian --unprivileged 1
        # nesting und keyctl braucht Docker in einem unprivilegierten Container
        --features nesting=1,keyctl=1
        --onboot 1 --timezone host
    )
    [[ -n $SSH_PUBLIC_KEY_FILE ]] && CREATE_ARGS+=(--ssh-public-keys "$SSH_PUBLIC_KEY_FILE")
    pct create "$CTID" "$TEMPLATE_STORAGE:vztmpl/$TEMPLATE" "${CREATE_ARGS[@]}"

    # Tailscale braucht das TUN-Gerät, das unprivilegierte Container sonst nicht bekommen
    cat >>"/etc/pve/lxc/$CTID.conf" <<'EOF'
lxc.cgroup2.devices.allow: c 10:200 rwm
lxc.mount.entry: /dev/net/tun dev/net/tun none bind,create=file
EOF
fi

pct status "$CTID" | grep -q running || pct start "$CTID"

info "Auf Netzwerk warten"
for _ in $(seq 1 60); do
    pct exec "$CTID" -- getent hosts github.com &>/dev/null && break
    sleep 2
done
pct exec "$CTID" -- getent hosts github.com &>/dev/null || fail "Container $CTID hat kein Netzwerk."

info "Repository holen"
CLONE_URL="$REPO_URL"
[[ -n $GIT_TOKEN ]] && CLONE_URL="${REPO_URL/https:\/\//https://x-access-token:$GIT_TOKEN@}"
# shellcheck disable=SC2016 # die Variablen setzt env im LXC
pct exec "$CTID" -- env CLONE_URL="$CLONE_URL" REPO_URL="$REPO_URL" REPO_BRANCH="$REPO_BRANCH" bash -euo pipefail -c '
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq ca-certificates curl git >/dev/null
    if [[ ! -d /opt/onkel-bummel/.git ]]; then
        git clone --quiet --depth 1 --branch "$REPO_BRANCH" "$CLONE_URL" /opt/onkel-bummel
        # Den Token nicht in der Git-Konfiguration liegen lassen; update.sh holt ihn aus /etc/onkel-bummel
        git -C /opt/onkel-bummel remote set-url origin "$REPO_URL"
    fi
' || fail "Repository $REPO_URL ließ sich nicht klonen. Privat? Dann GIT_TOKEN setzen."

if [[ -n $APNS_KEY_FILE ]]; then
    info "APNs-Schlüssel übertragen"
    pct exec "$CTID" -- install -d -m 750 -g 1654 /etc/onkel-bummel/secrets
    pct push "$CTID" "$APNS_KEY_FILE" /etc/onkel-bummel/secrets/apns.p8 --perms 640 --group 1654
fi

# Alles Weitere läuft im LXC, damit es sich dort auch ohne Proxmox-Shell wiederholen lässt
pct exec "$CTID" -- env \
    CT_HOSTNAME="$CT_HOSTNAME" TS_AUTHKEY="$TS_AUTHKEY" \
    REPO_URL="$REPO_URL" REPO_BRANCH="$REPO_BRANCH" GIT_TOKEN="$GIT_TOKEN" \
    AUTO_UPDATE="$AUTO_UPDATE" UPDATE_INTERVAL="$UPDATE_INTERVAL" \
    OIDC_ISSUER="$OIDC_ISSUER" OIDC_WEB_CLIENT_SECRET="$OIDC_WEB_CLIENT_SECRET" \
    APNS_KEY_ID="$APNS_KEY_ID" APNS_TEAM_ID="$APNS_TEAM_ID" LOKI_URL="$LOKI_URL" \
    bash /opt/onkel-bummel/deploy/proxmox/setup.sh ||
    fail "Einrichtung im Container $CTID ging nicht. Ursache oben; danach dieses Skript mit CTID=$CTID erneut aufrufen."

echo
echo "  Container:  $CTID ($CT_HOSTNAME)"
echo "  Shell:      pct enter $CTID"
