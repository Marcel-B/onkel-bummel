# Onkel Bummel

Eigener Benachrichtigungsdienst mit Verlauf, Suche und Bildern, benannt nach meinem ersten Postboten. Ersetzt ntfy für Slipways, fil-o-mat und die Grafana-Alarme. Was er können soll, steht in [docs/SPEC.md](docs/SPEC.md).

| Teil | Stand |
|---|---|
| Server (C# / .NET 10) | Senden (JSON, multipart, Grafana-Webhook) mit Bildern und Thumbnails, Publish-Tokens, Verlauf mit Suche, Live-Stream (SSE), Actions, Anmeldung per JWT, Aufbewahrung, Aufräumen, Sicherung in die Nextcloud, Metriken und Loki |
| Push (APNs), Geräte | Push an abonnierte Geräte mit Retry, Bild über signierten Link, Actions als Buttons, Badge |
| Web-Oberfläche | folgt |
| iOS-App | Einrichtung mit Login, Inbox, Topics mit Galerie, Suche, Abos, Cache und Live-Updates, Notification Service Extension |

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

## iOS-App

Voraussetzung: Xcode und [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`). Das Xcode-Projekt wird aus `ios/project.yml` erzeugt und nicht eingecheckt.

```bash
cd ios
xcodegen
open OnkelBummel.xcodeproj
```

Signiert wird automatisch mit dem Team `6GGU8ET2C4`. Die App braucht im Identity-Server den Public Client `onkel-bummel-ios` mit der Redirect-URI `com.b-velop.onkel-bummel:/oauth/callback` (siehe [Spezifikation](docs/SPEC.md#anmeldung)). Im Simulator lässt sich die Server-Adresse beim Start vorgeben: `xcrun simctl launch booted com.b-velop.onkel-bummel -setupServer onkel-bummel.<tailnet>.ts.net`. Einen Push mit Bild probiert man dort mit `xcrun simctl push booted com.b-velop.onkel-bummel push.json` aus.

Push im LXC einschalten: den APNs-Schlüssel nach `/etc/onkel-bummel/secrets/apns.p8` legen (`chown root:1654`, `chmod 640`), in `/etc/onkel-bummel/onkel-bummel.env` `Apns__KeyPath=/run/secrets/apns.p8`, `Apns__KeyId` und `Apns__TeamId=6GGU8ET2C4` eintragen, dann `docker compose up -d`.

## Tests

```bash
cd backend
dotnet test
cd ../ios/OnkelBummelKit
swift test
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
