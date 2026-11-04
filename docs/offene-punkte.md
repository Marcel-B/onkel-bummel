# Offene Punkte

Was noch offen ist, eine Entscheidung braucht oder erst im Betrieb bestätigt werden kann. Erledigte Punkte hier löschen. Stand: 04.11.2026.

## Im Betrieb bestätigen

1. **fil-o-mat mit Bild und Knöpfen.** fil-o-mat sendet seit 04.11.2026 an das Topic `filomat`, angekommen ist bisher nur die Testmeldung ohne Bild und ohne Actions. Bei der nächsten Meldung zu einem fertigen oder abgebrochenen Druck prüfen, ob das Kamera- oder Vorschaubild dabei ist, und bei „Welche Spule?“ die Knöpfe (`POST /choices/{token}` an fil-o-mat).
2. **GitGuardian-Warnung.** Marcel hat am 04.11.2026 eine Warnung gesehen. Alle PR-Prüfungen in onkel-bummel, Slipways und fil-o-mat melden „No secrets detected“, und eine Suche in den Commits seit dem 01.11.2026 fand nur Metriknamen und die Prüfung auf „BEGIN PRIVATE KEY“ in `ios/scripts/testflight-secrets.sh`. Woher die Warnung kam (Repo, Datei), ist offen. Möglich ist auch die alte, als falscher Alarm markierte Meldung zum Basic-Header aus PR #1.

## Sicherung

1. **Nextcloud-Sicherung von Onkel Bummel ist aus.** In CT 106 sind `Backup__NextcloudWebDavUrl` und `Backup__NextcloudUserName` noch auskommentiert. Damit fehlt die Metrik `onkelbummel_backup_last_success_timestamp_seconds`, und die Regel „Sicherung zu alt“ greift nicht. Gesichert ist CT 106 trotzdem über Proxmox (täglich 03:00, `keep-last=2` auf `backup-hdd`, ein Job lädt die Sicherungen in die Nextcloud bei Hetzner). Entscheiden, ob die eigene Sicherung zusätzlich laufen soll. Einrichten wie in [deploy/proxmox/README.md](../deploy/proxmox/README.md#sicherung-in-die-nextcloud) beschrieben.

## Ausfallsicherheit (Entscheidung steht aus)

Identity (CT 112) und Onkel Bummel (CT 106) laufen zu Hause. Fallen Strom oder Internet aus, sind beide weg, das Monitoring (CT 110) und fil-o-mat (CT 111) aber auch. Das größte Loch: Den Ausfall selbst meldet niemand. Am 04.11.2026 besprochen und vorerst zurückgestellt:

- **Hausalarm (kleiner Schritt):** Das Monitoring meldet sich regelmäßig bei einem externen Wächter. Bleibt das aus, kommt eine Nachricht über einen Weg, der nicht über das Zuhause läuft, etwa E-Mail oder Healthchecks.io.
- **Umzug auf slipways-1:**
  - Hardware: 2 vCPU, 3,7 GiB RAM ohne Swap, 19 GB frei. Das reicht für den Betrieb, nicht für Builds.
  - Beide Dienste müssten zusammen umziehen, weil Onkel Bummel Identity für Login und Refresh braucht.
  - Die Tailscale-Namen `identity.tail6105c0.ts.net` und `onkel-bummel.tail6105c0.ts.net` müssen bleiben, denn der Issuer steckt in Tokens, Clients und App. Dafür braucht jeder Dienst einen eigenen Tailscale-Sidecar, weil slipways-1 schon Knoten `slipways` ist.
  - Identity hat noch keinen Docker-Weg für den Betrieb: Schlüssel unter `/etc/identity/keys`, `identity.env`, Sicherung und Restore sind für systemd geschrieben.
  - Die Proxmox-Sicherung entfällt dann. Ersatz wären die Nextcloud-Sicherungen der Apps, auf Wunsch zusätzlich Snapshots bei Hetzner.

## Builds der anderen Dienste auslagern

Onkel Bummel zieht seit PR #8 fertige Images, bestätigt am 04.11.2026 mit dem Update auf `cf957b3`. Dasselbe ginge bei den anderen, je ein eigener PR im jeweiligen Repo:

- **Identity (CT 112):** baut mit `dotnet publish` direkt im LXC (`identity-update`, ohne Docker, etwa 1,5 GiB RAM beim Build). Dort eher ein fertiges `publish`-Archiv aus GitHub Actions oder gleich der Docker-Weg (siehe Ausfallsicherheit).
- **fil-o-mat (CT 111)** und **Slipways (slipways-1)**: haben eigene Updater, die auf dem Host bauen. Muster: Workflow wie `server-image.yml` und Pull-Modus wie in `deploy/proxmox/update.sh`.

## Funktionen

1. **Kurzbefehle und Widget (Meilenstein 8 des ursprünglichen Plans, noch nicht in der [Spezifikation](SPEC.md)).** App Intents für Kurzbefehle, z. B. „Nachricht senden“ und „Ungelesene zeigen“, und ein Widget „Ungelesen“.
2. **Suche: „ss“ findet kein „ß“.** Der FTS5-Tokenizer `unicode61 remove_diacritics 2` (`backend/src/OnkelBummel.Api/Data/MessageSearch.cs`) faltet „ü“ zu „u“, aber nicht „ß“ zu „ss“. Lösung etwa: Text vor dem Indizieren und die Suchanfrage gleich normalisieren. Das braucht eine Migration, die Index und Trigger neu anlegt.
3. **Grafana-Alarme schöner anzeigen.** Icon und Farbe für das Topic `alarme`, Emoji-Tags je nach Zustand (`firing` / `resolved`) und eine lesbarere Zusammenfassung mehrerer Alarme in einer Meldung (`/api/v1/webhooks/grafana`).
