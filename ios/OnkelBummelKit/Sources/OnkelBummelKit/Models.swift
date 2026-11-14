import Foundation

/// Answer of `GET /api/v1/config`: where and how the app signs in.
public struct ServerConfig: Codable, Sendable, Equatable {
    public var name: String
    public var issuer: URL?
    public var clientId: String
    public var scopes: [String]

    public init(name: String, issuer: URL?, clientId: String, scopes: [String]) {
        self.name = name
        self.issuer = issuer
        self.clientId = clientId
        self.scopes = scopes
    }
}

public struct Attachment: Codable, Sendable, Hashable, Identifiable {
    public var id: String
    public var fileName: String
    public var contentType: String
    public var size: Int64
    public var width: Int?
    public var height: Int?
    /// Original file, relative to the server.
    public var url: String
    /// JPEG of at most 600 px; nil for files that are not images.
    public var thumbnailUrl: String?

    public var isImage: Bool { contentType.hasPrefix("image/") }

    public init(id: String, fileName: String, contentType: String, size: Int64, width: Int? = nil, height: Int? = nil,
                url: String, thumbnailUrl: String? = nil) {
        self.id = id
        self.fileName = fileName
        self.contentType = contentType
        self.size = size
        self.width = width
        self.height = height
        self.url = url
        self.thumbnailUrl = thumbnailUrl
    }
}

/// Button on a message; the server makes the HTTP call.
public struct MessageAction: Codable, Sendable, Hashable, Identifiable {
    public var index: Int
    public var label: String
    public var method: String
    public var url: String
    public var clear: Bool
    /// HTTP status of the last run; 0 when the target did not answer.
    public var lastStatus: Int?
    public var lastRunAt: Date?

    public var id: Int { index }

    public init(index: Int, label: String, method: String = "POST", url: String, clear: Bool = false,
                lastStatus: Int? = nil, lastRunAt: Date? = nil) {
        self.index = index
        self.label = label
        self.method = method
        self.url = url
        self.clear = clear
        self.lastStatus = lastStatus
        self.lastRunAt = lastRunAt
    }
}

public struct Message: Codable, Sendable, Hashable, Identifiable {
    public var id: Int64
    public var topic: String
    public var title: String?
    public var message: String
    public var priority: Int
    public var tags: [String]
    public var click: String?
    public var source: String
    public var createdAt: Date
    public var readAt: Date?
    public var attachments: [Attachment]
    public var actions: [MessageAction]

    public init(id: Int64, topic: String, title: String? = nil, message: String, priority: Int = 3, tags: [String] = [],
                click: String? = nil, source: String = "api", createdAt: Date, readAt: Date? = nil,
                attachments: [Attachment] = [], actions: [MessageAction] = []) {
        self.id = id
        self.topic = topic
        self.title = title
        self.message = message
        self.priority = priority
        self.tags = tags
        self.click = click
        self.source = source
        self.createdAt = createdAt
        self.readAt = readAt
        self.attachments = attachments
        self.actions = actions
    }
}

public struct MessagePage: Codable, Sendable {
    public var messages: [Message]
    /// More lie beyond the last one; page on with its id.
    public var hasMore: Bool
}

public struct LastMessage: Codable, Sendable, Hashable {
    public var id: Int64
    public var title: String?
    public var message: String
    public var priority: Int
    public var createdAt: Date
}

public struct Topic: Codable, Sendable, Hashable, Identifiable {
    public var name: String
    public var displayName: String?
    public var icon: String?
    /// Hex color such as "#3b82f6".
    public var color: String?
    public var muted: Bool
    public var retentionDays: Int?
    public var total: Int
    public var unread: Int
    public var lastMessage: LastMessage?

    public var id: String { name }
    public var title: String { displayName ?? name }
}

public struct Device: Codable, Sendable, Hashable, Identifiable {
    public var id: String
    public var name: String
    public var environment: String
    /// Topic names, or ["*"] for all.
    public var topics: [String]
    public var createdAt: Date
    public var lastSeenAt: Date
}

public struct ActionRunResult: Codable, Sendable, Hashable {
    public var index: Int
    /// HTTP status of the target; 0 when it did not answer.
    public var status: Int
    public var ok: Bool
    public var runAt: Date
    public var markedRead: Bool
}

public struct MarkReadResult: Codable, Sendable {
    public var count: Int
}

/// Answer of `POST messages/delete`: how many of the messages were still there.
public struct DeleteMessagesResult: Codable, Sendable {
    public var count: Int
}

/// RFC 7807 problem as the server sends it; `detail` is German and meant for people.
public struct Problem: Codable, Sendable, Error, LocalizedError {
    public var title: String?
    public var status: Int?
    public var detail: String?

    public var errorDescription: String? { detail ?? title ?? "Fehler \(status ?? 0)" }
}

public enum Topics {
    /// Stands for every topic in subscriptions.
    public static let all = "*"
}
