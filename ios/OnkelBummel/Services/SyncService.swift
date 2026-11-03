import Foundation
import OnkelBummelKit
import SwiftData
import UserNotifications

/// Keeps the SwiftData cache in step with the server: catching up by cursor, live events while in the foreground,
/// and the changes the user makes (read, delete, actions).
@MainActor
@Observable
final class SyncService {
    enum LiveState: String {
        case off = "aus"
        case connecting = "verbindet"
        case connected = "verbunden"
        case waiting = "wartet auf neuen Versuch"
    }

    private(set) var liveState = LiveState.off
    private(set) var lastSync: Date?
    private(set) var isRefreshing = false
    var lastError: String?

    /// Newest messages fetched on every refresh; read states and deletions within them are taken over.
    static let recentPage = 100

    private let context: ModelContext
    private let api: @MainActor () -> APIClient?
    private var liveTask: Task<Void, Never>?
    private var topicRefresh: Task<Void, Never>?

    init(context: ModelContext, api: @escaping @MainActor () -> APIClient?) {
        self.context = context
        self.api = api
    }

    // MARK: Catching up

    func refresh() async {
        guard let api = api(), !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }
        do {
            // Everything after the newest cached message, oldest first, so no gap opens up.
            if var cursor = context.edgeMessageId(newest: true) {
                while true {
                    let page = try await api.messages(MessageQuery(after: cursor, limit: 200))
                    context.upsert(page.messages)
                    guard page.hasMore, let last = page.messages.last?.id else { break }
                    cursor = last
                }
            }
            let recent = try await api.messages(MessageQuery(limit: Self.recentPage))
            context.upsert(recent.messages)
            removeDeleted(within: recent)
            try await refreshTopics(api)
            try context.save()
            lastSync = .now
            lastError = nil
        } catch {
            lastError = error.localizedDescription
        }
    }

    /// Pages back from the oldest cached message; false when there is nothing older.
    @discardableResult
    func loadOlder(topic: String? = nil, imagesOnly: Bool = false) async -> Bool {
        guard let api = api() else { return false }
        do {
            let before = context.edgeMessageId(newest: false, topic: topic, imagesOnly: imagesOnly)
            let page = try await api.messages(MessageQuery(
                topics: topic.map { [$0] }, imagesOnly: imagesOnly, before: before, limit: 50))
            context.upsert(page.messages)
            try context.save()
            return page.hasMore
        } catch {
            lastError = error.localizedDescription
            return false
        }
    }

    /// A message the cache does not have yet, e.g. opened from a push before the sync ran.
    func fetch(_ id: Int64) async -> Message? {
        guard let api = api(), let message = try? await api.message(id) else { return nil }
        context.upsert([message])
        try? context.save()
        return message
    }

    private func refreshTopics(_ api: APIClient) async throws {
        let topics = try await api.topics()
        let names = Set(topics.map(\.name))
        for cached in (try? context.fetch(FetchDescriptor<CachedTopic>())) ?? [] where !names.contains(cached.name) {
            context.delete(cached)
        }
        for topic in topics {
            let name = topic.name
            var descriptor = FetchDescriptor<CachedTopic>(predicate: #Predicate { $0.name == name })
            descriptor.fetchLimit = 1
            if let cached = try? context.fetch(descriptor).first {
                cached.update(topic)
            } else {
                context.insert(CachedTopic(topic))
            }
        }
        try context.save()
        updateBadge()
    }

    /// The newest page covers an unbroken range of ids; what the cache has in that range but the page not is gone.
    private func removeDeleted(within page: MessagePage) {
        guard let newest = page.messages.first?.id, let oldest = page.messages.last?.id else { return }
        let present = Set(page.messages.map(\.id))
        let descriptor = FetchDescriptor<CachedMessage>(predicate: #Predicate { $0.id >= oldest && $0.id <= newest })
        for cached in (try? context.fetch(descriptor)) ?? [] where !present.contains(cached.id) {
            context.delete(cached)
        }
    }

    private func refreshTopicsSoon() {
        topicRefresh?.cancel()
        topicRefresh = Task {
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled, let api = api() else { return }
            try? await refreshTopics(api)
        }
    }

    // MARK: Live events

    /// Follows `GET stream` while the app is in the foreground and catches up after every (re)connect.
    func startLive() {
        guard liveTask == nil else { return }
        liveTask = Task { [weak self] in
            var failures = 0
            while !Task.isCancelled {
                guard let self else { return }
                do {
                    try await self.follow()
                    failures = 0
                } catch is CancellationError {
                    break
                } catch {
                    failures += 1
                    self.lastError = error.localizedDescription
                }
                if Task.isCancelled { break }
                self.liveState = .waiting
                let delays = [2, 5, 10, 30]
                try? await Task.sleep(for: .seconds(delays[min(failures, delays.count - 1)]))
            }
        }
    }

    func stopLive() {
        liveTask?.cancel()
        liveTask = nil
        liveState = .off
    }

    private func follow() async throws {
        guard let api = api() else { throw APIError.notSignedIn }
        liveState = .connecting
        let (bytes, response) = try await URLSession.shared.bytes(for: try await api.streamRequest())
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw APIError.http(status: (response as? HTTPURLResponse)?.statusCode ?? 0, detail: nil)
        }
        liveState = .connected
        await refresh()
        var parser = ServerSentEventParser()
        for try await byte in bytes {
            if let event = parser.feed(byte: byte), let live = LiveEvent(event) {
                apply(live)
            }
        }
    }

    private func apply(_ event: LiveEvent) {
        switch event {
        case let .message(message):
            context.upsert([message])
            refreshTopicsSoon()
        case let .read(ids, topic, upToId, readAt):
            let descriptor: FetchDescriptor<CachedMessage>
            if !ids.isEmpty {
                descriptor = FetchDescriptor(predicate: #Predicate { ids.contains($0.id) && $0.readAt == nil })
            } else if let topic {
                let limit = upToId ?? .max
                descriptor = FetchDescriptor(predicate: #Predicate { $0.topic == topic && $0.id <= limit && $0.readAt == nil })
            } else {
                let limit = upToId ?? .max
                descriptor = FetchDescriptor(predicate: #Predicate { $0.id <= limit && $0.readAt == nil })
            }
            for cached in (try? context.fetch(descriptor)) ?? [] { cached.readAt = readAt }
            refreshTopicsSoon()
        case let .deleted(id, _):
            if let cached = context.cachedMessage(id) { context.delete(cached) }
            refreshTopicsSoon()
        case .ping:
            break
        }
        try? context.save()
    }

    // MARK: Changes by the user

    func markRead(_ id: Int64) async {
        if let cached = context.cachedMessage(id), cached.readAt == nil {
            cached.readAt = .now
            adjustUnread(topic: cached.topic, by: -1)
        }
        do {
            try await api()?.markRead(id)
        } catch {
            lastError = error.localizedDescription
        }
    }

    /// Up to the newest message the app knows, so one arriving meanwhile stays unread.
    func markAllRead(topic: String?) async {
        let upToId = context.edgeMessageId(newest: true, topic: topic)
        let limit = upToId ?? .max
        let descriptor = topic.map { name in
            FetchDescriptor<CachedMessage>(predicate: #Predicate { $0.topic == name && $0.id <= limit && $0.readAt == nil })
        } ?? FetchDescriptor<CachedMessage>(predicate: #Predicate { $0.id <= limit && $0.readAt == nil })
        let now = Date.now
        for cached in (try? context.fetch(descriptor)) ?? [] { cached.readAt = now }
        try? context.save()
        do {
            try await api()?.markAllRead(topic: topic, upToId: upToId)
            if let api = api() { try await refreshTopics(api) }
        } catch {
            lastError = error.localizedDescription
        }
    }

    func delete(_ id: Int64) async {
        if let cached = context.cachedMessage(id) {
            if cached.readAt == nil { adjustUnread(topic: cached.topic, by: -1) }
            context.delete(cached)
            try? context.save()
        }
        do {
            try await api()?.deleteMessage(id)
        } catch {
            lastError = error.localizedDescription
        }
    }

    func runAction(messageId: Int64, index: Int) async throws -> ActionRunResult {
        guard let api = api() else { throw APIError.notSignedIn }
        let result = try await api.runAction(messageId: messageId, index: index)
        if let cached = context.cachedMessage(messageId) {
            var message = cached.message
            if let position = message.actions.firstIndex(where: { $0.index == index }) {
                message.actions[position].lastStatus = result.status
                message.actions[position].lastRunAt = result.runAt
            }
            if result.markedRead, message.readAt == nil {
                message.readAt = result.runAt
                adjustUnread(topic: message.topic, by: -1)
            }
            cached.update(message)
            try? context.save()
        }
        return result
    }

    func setMuted(_ muted: Bool, topic: String) async {
        guard let api = api() else { return }
        do {
            _ = try await api.setMuted(muted, topic: topic)
            try await refreshTopics(api)
        } catch {
            lastError = error.localizedDescription
        }
    }

    // MARK: Badge and reset

    func updateBadge() {
        let unread = ((try? context.fetch(FetchDescriptor<CachedTopic>())) ?? []).reduce(0) { $0 + $1.unread }
        UNUserNotificationCenter.current().setBadgeCount(unread)
    }

    private func adjustUnread(topic: String, by delta: Int) {
        var descriptor = FetchDescriptor<CachedTopic>(predicate: #Predicate { $0.name == topic })
        descriptor.fetchLimit = 1
        if let cached = try? context.fetch(descriptor).first {
            cached.unread = max(0, cached.unread + delta)
            if var topic = cached.topic {
                topic.unread = cached.unread
                cached.update(topic)
            }
        }
        try? context.save()
        updateBadge()
    }

    func clear() {
        stopLive()
        try? context.delete(model: CachedMessage.self)
        try? context.delete(model: CachedTopic.self)
        try? context.save()
        lastSync = nil
        UNUserNotificationCenter.current().setBadgeCount(0)
    }
}
