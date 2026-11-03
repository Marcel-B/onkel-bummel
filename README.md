# Onkel Bummel

Eigener Benachrichtigungsdienst mit Verlauf, Suche und Bildern, benannt nach meinem ersten Postboten. Ersetzt ntfy für Slipways, fil-o-mat und die Grafana-Alarme. Was er können soll, steht in [docs/SPEC.md](docs/SPEC.md).

| Teil | Stand |
|---|---|
| Server (C# / .NET 10) | Senden (JSON, multipart, Grafana-Webhook) mit Bildern und Thumbnails, Publish-Tokens, Verlauf mit Suche, Live-Stream (SSE), Actions, Anmeldung per JWT, Aufbewahrung, Aufräumen, Sicherung in die Nextcloud, Metriken und Loki |
| Push (APNs), Geräte | folgt |
| Web-Oberfläche | folgt |
| iOS-App | folgt |

## Lokal starten

Voraussetzung: .NET 10 SDK.

```bash
cd backend
dotnet run --project src/OnkelBummel.Api --launch-profile http
```

Die App läuft auf http://localhost:8080, die Metriken liegen unter http://localhost:9464/metrics. Datenbank und Anhänge landen in `backend/src/OnkelBummel.Api/local-data/`.

## Senden

Zuerst einen Publish-Token anlegen. Solange es die Web-Oberfläche nicht gibt, geht das auf der Kommandozeile, im LXC:

```bash
cd /opt/onkel-bummel/deploy
docker compose exec onkel-bummel dotnet OnkelBummel.Api.dll --create-token grafana alarme
docker compose exec onkel-bummel dotnet OnkelBummel.Api.dll --create-token fil-o-mat filomat,kamera
docker compose exec onkel-bummel dotnet OnkelBummel.Api.dll --list-tokens
docker compose exec onkel-bummel dotnet OnkelBummel.Api.dll --delete-token <id>
```

Statt einzelner Topics geht `'*'` für alle. Der Token (`ob_…`) wird nur dieses eine Mal angezeigt. Lokal: `dotnet run --project src/OnkelBummel.Api -- --create-token test '*'`.

```bash
# Text
curl -H "Authorization: Bearer ob_…" -H "Content-Type: application/json" \
  -d '{"topic":"garage","title":"Tor","message":"Das Tor ist offen","priority":4,"tags":["warning"]}' \
  https://onkel-bummel.<tailnet>.ts.net/api/v1/messages

# mit Bild und Action-Button
curl -H "Authorization: Bearer ob_…" -F topic=kamera -F message="Bewegung an der Einfahrt" -F file=@snap.jpg \
  -F 'actions=[{"label":"Licht an","url":"http://haus/licht","method":"POST","clear":true}]' \
  https://onkel-bummel.<tailnet>.ts.net/api/v1/messages
```

Grafana: Kontaktpunkt vom Typ Webhook mit der URL `https://onkel-bummel.<tailnet>.ts.net/api/v1/webhooks/grafana?topic=alarme`, unter „Authorization Header“ Schema `Bearer` und als Credentials den Token. Alle Felder und Fehler stehen in der [Spezifikation](docs/SPEC.md#senden).

## Tests

```bash
cd backend
dotnet test
```

## Betrieb

Onkel Bummel läuft in einem LXC auf Proxmox, erreichbar nur im Tailnet unter `https://onkel-bummel.<tailnet>.ts.net`, die Metriken unter Port 9464. Neue Commits auf `main` spielt der LXC selbst ein, nach bestandenen Tests und mit Rücknahme, wenn die neue Version nicht hochkommt.

```bash
# in der Proxmox-Shell als root
TS_AUTHKEY=tskey-auth-... bash <(curl -fsSL https://raw.githubusercontent.com/Marcel-B/onkel-bummel/main/deploy/proxmox/create-lxc.sh)
```

Einstellungen, Updates und Pfade: [deploy/proxmox/README.md](deploy/proxmox/README.md).

## Monitoring

`/metrics` antwortet nur auf Port 9464, und die übrigen Ports antworten nicht auf `/metrics`. Die Messwerte stehen in der [Spezifikation](docs/SPEC.md#metriken-und-logs). Logs gehen mit `Monitoring__Loki__Url` zusätzlich an Loki (`app=onkelbummel`).

## Datenbank-Migrationen

```bash
cd backend
dotnet tool restore
Database__InitializeOnStartup=false dotnet ef migrations add <Name> -p src/OnkelBummel.Api -o Data/Migrations
```

Die Volltextsuche (FTS5-Tabelle und Trigger) legt die erste Migration per SQL aus `Data/MessageSearch.cs` an. Eine Migration, die `Title`, `Body` oder `Tags` von `Messages` ändert, muss sie mitziehen.
