import OnkelBummelKit
import SwiftData
import SwiftUI

struct TopicsView: View {
    @Environment(SyncService.self) private var sync
    @Query(sort: \CachedTopic.lastMessageId, order: .reverse) private var topics: [CachedTopic]

    var body: some View {
        NavigationStack {
            List(topics.compactMap(\.topic)) { topic in
                NavigationLink(value: topic.name) {
                    TopicRow(topic: topic)
                }
                .swipeActions(edge: .leading) {
                    if topic.unread > 0 {
                        Button("Gelesen", systemImage: "checkmark") { Task { await sync.markAllRead(topic: topic.name) } }
                            .tint(.blue)
                    }
                }
                .swipeActions(edge: .trailing) {
                    Button(topic.muted ? "Laut" : "Stumm", systemImage: topic.muted ? "bell" : "bell.slash") {
                        Task { await sync.setMuted(!topic.muted, topic: topic.name) }
                    }
                    .tint(.orange)
                }
            }
            .listStyle(.plain)
            .overlay {
                if topics.isEmpty {
                    ContentUnavailableView("Keine Topics", systemImage: "square.stack",
                                           description: Text("Ein Topic entsteht mit seiner ersten Nachricht."))
                }
            }
            .refreshable { await sync.refresh() }
            .navigationTitle("Topics")
            .navigationDestination(for: String.self) { name in TopicDetailView(name: name) }
            .navigationDestination(for: Int64.self) { MessageDetailView(id: $0) }
        }
    }
}

private struct TopicRow: View {
    let topic: Topic

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                Circle().fill(Color(hex: topic.color).opacity(0.2))
                Text(topic.icon ?? String(topic.title.prefix(1)).uppercased())
                    .font(topic.icon == nil ? .headline : .title3)
            }
            .frame(width: 40, height: 40)
            VStack(alignment: .leading, spacing: 2) {
                HStack {
                    Text(topic.title).font(.headline)
                    if topic.muted { Image(systemName: "bell.slash").font(.caption).foregroundStyle(.secondary) }
                }
                if let last = topic.lastMessage {
                    Text(last.title ?? last.message).font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 4) {
                if let last = topic.lastMessage {
                    Text(last.createdAt.listLabel).font(.caption).foregroundStyle(.secondary)
                }
                if topic.unread > 0 {
                    Text("\(topic.unread)")
                        .font(.caption.bold())
                        .foregroundStyle(.white)
                        .padding(.horizontal, 7)
                        .padding(.vertical, 2)
                        .background(Color.accentColor, in: Capsule())
                }
            }
        }
    }
}

struct TopicDetailView: View {
    let name: String

    enum Mode: String, CaseIterable { case timeline = "Timeline", gallery = "Galerie" }

    @Environment(SyncService.self) private var sync
    @Query private var topics: [CachedTopic]
    @State private var mode = Mode.timeline

    init(name: String) {
        self.name = name
        _topics = Query(filter: #Predicate { $0.name == name })
    }

    var body: some View {
        let topic = topics.first?.topic
        Group {
            switch mode {
            case .timeline: MessageList(topic: name)
            case .gallery: GalleryView(topic: name)
            }
        }
        .navigationTitle(topic?.title ?? name)
        .toolbar {
            ToolbarItem(placement: .principal) {
                Picker("Ansicht", selection: $mode) {
                    ForEach(Mode.allCases, id: \.self) { Text($0.rawValue) }
                }
                .pickerStyle(.segmented)
                .frame(width: 200)
            }
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button("Alle als gelesen markieren", systemImage: "checkmark.circle") {
                        Task { await sync.markAllRead(topic: name) }
                    }
                    if let topic {
                        Button(topic.muted ? "Pushes wieder an" : "Stumm schalten", systemImage: topic.muted ? "bell" : "bell.slash") {
                            Task { await sync.setMuted(!topic.muted, topic: name) }
                        }
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
    }
}

/// The pictures of a topic in a grid; a tap opens the message.
struct GalleryView: View {
    let topic: String

    @Environment(SyncService.self) private var sync
    @Query private var messages: [CachedMessage]
    @State private var hasMore = true

    init(topic: String) {
        self.topic = topic
        _messages = Query(filter: #Predicate { $0.topic == topic && $0.hasImage }, sort: \CachedMessage.id, order: .reverse)
    }

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 2)], spacing: 2) {
                ForEach(messages) { cached in
                    let message = cached.message
                    ForEach(message.attachments.filter { $0.thumbnailUrl != nil }) { image in
                        NavigationLink(value: message.id) {
                            Color.clear
                                .aspectRatio(1, contentMode: .fit)
                                .overlay { RemoteImage(path: image.thumbnailUrl!) }
                                .clipped()
                        }
                    }
                }
            }
            if hasMore {
                ProgressView()
                    .padding()
                    .task(id: messages.count) { hasMore = await sync.loadOlder(topic: topic, imagesOnly: true) }
            }
        }
        .overlay {
            if messages.isEmpty && !hasMore {
                ContentUnavailableView("Keine Bilder", systemImage: "photo.on.rectangle")
            }
        }
    }
}
