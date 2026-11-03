#!/usr/bin/env bash
# Spielt den neuesten Stand des Branches ein. Liegt im LXC als /usr/local/bin/onkel-bummel-update (setup.sh),
# der systemd-Timer onkel-bummel-update.timer ruft es mit --if-changed auf.
#   onkel-bummel-update               jetzt einspielen, auch einen Commit, der schon einmal scheiterte
#   onkel-bummel-update --if-changed  nur, wenn es einen neuen Commit gibt, der noch nicht scheiterte
#
# Ablauf: Commit holen → Tests im Docker-Build (Stage "test") → Image bauen → Datenbank sichern → neu starten.
# Scheitern Tests oder Build, läuft die bisherige Version einfach weiter. Kommt die neue Version nicht gesund
# hoch, kommen das vorige Image und die Datenbank von vor dem Update zurück. Nachrichten, die in diesen
# ein, zwei Minuten eingingen, gehen dabei verloren.
set -euo pipefail

# Alles steckt in main, damit bash das Skript ganz gelesen hat, bevor es sich am Ende selbst ersetzt
main() {
    CONF_DIR="${ONKEL_BUMMEL_CONF:-/etc/onkel-bummel}"
    STATE_DIR="${ONKEL_BUMMEL_STATE:-/var/lib/onkel-bummel-update}"
    export ONKEL_BUMMEL_DATA="${ONKEL_BUMMEL_DATA:-/var/lib/onkel-bummel}"
    HEALTH_URL="${ONKEL_BUMMEL_HEALTH_URL:-http://127.0.0.1:8080/health}"
    DB="$ONKEL_BUMMEL_DATA/onkel-bummel.db"
    APP_UID=1654

    # REPO_URL, REPO_BRANCH, GIT_TOKEN, SRC_DIR
    # shellcheck source=/dev/null
    source "$CONF_DIR/source.env"
    COMPOSE_FILE="$SRC_DIR/deploy/docker-compose.yml"
    IMAGE=onkel-bummel:latest
    PREVIOUS=onkel-bummel:previous

    mkdir -p "$STATE_DIR"
    exec 9>"$STATE_DIR/lock"
    flock -n 9 || { echo "Es läuft schon ein Update."; exit 0; }

    url="$REPO_URL"
    [[ -n ${GIT_TOKEN:-} ]] && url="${REPO_URL/https:\/\//https://x-access-token:$GIT_TOKEN@}"

    if [[ ${1:-} == --if-changed ]]; then
        latest="$(git ls-remote "$url" "refs/heads/$REPO_BRANCH" | cut -f1)"
        [[ -n $latest ]] || { echo "Branch $REPO_BRANCH nicht gefunden." >&2; exit 1; }
        [[ $latest == "$(state deployed)" ]] && exit 0
        # Ein gescheiterter Commit wird nicht bei jedem Lauf neu gebaut; ein neuerer oder ein Aufruf von Hand schon
        [[ $latest == "$(state failed)" ]] && exit 0
    fi

    if [[ -d $SRC_DIR/.git ]]; then
        old="$(git -C "$SRC_DIR" rev-parse HEAD)"
        git -C "$SRC_DIR" fetch --quiet --depth 1 "$url" "$REPO_BRANCH"
        git -C "$SRC_DIR" reset --quiet --hard FETCH_HEAD
    else
        old=""
        git clone --quiet --depth 1 --branch "$REPO_BRANCH" "$url" "$SRC_DIR"
        git -C "$SRC_DIR" remote set-url origin "$REPO_URL"
    fi
    commit="$(git -C "$SRC_DIR" rev-parse HEAD)"
    short="${commit:0:7}"
    echo "Spiele $short ein: $(git -C "$SRC_DIR" log -1 --format=%s)"

    echo "Tests"
    if ! docker build --quiet --target test "$SRC_DIR/backend" >/dev/null; then
        # Ohne --quiet nochmal, damit die fehlgeschlagenen Tests im Log stehen; die Stages kommen aus dem Cache
        docker build --progress=plain --target test "$SRC_DIR/backend" 2>&1 | tail -n 60 || true
        give_up "Tests von $short schlagen fehl, die bisherige Version läuft weiter."
    fi

    echo "Image bauen"
    has_previous=0
    if docker image inspect "$IMAGE" &>/dev/null; then
        docker tag "$IMAGE" "$PREVIOUS"
        has_previous=1
    fi
    if ! compose build --quiet; then
        [[ $has_previous == 1 ]] && docker tag "$PREVIOUS" "$IMAGE"
        give_up "Image von $short ließ sich nicht bauen, die bisherige Version läuft weiter."
    fi

    backup=""
    if [[ -f $DB ]]; then
        # Online-Backup von SQLite: stimmig, auch während der Server schreibt
        backup="$STATE_DIR/pre-update.db"
        rm -f "$backup"
        sqlite3 "$DB" ".backup '$backup'"
    fi

    echo "Neu starten"
    if compose up -d --no-build --remove-orphans && healthy; then
        state_set deployed "$commit"
        rm -f "$STATE_DIR/failed" "$backup"
        docker image rm "$PREVIOUS" &>/dev/null || true
        docker image prune -f >/dev/null || true
        docker builder prune -f --filter until=168h >/dev/null || true
        # Neuer Stand dieses Skripts; mv statt cp, damit die laufende Kopie unberührt bleibt
        install -m 755 "$SRC_DIR/deploy/proxmox/update.sh" /usr/local/bin/onkel-bummel-update.new 2>/dev/null &&
            mv /usr/local/bin/onkel-bummel-update.new /usr/local/bin/onkel-bummel-update || true
        echo "$short läuft."
        exit 0
    fi

    compose logs --tail 60 || true
    if [[ $has_previous == 0 ]]; then
        give_up "$short kommt nicht gesund hoch, und es gibt keine vorige Version."
    fi
    echo "$short kommt nicht gesund hoch, die vorige Version kommt zurück." >&2
    compose stop
    if [[ -n $backup ]]; then
        # Die WAL-Dateien der neuen Version dürfen nicht auf die alte Datenbank angewendet werden
        rm -f "$DB-wal" "$DB-shm"
        cp "$backup" "$DB"
        chown "$APP_UID:$APP_UID" "$DB"
    fi
    docker tag "$PREVIOUS" "$IMAGE"
    compose up -d --no-build --force-recreate
    healthy || echo "Auch die vorige Version antwortet nicht, bitte nachsehen: docker compose -f $COMPOSE_FILE logs" >&2
    give_up "Update auf $short zurückgenommen."
}

compose() { docker compose -f "$COMPOSE_FILE" "$@"; }

healthy() {
    for _ in $(seq 1 45); do
        curl -fsS -o /dev/null "$HEALTH_URL" 2>/dev/null && return 0
        sleep 2
    done
    return 1
}

state() { cat "$STATE_DIR/$1" 2>/dev/null || true; }
state_set() { echo "$2" >"$STATE_DIR/$1"; }

# Merkt sich den Commit als gescheitert und setzt den Checkout auf die laufende Version zurück
give_up() {
    state_set failed "$commit"
    [[ -n ${old:-} ]] && git -C "$SRC_DIR" reset --quiet --hard "$old"
    echo "$1" >&2
    exit 1
}

main "$@"
