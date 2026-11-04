import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// The server of the demo mode: sample topics and messages that live only in memory on the device. It answers the
/// same REST API as the real server, through `DemoURLProtocol`, so the rest of the app needs no special cases.
public final class DemoServer: @unchecked Sendable {
    public static let shared = DemoServer()
    /// Never resolved: `.invalid` is reserved, and `DemoURLProtocol` answers everything under it.
    public static let baseURL = URL(string: "https://demo.onkel-bummel.invalid/")!

    /// A URLSession that sends every request to the demo server and nothing over the network.
    public static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [DemoURLProtocol.self]
        return URLSession(configuration: configuration)
    }()

    struct Answer {
        var status: Int
        var body: Data
        var contentType = "application/json"
    }

    private struct TopicSettings {
        var name: String
        var displayName: String
        var icon: String
        var color: String
        var muted = false
        var retentionDays: Int?
    }

    private let lock = NSLock()
    private var topics: [TopicSettings] = []
    /// Newest first.
    private var messages: [Message] = []
    private var nextId: Int64 = 1
    private var device: Device?
    private var samplesSent = 0
    private var images: (@Sendable (String) -> Data?)?

    public init(now: Date = .now) {
        reset(now: now)
    }

    /// Back to the sample data; the demo starts like this every time.
    public func reset(now: Date = .now) {
        lock.withLock {
            topics = Self.sampleTopics
            messages = Self.sampleMessages(now: now)
            nextId = (messages.map(\.id).max() ?? 0) + 1
            device = nil
            samplesSent = 0
        }
    }

    /// Draws the pictures of the sample messages by name (`print`, `camera`); the package has no UIKit.
    public func setImages(_ images: @escaping @Sendable (String) -> Data?) {
        lock.withLock { self.images = images }
    }

    /// A new message as if a service had sent it, for trying out notifications.
    @discardableResult
    public func sendSample(now: Date = .now) -> Message {
        lock.withLock {
            let template = Self.samples[samplesSent % Self.samples.count]
            samplesSent += 1
            var message = template
            message.id = nextId
            message.createdAt = now
            nextId += 1
            messages.insert(message, at: 0)
            return message
        }
    }

    // MARK: Requests

    func answer(_ request: URLRequest) -> Answer {
        guard let url = request.url else { return problem(400, "Keine Adresse.") }
        let method = request.httpMethod ?? "GET"
        let path = url.path
        if path.hasPrefix("/demo/files/") {
            let name = String(path.dropFirst("/demo/files/".count))
            guard let data = lock.withLock({ images })?(name) else { return problem(404, "Kein Bild.") }
            return Answer(status: 200, body: data, contentType: "image/jpeg")
        }
        guard path.hasPrefix("/api/v1/") else { return problem(404, "Nicht gefunden.") }
        let parts = path.dropFirst("/api/v1/".count).split(separator: "/").map(String.init)
        let query = Dictionary(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?
            .map { ($0.name, $0.value ?? "") } ?? [], uniquingKeysWith: { _, last in last })
        let body = Self.body(of: request)
        return lock.withLock { route(method, parts, query, body) }
    }

    private func route(_ method: String, _ parts: [String], _ query: [String: String], _ body: Data?) -> Answer {
        let part = parts + Array(repeating: "", count: max(0, 4 - parts.count))
        switch (method, parts.count, part[0], part[1], part[2]) {
        case ("GET", 1, "config", _, _):
            return json(ServerConfig(name: "Demo", issuer: nil, clientId: "demo", scopes: []))
        case ("GET", 1, "messages", _, _):
            return json(page(query))
        case ("POST", 2, "messages", "read", _):
            struct Body: Decodable { var topic: String?; var upToId: Int64? }
            let request = body.flatMap { try? JSON.decoder.decode(Body.self, from: $0) }
            let limit = request?.upToId ?? .max
            let count = markRead { $0.id <= limit && (request?.topic == nil || $0.topic == request?.topic) }
            return json(MarkReadResult(count: count))
        case let ("GET", 2, "messages", id, _):
            guard let message = messages.first(where: { String($0.id) == id }) else { return problem(404, "Nachricht nicht gefunden.") }
            return json(message)
        case let ("DELETE", 2, "messages", id, _):
            messages.removeAll { String($0.id) == id }
            return Answer(status: 204, body: Data())
        case let ("POST", 3, "messages", id, "read"):
            markRead { String($0.id) == id }
            return Answer(status: 204, body: Data())
        case let ("POST", 4, "messages", id, "actions"):
            return runAction(messageId: id, index: Int(part[3]) ?? -1)
        case ("GET", 1, "topics", _, _):
            return json(topics.map(topic))
        case let ("PATCH", 2, "topics", name, _):
            struct Body: Decodable { var muted: Bool? }
            guard let position = topics.firstIndex(where: { $0.name == name }) else { return problem(404, "Topic nicht gefunden.") }
            if let muted = body.flatMap({ try? JSON.decoder.decode(Body.self, from: $0) })?.muted {
                topics[position].muted = muted
            }
            return json(topic(topics[position]))
        case ("GET", 1, "devices", _, _):
            return json(device.map { [$0] } ?? [])
        case ("POST", 1, "devices", _, _):
            struct Body: Decodable { var name: String; var environment: String; var topics: [String]? }
            guard let request = body.flatMap({ try? JSON.decoder.decode(Body.self, from: $0) }) else { return problem(400, "Ungültiges Gerät.") }
            let now = Date.now
            device = Device(id: "demo", name: request.name, environment: request.environment,
                            topics: request.topics ?? device?.topics ?? [Topics.all],
                            createdAt: device?.createdAt ?? now, lastSeenAt: now)
            return json(device)
        case ("PUT", 3, "devices", _, "subscriptions"):
            struct Body: Decodable { var topics: [String] }
            guard var current = device, let request = body.flatMap({ try? JSON.decoder.decode(Body.self, from: $0) }) else {
                return problem(404, "Gerät nicht gefunden.")
            }
            current.topics = request.topics
            device = current
            return json(current)
        case ("DELETE", 2, "devices", _, _):
            device = nil
            return Answer(status: 204, body: Data())
        default:
            return problem(404, "Gibt es in der Demo nicht.")
        }
    }

    /// Like the server: newest first, with `after` oldest first; `q` searches title, text and tags.
    private func page(_ query: [String: String]) -> MessagePage {
        let limit = min(max(Int(query["limit"] ?? "") ?? 50, 1), 200)
        let topics = query["topics"].map { Set($0.split(separator: ",").map(String.init)) }
        let search = query["q"]?.lowercased().split(separator: " ").map(String.init) ?? []
        var found = messages.filter { message in
            if let topics, !topics.contains(message.topic) { return false }
            if query["unread"] == "1", message.readAt != nil { return false }
            if query["has_image"] == "1", !message.attachments.contains(where: \.isImage) { return false }
            if let before = query["before"].flatMap(Int64.init), message.id >= before { return false }
            if let after = query["after"].flatMap(Int64.init), message.id <= after { return false }
            let text = ([message.title ?? "", message.message] + message.tags).joined(separator: " ").lowercased()
            return search.allSatisfy { text.contains($0) }
        }
        if query["after"] != nil { found.reverse() }
        return MessagePage(messages: Array(found.prefix(limit)), hasMore: found.count > limit)
    }

    @discardableResult
    private func markRead(where matches: (Message) -> Bool) -> Int {
        let now = Date.now
        var count = 0
        for position in messages.indices where messages[position].readAt == nil && matches(messages[position]) {
            messages[position].readAt = now
            count += 1
        }
        return count
    }

    private func runAction(messageId: String, index: Int) -> Answer {
        guard let position = messages.firstIndex(where: { String($0.id) == messageId }),
              let action = messages[position].actions.firstIndex(where: { $0.index == index }) else {
            return problem(404, "Action nicht gefunden.")
        }
        let now = Date.now
        messages[position].actions[action].lastStatus = 200
        messages[position].actions[action].lastRunAt = now
        let markRead = messages[position].actions[action].clear && messages[position].readAt == nil
        if markRead { messages[position].readAt = now }
        return json(ActionRunResult(index: index, status: 200, ok: true, runAt: now, markedRead: markRead))
    }

    private func topic(_ settings: TopicSettings) -> Topic {
        let own = messages.filter { $0.topic == settings.name }
        let last = own.first.map {
            LastMessage(id: $0.id, title: $0.title, message: $0.message, priority: $0.priority, createdAt: $0.createdAt)
        }
        return Topic(name: settings.name, displayName: settings.displayName, icon: settings.icon, color: settings.color,
                     muted: settings.muted, retentionDays: settings.retentionDays, total: own.count,
                     unread: own.filter { $0.readAt == nil }.count, lastMessage: last)
    }

    private func json(_ value: some Encodable) -> Answer {
        guard let data = try? JSON.encoder.encode(value) else { return problem(500, "Antwort nicht lesbar.") }
        return Answer(status: 200, body: data)
    }

    private func problem(_ status: Int, _ detail: String) -> Answer {
        let data = (try? JSON.encoder.encode(Problem(title: nil, status: status, detail: detail))) ?? Data()
        return Answer(status: status, body: data, contentType: "application/problem+json")
    }

    /// URLSession hands the body to a URLProtocol as a stream, not as `httpBody`.
    private static func body(of request: URLRequest) -> Data? {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: buffer.count)
            guard read > 0 else { break }
            data.append(buffer, count: read)
        }
        return data
    }

    // MARK: Sample data

    private static let sampleTopics = [
        TopicSettings(name: "drucker", displayName: "3D-Drucker", icon: "🖨️", color: "#f97316"),
        TopicSettings(name: "garage", displayName: "Garage", icon: "🚗", color: "#3b82f6"),
        TopicSettings(name: "alarme", displayName: "Alarme", icon: "🚨", color: "#ef4444"),
        TopicSettings(name: "haus", displayName: "Zuhause", icon: "🏠", color: "#a855f7"),
        TopicSettings(name: "sicherung", displayName: "Sicherung", icon: "💾", color: "#22c55e", retentionDays: 30),
    ]

    private static func image(_ name: String, width: Int = 1200, height: Int = 800) -> Attachment {
        Attachment(id: "demo-\(name)", fileName: "\(name).jpg", contentType: "image/jpeg", size: 180_000,
                   width: width, height: height, url: "/demo/files/\(name)", thumbnailUrl: "/demo/files/\(name)")
    }

    private static func sampleMessages(now: Date) -> [Message] {
        func ago(_ minutes: Double) -> Date { now.addingTimeInterval(-minutes * 60) }
        var list = [
            Message(id: 0, topic: "drucker", title: "Druck fertig", message: "Boot.gcode nach 1 h 42 min, 12 g Filament.",
                    priority: 3, tags: ["tada", "printer"], source: "api", createdAt: ago(4),
                    attachments: [image("print")],
                    actions: [MessageAction(index: 0, label: "Nächsten Druck starten", url: "https://drucker.example/start", clear: true)]),
            Message(id: 0, topic: "garage", title: "Bewegung erkannt", message: "Kamera Einfahrt, 22:41 Uhr.",
                    priority: 4, tags: ["camera"], createdAt: ago(18), attachments: [image("camera")]),
            Message(id: 0, topic: "alarme", title: "[FIRING] Festplatte fast voll",
                    message: "Auf dem Server sind nur noch 6 % frei (Schwelle 10 %).", priority: 5,
                    tags: ["rotating_light"], source: "grafana", createdAt: ago(35)),
            Message(id: 0, topic: "haus", title: "Waschmaschine fertig", message: "Wäsche kann raus.", priority: 3,
                    tags: ["droplet"], createdAt: ago(52)),
            Message(id: 0, topic: "garage", title: "Tor offen", message: "Das Garagentor ist seit 30 Minuten offen.",
                    priority: 4, tags: ["warning", "door"], createdAt: ago(75),
                    actions: [MessageAction(index: 0, label: "Tor schließen", url: "https://garage.example/close", clear: true)]),
            Message(id: 0, topic: "drucker", title: "Filament fast leer", message: "Auf der Spule PLA Schwarz sind noch etwa 40 g.",
                    priority: 4, tags: ["warning"], createdAt: ago(130), readAt: ago(120)),
            Message(id: 0, topic: "alarme", title: "[RESOLVED] Backup-Ziel nicht erreichbar",
                    message: "Das Backup-Ziel antwortet wieder.", priority: 3, tags: ["white_check_mark"],
                    source: "grafana", createdAt: ago(240), readAt: ago(200)),
            Message(id: 0, topic: "haus", title: "Briefkasten", message: "Post ist da.", priority: 2, tags: ["mailbox"],
                    createdAt: ago(420), readAt: ago(400)),
            Message(id: 0, topic: "drucker", title: "Druck abgebrochen", message: "Halter.gcode bei 63 %: Filament gerissen.",
                    priority: 4, tags: ["x", "printer"], createdAt: ago(1500), readAt: ago(1400),
                    attachments: [image("print-failed")]),
        ]
        for day in 0..<7 {
            list.append(Message(id: 0, topic: "sicherung", title: "Sicherung fertig",
                                message: "Alle Daten gesichert, \(14 + day % 3) Minuten.", priority: 2,
                                tags: ["floppy_disk"], createdAt: ago(Double(day) * 1440 + 180),
                                readAt: day == 0 ? nil : ago(Double(day) * 1440)))
        }
        list.sort { $0.createdAt > $1.createdAt }
        for position in list.indices { list[position].id = Int64(list.count - position) }
        return list
    }

    private static let samples = [
        Message(id: 0, topic: "drucker", title: "Druck fertig", message: "Ein Beispiel: so kommt eine Nachricht mit Bild an.",
                priority: 3, tags: ["tada", "printer"], createdAt: .now, attachments: [image("print")],
                actions: [MessageAction(index: 0, label: "Nächsten Druck starten", url: "https://drucker.example/start", clear: true)]),
        Message(id: 0, topic: "alarme", title: "[FIRING] Temperatur Serverschrank",
                message: "Ein Beispiel für einen dringenden Alarm: 41 °C (Schwelle 35 °C).", priority: 5,
                tags: ["thermometer"], source: "grafana", createdAt: .now),
        Message(id: 0, topic: "haus", title: "Spülmaschine fertig", message: "Ein Beispiel für eine leise Nachricht.",
                priority: 2, tags: ["droplet"], createdAt: .now),
    ]
}

/// Answers every request to `DemoServer.baseURL` from `DemoServer.shared`.
public final class DemoURLProtocol: URLProtocol {
    override public class func canInit(with request: URLRequest) -> Bool {
        request.url?.host == DemoServer.baseURL.host
    }

    override public class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override public func startLoading() {
        let answer = DemoServer.shared.answer(request)
        guard let url = request.url,
              let response = HTTPURLResponse(url: url, statusCode: answer.status, httpVersion: "HTTP/1.1",
                                             headerFields: ["Content-Type": answer.contentType]) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: answer.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override public func stopLoading() {}
}

/// Access tokens for the demo server, which takes any.
public struct DemoTokens: AccessTokenSource {
    public init() {}
    public func accessToken() async throws -> String { "demo" }
    public func tokenRejected(_ token: String) async {}
}

extension APIClient {
    /// The API of the demo server.
    public static var demo: APIClient {
        APIClient(baseURL: DemoServer.baseURL, tokens: DemoTokens(), session: DemoServer.session)
    }
}
