import OnkelBummelKit
import SwiftData
import SwiftUI

/// Cached messages, newest first, with swipe actions and paging back on reaching the end.
struct MessageList: View {
    @Environment(SyncService.self) private var sync
    @Query private var messages: [CachedMessage]
    @Query private var topics: [CachedTopic]
    @State private var hasMore = true

    private let topic: String?

    init(topic: String? = nil, unreadOnly: Bool = false) {
        self.topic = topic
        var predicate: Predicate<CachedMessage>?
        if let topic {
            predicate = unreadOnly ? #Predicate { $0.topic == topic && $0.readAt == nil } : #Predicate { $0.topic == topic }
        } else if unreadOnly {
            predicate = #Predicate { $0.readAt == nil }
        }
        _messages = Query(filter: predicate, sort: \CachedMessage.id, order: .reverse)
    }

    var body: some View {
        let names = Dictionary(topics.compactMap(\.topic).map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })
        List {
            ForEach(messages) { cached in
                let message = cached.message
                NavigationLink(value: message.id) {
                    MessageRow(message: message, topic: names[message.topic], showTopic: topic == nil)
                }
                .swipeActions(edge: .leading) {
                    if message.readAt == nil {
                        Button("Gelesen", systemImage: "checkmark") { Task { await sync.markRead(message.id) } }
                            .tint(.blue)
                    }
                }
                .swipeActions(edge: .trailing) {
                    Button("Löschen", systemImage: "trash", role: .destructive) { Task { await sync.delete(message.id) } }
                }
            }
            if hasMore {
                HStack {
                    Spacer()
                    ProgressView()
                    Spacer()
                }
                .listRowSeparator(.hidden)
                .task(id: messages.count) { hasMore = await sync.loadOlder(topic: topic) }
            }
        }
        .listStyle(.plain)
        .overlay {
            if messages.isEmpty && !hasMore {
                ContentUnavailableView("Keine Nachrichten", systemImage: "tray",
                                       description: Text("Was Onkel Bummel bringt, steht hier."))
            }
        }
        .refreshable { await sync.refresh() }
    }
}

struct MessageRow: View {
    let message: Message
    var topic: Topic?
    var showTopic = true

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Circle()
                .fill(message.readAt == nil ? Color.accentColor : .clear)
                .frame(width: 8, height: 8)
                .padding(.top, 6)
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline) {
                    Text(Emoji.title(message.title, tags: message.tags, fallback: topic?.title ?? message.topic))
                        .font(.headline)
                        .fontWeight(message.readAt == nil ? .semibold : .regular)
                        .lineLimit(2)
                    Spacer(minLength: 4)
                    PriorityMark(priority: message.priority)
                    Text(message.createdAt.listLabel)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if !message.message.isEmpty {
                    Text(message.message)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .lineLimit(3)
                }
                if showTopic || !Emoji.plainTags(message.tags).isEmpty || !message.actions.isEmpty {
                    HStack(spacing: 6) {
                        if showTopic { TopicChip(name: message.topic, topic: topic) }
                        ForEach(Emoji.plainTags(message.tags).prefix(3), id: \.self) { tag in
                            Text(tag).font(.caption2).foregroundStyle(.secondary)
                        }
                        if !message.actions.isEmpty {
                            Image(systemName: "hand.tap").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            if let image = message.attachments.first(where: { $0.thumbnailUrl != nil }), let thumbnail = image.thumbnailUrl {
                RemoteImage(path: thumbnail)
                    .frame(width: 64, height: 64)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
            }
        }
        .padding(.vertical, 2)
    }
}

struct TopicChip: View {
    let name: String
    var topic: Topic?

    var body: some View {
        Text("\(topic?.icon.map { $0 + " " } ?? "")\(topic?.title ?? name)")
            .font(.caption2.weight(.medium))
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Color(hex: topic?.color).opacity(0.18), in: Capsule())
    }
}

struct PriorityMark: View {
    let priority: Int

    var body: some View {
        switch priority {
        case 5: Image(systemName: "exclamationmark.2").foregroundStyle(.red).font(.caption)
        case 4: Image(systemName: "exclamationmark").foregroundStyle(.orange).font(.caption)
        case 1: Image(systemName: "arrow.down").foregroundStyle(.secondary).font(.caption)
        default: EmptyView()
        }
    }
}

extension Date {
    /// Time for today, weekday within the week, date before that.
    var listLabel: String {
        let calendar = Calendar.current
        if calendar.isDateInToday(self) { return formatted(date: .omitted, time: .shortened) }
        if let days = calendar.dateComponents([.day], from: self, to: .now).day, days < 6 {
            return formatted(.dateTime.weekday(.abbreviated).hour().minute())
        }
        return formatted(date: .numeric, time: .omitted)
    }
}

extension Color {
    /// "#3b82f6" as a color; gray without one.
    init(hex: String?) {
        guard let hex, hex.count == 7, hex.hasPrefix("#"), let value = UInt32(hex.dropFirst(), radix: 16) else {
            self = .gray
            return
        }
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255,
                  blue: Double(value & 0xFF) / 255)
    }
}
