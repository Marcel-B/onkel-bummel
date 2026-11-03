import Foundation

/// One server-sent event as it came over the wire.
public struct ServerSentEvent: Sendable, Equatable {
    public var type: String
    public var data: String
}

/// Reads `text/event-stream` line by line: `event:` and `data:` fields, an empty line ends an event.
public struct ServerSentEventParser: Sendable {
    private var type = ""
    private var data: [String] = []
    private var line: [UInt8] = []

    public init() {}

    /// Feeds the stream byte by byte, as `URLSession.AsyncBytes` delivers it; its `lines` would drop the empty
    /// lines that end events.
    public mutating func feed(byte: UInt8) -> ServerSentEvent? {
        guard byte == 0x0A else {
            line.append(byte)
            return nil
        }
        if line.last == 0x0D { line.removeLast() }
        defer { line.removeAll(keepingCapacity: true) }
        return feed(String(decoding: line, as: UTF8.self))
    }

    /// Feeds one line without its line break; returns the event an empty line completes.
    public mutating func feed(_ line: String) -> ServerSentEvent? {
        if line.isEmpty {
            defer { type = ""; data = [] }
            guard !data.isEmpty else { return nil }
            return ServerSentEvent(type: type.isEmpty ? "message" : type, data: data.joined(separator: "\n"))
        }
        if line.hasPrefix(":") { return nil }
        let (field, value) = Self.split(line)
        switch field {
        case "event": type = value
        case "data": data.append(value)
        default: break
        }
        return nil
    }

    private static func split(_ line: String) -> (String, String) {
        guard let colon = line.firstIndex(of: ":") else { return (line, "") }
        var value = line[line.index(after: colon)...]
        if value.first == " " { value = value.dropFirst() }
        return (String(line[..<colon]), String(value))
    }
}

/// What `GET stream` reports.
public enum LiveEvent: Sendable, Equatable {
    case message(Message)
    /// Single messages by id, or a whole topic (all topics when nil) up to an id.
    case read(ids: [Int64], topic: String?, upToId: Int64?, readAt: Date)
    case deleted(id: Int64, topic: String)
    case ping

    public init?(_ event: ServerSentEvent) {
        let data = Data(event.data.utf8)
        switch event.type {
        case "message":
            guard let message = try? JSON.decoder.decode(Message.self, from: data) else { return nil }
            self = .message(message)
        case "read":
            struct Read: Decodable { var ids: [Int64]; var topic: String?; var upToId: Int64?; var readAt: Date }
            guard let read = try? JSON.decoder.decode(Read.self, from: data) else { return nil }
            self = .read(ids: read.ids, topic: read.topic, upToId: read.upToId, readAt: read.readAt)
        case "deleted":
            struct Deleted: Decodable { var id: Int64; var topic: String }
            guard let deleted = try? JSON.decoder.decode(Deleted.self, from: data) else { return nil }
            self = .deleted(id: deleted.id, topic: deleted.topic)
        case "ping":
            self = .ping
        default:
            return nil
        }
    }
}
