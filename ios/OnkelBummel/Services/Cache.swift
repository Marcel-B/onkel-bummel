import Foundation
import OnkelBummelKit
import SwiftData

/// A message in the local cache: the fields lists filter and sort by, plus the whole message as JSON.
@Model
final class CachedMessage {
    @Attribute(.unique) var id: Int64
    var topic: String
    var createdAt: Date
    var readAt: Date?
    var hasImage: Bool
    var payload: Data

    init(_ message: Message) {
        id = message.id
        topic = message.topic
        createdAt = message.createdAt
        readAt = message.readAt
        hasImage = message.attachments.contains { $0.isImage }
        payload = (try? JSON.encoder.encode(message)) ?? Data()
    }

    var message: Message {
        var message = (try? JSON.decoder.decode(Message.self, from: payload))
            ?? Message(id: id, topic: topic, message: "", createdAt: createdAt)
        message.readAt = readAt
        return message
    }

    var isUnread: Bool { readAt == nil }

    func update(_ message: Message) {
        topic = message.topic
        createdAt = message.createdAt
        readAt = message.readAt
        hasImage = message.attachments.contains { $0.isImage }
        payload = (try? JSON.encoder.encode(message)) ?? payload
    }
}

/// A topic with its counts, as `GET topics` returned it last.
@Model
final class CachedTopic {
    @Attribute(.unique) var name: String
    var lastMessageId: Int64
    var unread: Int
    var payload: Data

    init(_ topic: Topic) {
        name = topic.name
        lastMessageId = topic.lastMessage?.id ?? 0
        unread = topic.unread
        payload = (try? JSON.encoder.encode(topic)) ?? Data()
    }

    var topic: Topic? { try? JSON.decoder.decode(Topic.self, from: payload) }

    func update(_ topic: Topic) {
        lastMessageId = topic.lastMessage?.id ?? 0
        unread = topic.unread
        payload = (try? JSON.encoder.encode(topic)) ?? payload
    }
}

extension ModelContext {
    func cachedMessage(_ id: Int64) -> CachedMessage? {
        var descriptor = FetchDescriptor<CachedMessage>(predicate: #Predicate { $0.id == id })
        descriptor.fetchLimit = 1
        return try? fetch(descriptor).first
    }

    func upsert(_ messages: [Message]) {
        for message in messages {
            if let cached = cachedMessage(message.id) {
                cached.update(message)
            } else {
                insert(CachedMessage(message))
            }
        }
    }

    /// Smallest or largest cached id, optionally within topics or only messages with images.
    func edgeMessageId(newest: Bool, topic: String? = nil, imagesOnly: Bool = false) -> Int64? {
        var descriptor = FetchDescriptor<CachedMessage>(sortBy: [SortDescriptor(\.id, order: newest ? .reverse : .forward)])
        if let topic {
            descriptor.predicate = imagesOnly
                ? #Predicate { $0.topic == topic && $0.hasImage }
                : #Predicate { $0.topic == topic }
        } else if imagesOnly {
            descriptor.predicate = #Predicate { $0.hasImage }
        }
        descriptor.fetchLimit = 1
        return try? fetch(descriptor).first?.id
    }
}
