# Onkel Bummel

Eigener Benachrichtigungsdienst mit Verlauf, Suche und Bildern, benannt nach meinem ersten Postboten. Ersetzt ntfy für Slipways, fil-o-mat und die Grafana-Alarme. Was er können soll, steht in [docs/SPEC.md](docs/SPEC.md).

| Teil | Stand |
|---|---|
| Server (C# / .NET 10) | Gerüst: Datenbank mit Volltextsuche, Metriken, Loki, Health, `GET /api/v1/config` |
| Senden, Verlauf, Anmeldung, Push | folgt |
| Web-Oberfläche | folgt |
| iOS-App | folgt |

## Lokal starten

Voraussetzung: .NET 10 SDK.

```bash
cd backend
dotnet run --project src/OnkelBummel.Api --launch-profile http
```

Die App läuft auf http://localhost:8080, die Metriken liegen unter http://localhost:9464/metrics. Datenbank und Anhänge landen in `backend/src/OnkelBummel.Api/local-data/`.

## Tests

```bash
cd backend
dotnet test
```

## Betrieb

Onkel Bummel läuft in einem LXC auf Proxmox, erreichbar nur im Tailnet unter `https://onkel-bummel.<tailnet>.ts.net`, die Metriken unter Port 9464. Neue Commits auf `main` spielt der LXC selbst ein, nach bestandenen Tests und mit Rücknahme, wenn die neue Version nicht hochkommt.

```bash
# in der Proxmox-Shell als root
TS_AUTHKEY=tskey-auth-... bash create-lxc.sh
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
