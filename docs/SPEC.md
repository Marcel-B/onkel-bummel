# Onkel Bummel – Spezifikation

Onkel Bummel ist ein eigener Benachrichtigungsdienst für einen Nutzer, benannt nach Marcels erstem Postboten. Er ersetzt ntfy und bietet vor allem einen **durchsuchbaren Verlauf mit Übersicht** (Topics, Gelesen-Status, Galerie) und **gute Unterstützung für Bilder**.

- **Server:** C# / .NET 10 (ASP.NET Core) in Docker im Heimnetz, im Tailnet unter `https://onkel-bummel.<tailnet>.ts.net`
- **Clients:** native iOS-App (SwiftUI, APNs) und eine Web-Oberfläche, die vom Server ausgeliefert wird
- **Anmeldung:** über den eigenen Identity-Server ([Marcel-B/Identity](https://github.com/Marcel-B/Identity)); wer Onkel Bummel ohne Identity-Server selbst betreibt, meldet sich mit einem Zugangsschlüssel an (siehe [Anmeldung](#anmeldung))
- **Monitoring:** Prometheus, Loki und Grafana im gemeinsamen Monitoring-LXC ([Marcel-B/Tonwerk-AI → `deploy/monitoring/`](https://github.com/Marcel-B/Tonwerk-AI/tree/main/deploy/monitoring))

Nicht im Umfang: mehrere Nutzer, Android, MQTT, ntfy-Kompatibilität, E-Mail/SMS, Regeln/Eskalation, Critical Alerts.

## Namen

| Was | Name |
|---|---|
| Anzeigename | Onkel Bummel |
| Repo, Hostname, Docker-Service | `onkel-bummel` |
| Solution / Projekte | `OnkelBummel`, `OnkelBummel.Api`, `OnkelBummel.Api.Tests` |
| Metrik-Präfix, Loki-Label `app` | `onkelbummel` |
| iOS-Bundle-ID, URL-Schema | `com.b-velop.onkel-bummel` (Notification Service Extension `com.b-velop.onkel-bummel.NotificationService`, App Group `group.com.b-velop.onkel-bummel`, Team `6GGU8ET2C4`) |
| Publish-Tokens | Präfix `ob_` |

## Konzepte

- **Nachricht:** gehört zu einem Topic und hat einen Text (Markdown light), optional einen Titel, eine Priorität 1–5 (Standard 3), Tags, eine Klick-URL, 0–n Anhänge (vor allem Bilder), 0–3 Actions, einen Zeitstempel und einen Gelesen-Status. Die ID ist eine fortlaufende Zahl und dient auch als Cursor beim Blättern.
- **Topic:** einfacher Name wie `garage`, `slipways`, `filomat`, `alarme` (`[a-z0-9_-]{1,64}`). Ein Topic entsteht mit der ersten Nachricht. Einstellbar sind Anzeigename, Icon, Farbe, Stummschaltung (kein Push) und Aufbewahrung in Tagen. Nicht erlaubt sind die Namen `api`, `auth`, `static`, `health`, `metrics` und `*`.
- **Action:** Button an einer Nachricht mit Label und HTTP-Aufruf (Methode, URL, optional Header und Body, `clear`). Den Aufruf führt der Server aus: Er steht im Heimnetz und erreicht das Ziel immer, das Handy nicht unbedingt. Das letzte Ergebnis (Status, Zeit) wird an der Nachricht gespeichert. `clear` markiert die Nachricht danach als gelesen.
- **Gerät:** eine iOS-Installation mit APNs-Token, Umgebung (`sandbox`/`production`) und abonnierten Topics (`*` = alle).
- **Publish-Token:** statischer Schlüssel für Sender (Skripte, Dienste, Grafana), auf Topics beschränkt oder `*`. Er trägt 256 Zufallsbits, wird einmal angezeigt und nur als SHA-256-Hash gespeichert. Anlegen in der Web-Oberfläche (`POST publish-tokens`) oder auf der Kommandozeile: `dotnet OnkelBummel.Api.dll --create-token <name> <topics>` (außerdem `--list-tokens` und `--delete-token <id>`).

## Senden

Auth: `Authorization: Bearer ob_…`. Ein angemeldeter Benutzer (Access Token mit Rolle) darf ebenfalls senden, an jedes Topic. Fehler kommen als Problem-JSON (RFC 7807): `400` ungültig, `401` ohne gültiges Token, `403` Topic nicht erlaubt, `413` zu groß, `415` falscher Inhaltstyp.

Grenzen: Titel 250 Zeichen, Text 32 000 Zeichen (leer nur mit Anhang), höchstens 10 Tags mit je 64 Zeichen, 3 Actions (Label 40 Zeichen, Methode `GET`, `POST`, `PUT`, `PATCH` oder `DELETE`, Standard `POST`), `Limits:MaxFilesPerMessage` Anhänge (Standard 10) und `Limits:MaxRequestBytes` für die ganze Anfrage (Standard 100 MB). Die Klick-URL darf kein `javascript:`, `data:`, `vbscript:` oder `file:` sein.

Anhänge: Der Typ wird an den ersten Bytes erkannt, nicht an Name oder Content-Type. Angenommen werden JPEG, PNG, GIF, WebP und HEIC, außerdem PDF und Text (UTF-8, als `text/…` oder mit Endung `.txt`, `.log`, `.csv`, `.json`, `.md`). Alles andere gibt `415`. Ein Bild, das sich nicht lesen lässt, gibt `400`. HEIC kann der Server nicht dekodieren und speichert es ohne Thumbnail. Thumbnails sind immer JPEG, weil Mitteilungsanhänge unter iOS kein WebP nehmen.

### `POST /api/v1/messages` mit JSON

```json
{
  "topic": "filomat",
  "title": "Druck fertig",
  "message": "Benchy ist fertig (1 h 12 min).",
  "priority": 3,
  "tags": ["white_check_mark", "drucker"],
  "click": "https://fil-o-mat.<tailnet>.ts.net/jobs/42",
  "actions": [
    { "label": "Nächster Druck", "url": "http://fil-o-mat:8080/api/jobs/next", "method": "POST", "clear": true }
  ],
  "images": [
    { "url": "http://fil-o-mat:8080/api/printers/1/thumbnail" },
    { "data": "<base64>", "filename": "kamera.jpg" }
  ]
}
```

### `POST /api/v1/messages` mit `multipart/form-data`

Felder `topic`, `title`, `message`, `priority`, `tags` (kommagetrennt), `click`, `actions` (JSON-String wie oben) und beliebig viele Dateien im Feld `file` (Dateien in anderen Feldern zählen auch):

```bash
curl -H "Authorization: Bearer ob_…" -F topic=kamera -F message="Bewegung an der Einfahrt" -F file=@snap.jpg \
  https://onkel-bummel.<tailnet>.ts.net/api/v1/messages
```

### `POST /api/v1/webhooks/grafana?topic=alarme`

Nimmt den Payload eines Grafana-Webhook-Kontaktpunkts direkt an. `firing` wird zu Priorität 4 mit Titel „Alarm: <alertname>“, `resolved` zu Priorität 2 mit „Wieder gut: <alertname>“. Text = die `summary`-Annotationen (sonst `description`), Klick-URL = `externalURL`, Tag `grafana`, Quelle `grafana`.

Eine Gruppe mit mehreren Alarmen wird eine Nachricht: Titel „Alarm: A (+2)“, im Text eine Zeile pro Alarm. Sind in einer Gruppe, die noch feuert, einzelne Alarme schon vorbei, stehen sie als „Wieder gut: …“ darunter. Das Token braucht das Recht für das Topic aus `?topic=`.

### Antwort und Wiederholungen

- `201 Created` mit der gespeicherten Nachricht (`id`, `topic`, `title`, `message`, `priority`, `tags`, `click`, `source`, `createdAt`, `readAt`, `attachments`, `actions`) und `Location: /api/v1/messages/<id>`. Zeiten sind UTC (ISO 8601 mit `Z`). Header und Body der Actions gibt die API nicht wieder heraus, sie können Schlüssel des Ziels enthalten.
- Mit dem Header `Idempotency-Key` (höchstens 200 Zeichen) gibt eine Wiederholung mit demselben Schlüssel innerhalb von 24 Stunden die ursprüngliche Antwort zurück, mit `Idempotent-Replayed: true`, und legt keine zweite Nachricht an. Danach ist der Schlüssel wieder frei.
- Tags mit bekannten Emoji-Kurznamen (`tada`, `white_check_mark`, `warning`, `rotating_light`, …) zeigen die Clients als Emoji vor dem Titel, die übrigen als Tags.

### Ablauf

1. Token und Topic-Recht prüfen, Idempotency-Key prüfen
2. Anhänge speichern (Upload, Base64 oder von einer URL geladen): Inhaltstyp prüfen, höchstens `Limits:MaxFileBytes` (Standard 20 MB) pro Datei, Bilder bekommen ein Thumbnail (längste Seite 600 px, EXIF-Rotation angewendet)
3. In die Datenbank schreiben (die Volltextsuche aktualisiert sich über Trigger), Topic anlegen, falls es neu ist
4. Live-Event an verbundene Clients (SSE)
5. APNs-Push an jedes Gerät mit passendem Abo, sofern das Topic nicht stumm ist: über eine Queue mit Wiederholungen; Tokens, die Apple als ungültig meldet (`410`), werden entfernt

## Lesen und Verwalten (`/api/v1`)

Erlaubt mit einem Access Token vom Identity-Server (iOS-App) oder dem Session-Cookie der Web-Oberfläche.

| Aufruf | Zweck |
|---|---|
| `GET messages?topics=a,b&q=&unread=1&has_image=1&min_priority=&tags=a,b&since=&until=&before=<id>&after=<id>&limit=50` | Verlauf als `{"messages": […], "hasMore": …}`, neueste zuerst; mit `after` (ohne `before`) älteste zuerst, damit man mit der letzten ID weiterblättern kann. `q` ist eine Volltextsuche über Titel, Text und Tags (jedes Wort als Präfix). `min_priority` 1–5, `tags` müssen alle an der Nachricht hängen, `since` (einschließlich) und `until` (ausschließlich) sind Zeitpunkte in ISO 8601. `limit` 1–200 |
| `GET messages/{id}`, `DELETE messages/{id}` | eine Nachricht; Löschen entfernt auch die Dateien |
| `POST messages/{id}/read`, `POST messages/read` | gelesen setzen, einzeln oder gesammelt: `{"topic": …, "upToId": …}`, beides optional; ohne `upToId` alles, was es gerade gibt |
| `POST messages/{id}/actions/{index}` | Action ausführen; Antwort `{"index", "status", "ok", "runAt", "markedRead"}`, `status` 0 heißt: Ziel nicht erreicht |
| `GET topics`, `PATCH topics/{name}` | Topics mit Anzahl gesamt/ungelesen und letzter Nachricht, zuletzt aktive zuerst; Einstellungen ändern (`displayName`, `icon`, `color` als `#rrggbb`, `muted`, `retentionDays`; nur die mitgeschickten Felder, `null` löscht) |
| `GET attachments/{id}`, `GET attachments/{id}/thumb` | Original (`?download=true` zum Speichern) und Thumbnail |
| `GET stream?topics=…` | Server-Sent Events: `message` (die Nachricht wie oben), `read` (`ids` oder `topic` + `upToId`), `deleted` (`id`, `topic`) und `ping` beim Verbinden und alle 25 Sekunden |
| `GET devices`, `POST devices`, `PUT devices/{id}/subscriptions`, `DELETE devices/{id}` | eigene Geräte; registrieren mit `{"id"?, "name", "apnsToken", "environment", "topics"?}` (bekannt am APNs-Token oder an der `id`, die die App sich merkt, so bleibt das Gerät bei neuem Token dasselbe; neue Geräte abonnieren `["*"]`); Abos setzen mit `{"topics": […]}`; abmelden |
| `GET attachments/{id}/push-thumb?expires=&signature=` (ohne Anmeldung) | Thumbnail über den signierten Link aus einem Push, 15 Minuten gültig |
| `GET/POST/DELETE publish-tokens` | Publish-Tokens verwalten; `POST {"name", "topics": [...]}` gibt den Schlüssel als `token` zurück, nur dieses eine Mal |
| `GET config` (ohne Anmeldung) | Issuer, Client-ID der App und Audience für die Einrichtung der App, dazu `login`: `oidc` mit Identity-Server, `key` mit Zugangsschlüssel |
| `GET /health` (ohne Anmeldung) | Zustand für Docker und Blackbox-Probes |
| `GET /auth/login?returnUrl=`, `GET /auth/me`, `POST /auth/logout` | Anmeldung der Web-Oberfläche (siehe [Anmeldung](#anmeldung)): zum Identity-Server und zurück auf `returnUrl` (nur Pfade dieses Servers); der angemeldete Benutzer als `{"sub", "name"}` (`401` ohne Anmeldung, `403` ohne Rolle); abmelden hier und beim Identity-Server. Ohne Identity-Server führt `/auth/login` gleich zurück auf `returnUrl` |
| `POST /auth/key` `{"key"}` | Anmeldung der Web-Oberfläche mit dem Zugangsschlüssel, nur ohne Identity-Server (sonst `404`); `204` mit Session-Cookie, `401` bei falschem Schlüssel |

## Push (APNs)

- Token-Authentifizierung mit dem .p8-Schlüssel (ES256-JWT, alle 50 Minuten erneuert), HTTP/2 direkt über `HttpClient`. Ohne `Apns:KeyPath`, `KeyId` und `TeamId` gehen keine Pushes raus.
- Jede neue Nachricht kommt in eine Warteschlange; daraus geht ein Push an jedes Gerät, das das Topic (oder `*`) abonniert hat, sofern das Topic nicht stumm ist. Geräte mit `environment` = `sandbox` (Entwicklungs-Builds) gehen an `api.sandbox.push.apple.com`, die übrigen an `api.push.apple.com`.
- Antwortet APNs mit `429`, `5xx` oder gar nicht, wird es bis zu `Apns:MaxAttempts` (4) Mal versucht, nach `Apns:FirstRetryDelay` (10 s) und dann jeweils sechsmal so lange. Ein abgelaufener Provider-Token wird neu signiert. Geräte, die Apple als abgemeldet meldet (`410`), werden entfernt; `BadDeviceToken` zählt als `invalid_token`, entfernt aber nichts (meist falsche Umgebung).
- `alert` mit Titel und Text: Titel = Emojis der Tags + Titel der Nachricht, Untertitel = Topic; ohne Titel ist das Topic der Titel. Der Text wird gekürzt, damit der Payload unter 4 KB bleibt.
- `thread-id` = Topic, `badge` = Anzahl ungelesen (alle Topics), `category` = `message`, `sound` ab Priorität 3
- `interruption-level` aus der Priorität: 1–2 `passive` (mit `apns-priority` 5), 3 `active`, 4–5 `time-sensitive`
- Bei Bildern oder Actions `mutable-content: 1`, damit die Notification Service Extension läuft. Unter `ob` stehen `id`, `topic`, `click`, `thumbnail` (signierter Link aufs erste Vorschaubild: HMAC-SHA256 über Anhang und Ablaufzeit, 15 Minuten gültig, Schlüssel in `<DataPath>/url-signing.key`; absolut mit `Server:PublicUrl`) und `actions` (`index`, `label`). Die Extension lädt das Bild darüber ohne Anmeldung, und App und Extension kommen sich bei rotierenden Refresh Tokens nicht in die Quere.

## Anmeldung

Es gibt zwei Betriebsarten, entschieden durch `Oidc:Issuer`:

- **Mit Identity-Server** (`Oidc:Issuer` gesetzt, so läuft Marcels Server): wie unten beschrieben.
- **Ohne Identity-Server** (`Oidc:Issuer` leer, für alle, die Onkel Bummel selbst betreiben): Beim ersten Start legt der Server einen **Zugangsschlüssel** `obk_…` an (256 Zufallsbits) und schreibt ihn einmal ins Log; gespeichert wird nur sein SHA-256-Hash in `<DataPath>/access-key.sha256`. Der Schlüssel hat alle Rechte, wie die Rolle `admin`. Die Web-Oberfläche fragt nach ihm (`POST /auth/key`) und bekommt dafür dasselbe Session-Cookie wie mit Identity-Server; Skripte und Clients schicken ihn als `Authorization: Bearer obk_…`. `dotnet OnkelBummel.Api.dll --new-access-key` legt einen neuen an; der alte gilt dann nicht mehr, und alle Browser-Sitzungen damit enden. Publish-Tokens funktionieren in beiden Betriebsarten gleich. Die iOS-App kann sich mit dem Zugangsschlüssel noch nicht anmelden, und Pushes an die App aus dem App Store kann nur ein Server mit Marcels APNs-Schlüssel senden.

Im Admin des Identity-Servers einmal anlegen (keine Codeänderung dort nötig):

| Was | Werte |
|---|---|
| Scope `onkel-bummel-api` | Ressource/Audience `onkel-bummel-api` |
| Rolle `onkel-bummel` | dem eigenen Benutzer zuweisen |
| Client `onkel-bummel-web` | vertraulich, Zustimmung `implicit`, `authorization_code`, Redirect `https://onkel-bummel.<tailnet>.ts.net/auth/callback`, Post-Logout `https://onkel-bummel.<tailnet>.ts.net/auth/signed-out` (lokal zusätzlich dasselbe mit `http://localhost:8080`), Scopes `profile` und `roles` |
| Client `onkel-bummel-ios` | Public Client (PKCE), Zustimmung `implicit`, `authorization_code` + `refresh_token`, Redirect `com.b-velop.onkel-bummel:/oauth/callback`, Scope `onkel-bummel-api` |

Im Server gibt es drei Anmeldeverfahren mit Identity-Server und eines ohne:

- **Web-Oberfläche:** Cookie + OpenID Connect (Code mit PKCE, Antwort per Query, Scopes `openid profile roles`). Die Rolle steht im ID-Token. Der Browser bekommt nur das Session-Cookie `onkel-bummel` (HttpOnly, SameSite=Lax, 30 Tage, verlängert sich bei Benutzung), kein Token. Ein Refresh Token ist nicht nötig, weil der Server mit dem Token keine andere API aufruft. Änderungen über die API mit dem Cookie brauchen den Header `X-Requested-With`, sonst `403`; eine fremde Seite kann ihn ohne CORS-Preflight nicht setzen. Hinter `tailscale serve` mit `UseForwardedHeaders`.
- **iOS-App:** JWT-Bearer. Geprüft werden Issuer, Audience `onkel-bummel-api`, Ablauf, Signatur (JWKS aus der Discovery) und die Rolle `onkel-bummel` oder `admin` (`role` kann String oder Array sein). Der Benutzer wird über `sub` erkannt.
- **Sender:** Publish-Tokens `ob_…`, eigener Authentication-Handler
- **Ohne Identity-Server:** Zugangsschlüssel `obk_…`, eigener Authentication-Handler; mit gesetztem `Oidc:Issuer` zählt er nicht

## Web-Oberfläche

Vue 3 mit TypeScript, Vite, Tailwind CSS, Pinia und Axios in `backend/web/`. Der Build landet im `wwwroot` des Servers, der die App ausliefert: Jeder Pfad außerhalb von `/api` bekommt `index.html` (mit Content-Security-Policy, ohne externe Ressourcen), Dateien unter `/assets` tragen einen Hash und werden ein Jahr gecacht. Hell und dunkel nach Systemeinstellung, auch auf dem Handy benutzbar. Ohne Anmeldung geht es gleich zum Identity-Server, auf einem Server ohne Identity-Server fragt die Oberfläche nach dem Zugangsschlüssel.

- Topic-Liste mit Ungelesen-Zählern, daneben die Timeline (Infinite Scroll, live per SSE, nach jedem Wiederverbinden neu geladen); Ungelesen-Zahl im Tab-Titel
- Suche und Filter: Topic, Mindestpriorität, Tags, Zeitraum, nur ungelesen, nur mit Bildern; der Filter steht in der Adresse (`/topics/garage?q=tor&prio=4&tags=a,b&von=2026-11-01&bis=2026-11-03&ungelesen=1&bilder=1`)
- Galerie pro Topic und über alle Topics, mit Lightbox (Pfeiltasten) und Download des Originals
- Action-Buttons mit dem letzten Ergebnis
- Gelesen setzen (einzeln, pro Topic, alle), Löschen
- Nachricht senden (`/senden?topic=garage`): Topic mit Vorschlägen, Titel, Text, Priorität, Tags mit Emoji-Vorschau, Link und Anhänge (auswählen, hineinziehen oder Bilder in den Text einfügen). Geht als `multipart/form-data` an `POST messages` mit einem `Idempotency-Key` pro Entwurf, damit ein zweiter Klick oder eine Wiederholung nur eine Nachricht ergibt.
- Text als Markdown light (fett, kursiv, Code, Links), ohne HTML aus der Nachricht
- Verwaltung (`/verwaltung`): Topics (Anzeigename, Icon, Farbe, stumm, Aufbewahrung), Publish-Tokens, eigene Geräte und ihre Abos, QR-Code mit der Server-Adresse für die App

## iOS-App

SwiftUI ab iOS 17, in `ios/` (Xcode-Projekt aus `project.yml` mit XcodeGen, Logik ohne UI im Swift-Paket `OnkelBummelKit`).

- **Einrichtung:** Adresse eingeben oder QR-Code scannen (der QR-Code enthält nur die Server-Adresse) → `GET /api/v1/config` und Discovery des Identity-Servers → Anmeldung mit `ASWebAuthenticationSession`, Authorization Code mit PKCE, Scopes aus der Config (mit `offline_access`) → Gerät registrieren → APNs-Token hochladen. Der Refresh Token liegt in der Keychain; der Identity-Server rotiert ihn, deshalb erneuert immer nur eine Stelle in der App.
- **Tabs:** Inbox, Topics (mit Zählern, je Topic Timeline oder Galerie), Suche (Volltext auf dem Server, Filter Topic, ungelesen, mit Bildern), Einstellungen (Mitteilungen, Abos, Server, Diagnose, Abmelden)
- Lokaler Cache mit SwiftData. Abgleich per Cursor (`after=` ab der neuesten bekannten Nachricht), dazu jedes Mal die neuesten 100 Nachrichten, aus denen Gelesen-Status und Löschungen übernommen werden. Ältere Seiten lädt die Liste beim Scrollen (`before=`). Im Vordergrund live per SSE, mit Abgleich nach jedem (Wieder-)Verbinden.
- Bilder mit Thumbnails in der Liste, Detailansicht mit Zoom, Teilen und Sichern des Originals
- Notification Service Extension: hängt das Thumbnail über den signierten Link an (nach 5 Sekunden ohne Antwort nur Text) und legt eine Kategorie mit „Gelesen“, den Actions der Nachricht und „Öffnen“ an; Nachrichten mit denselben Labels teilen sich eine Kategorie.
- Aktionen in der Mitteilung: „Gelesen“, „Öffnen“ und die Actions der Nachricht. Die App führt sie im Hintergrund über die API aus.
- Wischgesten (gelesen, löschen; bei Topics gelesen, stumm), Badge = Anzahl ungelesen
- Alles außer dem Push-Text braucht Tailscale, am besten auf „On Demand“
- **Demo ohne Server** (für das App Review und zum Ausprobieren): „Ohne Server ausprobieren“ bei der Einrichtung. `DemoServer` in `OnkelBummelKit` beantwortet dieselbe API mit Beispieldaten im Speicher, über ein `URLProtocol` unter `https://demo.onkel-bummel.invalid/`; nichts geht ins Netz, keine Anmeldung, kein Gerät, kein SSE. Bilder zeichnet die App selbst („Beispielbild“). In den Einstellungen schickt „Beispielnachricht senden“ eine neue Nachricht und nach 5 Sekunden eine lokale Mitteilung dazu. Die Demo bleibt nach einem Neustart an und beginnt dann wieder mit den Beispieldaten; „Demo beenden“ führt zurück zur Einrichtung.
- Datenschutz und Impressum auf marcelbenders.com sind bei der Einrichtung und in den Einstellungen verlinkt. `PrivacyInfo.xcprivacy` in App und Extension: kein Tracking, keine erfassten Daten, `UserDefaults` mit den Gründen `CA92.1` (App) und `1C8F.1` (App Group).

Apple-Konto: Team `6GGU8ET2C4`, App `com.b-velop.onkel-bummel` (Push, App Groups, Time Sensitive Notifications), Extension `com.b-velop.onkel-bummel.NotificationService` (App Groups), App Group `group.com.b-velop.onkel-bummel`.

## Metriken und Logs

`/metrics` gibt es nur auf dem eigenen Port `Monitoring:MetricsPort` (Standard 9464). Dieser Port beantwortet nichts anderes, und die übrigen Ports beantworten `/metrics` nicht. Entscheidend ist der Port, auf dem die Verbindung ankommt. Labels haben nur wenige feste Werte, Topics, Inhalte, IDs und Benutzer kommen nicht vor.

| Messwert | Bedeutung |
|---|---|
| `onkelbummel_messages_received_total{source, result}` | eingegangene Nachrichten; `source`: `api`, `grafana`; `result`: `stored`, `invalid`, `unauthorized`, `forbidden`, `too_large`, `duplicate` |
| `onkelbummel_attachments_stored_total`, `onkelbummel_attachment_bytes_total` | gespeicherte Anhänge und ihre Größe |
| `onkelbummel_push_sent_total{result}` | Pushes an APNs: `ok`, `failed`, `invalid_token` |
| `onkelbummel_push_duration_seconds` | Dauer bis zur Antwort von APNs |
| `onkelbummel_push_queue_length` | wartende Pushes |
| `onkelbummel_actions_executed_total{result}` | ausgeführte Actions: `ok`, `failed` |
| `onkelbummel_sse_connections` | offene Live-Verbindungen |
| `onkelbummel_messages{state}` | Nachrichten in der Datenbank, `total` und `unread` (jede Minute gezählt) |
| `onkelbummel_devices`, `onkelbummel_publish_tokens` | registrierte Geräte und Publish-Tokens |
| `onkelbummel_database_size_bytes`, `onkelbummel_attachments_size_bytes` | Größe der Datenbank (mit WAL) und der Anhänge |
| `onkelbummel_job_runs_total{task, result}`, `onkelbummel_job_duration_seconds{task}`, `onkelbummel_job_last_success_timestamp_seconds{task}` | Hintergrund-Jobs `retention`, `cleanup`, `backup` |
| `onkelbummel_backup_last_success_timestamp_seconds` | letzte erfolgreiche Sicherung (Alarm „Sicherung zu alt“) |
| `onkelbummel_loki_lines_pushed_total`, `onkelbummel_loki_lines_dropped_total{reason}` | Logzeilen an Loki bzw. verworfen (`queue_full`, `push_failed`) |
| `http_requests_received_total`, `http_request_duration_seconds` | Anfragen nach Route-Vorlage, Methode und Status |
| `process_*`, `dotnet_*` | Prozess und Laufzeit |

Mit `Monitoring:Loki:Url` gehen die Logzeilen zusätzlich an Loki, mit den Labels `app=onkelbummel`, `host` (`Monitoring:Loki:Host`), `level` und `source` (`api`, `http`, `http-client`, `db`, `push`, `actions`, `auth`, `jobs`). Sie werden alle 2 Sekunden gebündelt verschickt. Ist Loki nicht erreichbar, werden sie verworfen, statt zu warten.

## Hintergrund-Jobs

- `retention` (täglich, `Jobs:MaintenanceAt`): löscht Nachrichten, die älter sind als die Aufbewahrung ihres Topics
- `cleanup` (gleich danach): löscht Anhang-Dateien ohne Nachricht, die älter als eine Stunde sind, und gibt Idempotency-Keys nach 24 Stunden frei
- `backup` (nächtlich, `Backup:At`): SQLite-Online-Backup plus Anhänge als ZIP per WebDAV in die Nextcloud, wie `identity-backup` im Identity-Server. Es entstehen `onkel-bummel-mon.zip` … `onkel-bummel-sun.zip` (die letzten sieben Tage) und `onkel-bummel-2026-10.zip` (der letzte Stand jedes Monats); in der Nextcloud wird nichts gelöscht. Mit `Backup:Passphrase` werden sie mit AES-256 verschlüsselt (`.zip.enc`, Format von `openssl enc -aes-256-cbc -pbkdf2 -iter 600000`). Ohne Nextcloud-Zugang aus.

Die Zeiten gelten in der Zeitzone des Containers (`TZ`).

## Datenmodell (SQLite)

| Tabelle | Spalten |
|---|---|
| `Topics` | `Name` (PK), `DisplayName`, `Icon`, `Color`, `Muted`, `RetentionDays` |
| `Messages` | `Id` (fortlaufend), `Topic`, `Title`, `Body`, `Priority`, `Tags` (JSON), `ClickUrl`, `Source`, `IdempotencyKey`, `CreatedAt`, `ReadAt` |
| `MessagesFts` | FTS5 über `Title`, `Body`, `Tags`, als External-Content-Tabelle von `Messages` mit Triggern |
| `Attachments` | `Id`, `MessageId`, `FileName`, `ContentType`, `Size`, `Sha256`, `Width`, `Height`, `Path`, `ThumbnailPath` |
| `MessageActions` | `MessageId` + `Index` (PK), `Label`, `Method`, `Url`, `Headers` (JSON), `Body`, `Clear`, `LastStatus`, `LastRunAt` |
| `Devices` | `Id`, `OwnerSub`, `Name`, `ApnsToken`, `Environment`, `CreatedAt`, `LastSeenAt` |
| `Subscriptions` | `DeviceId` + `Topic` (PK) |
| `PublishTokens` | `Id`, `Name`, `Hash`, `Topics` (JSON), `CreatedAt`, `LastUsedAt` |

## Konfiguration

Alles in `appsettings.json`, überschreibbar per Umgebungsvariable (`Abschnitt__Schlüssel`).

| Abschnitt | Schlüssel |
|---|---|
| `Server` | `PublicUrl`, `UseForwardedHeaders` |
| `Storage` | `DataPath` (Datenbank und Anhänge, im Container `/data`) |
| `Limits` | `MaxFileBytes`, `MaxFilesPerMessage`, `MaxRequestBytes` |
| `Oidc` | `Issuer`, `Audience`, `WebClientId`, `WebClientSecret`, `IosClientId`, `RequiredRole` |
| `Apns` | `KeyPath`, `KeyId`, `TeamId`, `BundleId`, `MaxAttempts`, `FirstRetryDelay` |
| `Monitoring` | `MetricsPort`, `InventoryInterval`, `Loki:Url`, `Loki:Host` |
| `Backup` | `NextcloudWebDavUrl`, `NextcloudUserName`, `NextcloudAppPassword`, `Passphrase`, `At` |
| `Jobs` | `Enabled`, `MaintenanceAt` (Uhrzeit für `retention` und `cleanup`, Standard 04:15) |

## Betrieb

Der Server läuft in einem unprivilegierten LXC auf Proxmox, den `deploy/proxmox/create-lxc.sh` anlegt. Tailscale läuft im LXC selbst, der Server als Docker-Container im Host-Netz des LXC (`deploy/docker-compose.yml`) und lauscht nur auf `127.0.0.1`. `tailscale serve` stellt die App unter `https://onkel-bummel.<tailnet>.ts.net` und die Metriken unter Port 9464 bereit, beide mit gültigem Zertifikat. Ausgehend erreicht der Server über Tailscale und MagicDNS den Identity-Server, Loki und die Ziele von Actions.

Neue Commits auf `main` testet und baut GitHub Actions (`.github/workflows/server-image.yml`) und legt das Image in der GitHub Container Registry ab. Ein systemd-Timer im LXC (`deploy/proxmox/update.sh`) zieht es und startet den Server neu; mit `IMAGE_REPO=` baut der LXC wie früher selbst. Kommt die neue Version nicht gesund hoch, laufen wieder das vorige Image und die Datenbank von vor dem Update. Einzelheiten in [deploy/proxmox/README.md](../deploy/proxmox/README.md).

Wer Onkel Bummel selbst betreibt, startet das Image aus der Registry mit [deploy/eigener-server/docker-compose.yml](../deploy/eigener-server/docker-compose.yml): ohne Identity-Server, mit Zugangsschlüssel, die Daten in einem Docker-Volume, erreichbar unter `http://localhost:8080`.

## Umstellung der bisherigen ntfy-Sender

1. **Slipways:** `NtfyClient` wird durch einen Client für `POST /api/v1/messages` ersetzt (JSON, Bearer-Token, `Idempotency-Key`). Der eigene ntfy-Container (`deploy/compose.ntfy.yaml`, `deploy/lxc/setup-ntfy.sh`) entfällt.
2. **fil-o-mat:** `Notifier` schickt multipart mit Thumbnail und Actions als JSON. Die Einstellungen in den Apps werden zu „Server-Adresse + Topic + Token“.
3. **Grafana-Alarme** (Tonwerk-AI, `deploy/monitoring`): Der Kontaktpunkt geht an `/api/v1/webhooks/grafana?topic=alarme` mit Bearer-Token.
4. Erst danach wird der ntfy-Container auf slipways-1 abgeschaltet, an dem heute auch die Grafana-Alarme hängen.
