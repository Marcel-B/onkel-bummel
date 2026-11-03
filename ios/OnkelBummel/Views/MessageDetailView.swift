import OnkelBummelKit
import SwiftData
import SwiftUI

struct MessageDetailView: View {
    let id: Int64

    @Environment(SyncService.self) private var sync
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    @Query private var cached: [CachedMessage]
    @Query private var topics: [CachedTopic]
    @State private var viewerIndex: Int?
    @State private var runningAction: Int?
    @State private var actionError: String?
    @State private var sharing: URL?

    init(id: Int64) {
        self.id = id
        _cached = Query(filter: #Predicate { $0.id == id })
    }

    var body: some View {
        Group {
            if let message = cached.first?.message {
                content(message)
            } else {
                ProgressView().task { _ = await sync.fetch(id) }
            }
        }
        .navigationBarTitleDisplayMode(.inline)
    }

    private func content(_ message: Message) -> some View {
        let topic = topics.lazy.compactMap(\.topic).first { $0.name == message.topic }
        let images = message.attachments.filter { $0.thumbnailUrl != nil }
        let files = message.attachments.filter { $0.thumbnailUrl == nil }
        return ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        TopicChip(name: message.topic, topic: topic)
                        PriorityMark(priority: message.priority)
                        Spacer()
                        Text(message.createdAt.formatted(date: .abbreviated, time: .shortened))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Text(Emoji.title(message.title, tags: message.tags, fallback: topic?.title ?? message.topic))
                        .font(.title2.bold())
                        .textSelection(.enabled)
                }

                if !message.message.isEmpty {
                    Text(Self.markdown(message.message))
                        .textSelection(.enabled)
                }

                if !images.isEmpty {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 8)], spacing: 8) {
                        ForEach(Array(images.enumerated()), id: \.element.id) { index, image in
                            Button { viewerIndex = index } label: {
                                // The cell sets the size; the picture fills it and is cut to it.
                                Color.clear
                                    .frame(height: 140)
                                    .frame(maxWidth: .infinity)
                                    .overlay { RemoteImage(path: image.thumbnailUrl!) }
                                    .clipShape(RoundedRectangle(cornerRadius: 10))
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }

                ForEach(files) { file in
                    Button {
                        Task { sharing = try? await ImageLoader.shared.file(for: file) }
                    } label: {
                        Label("\(file.fileName) · \(ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))",
                              systemImage: "doc")
                    }
                }

                if !message.actions.isEmpty {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(message.actions) { action in
                            actionButton(action, message: message)
                        }
                        if let actionError {
                            Text(actionError).font(.caption).foregroundStyle(.red)
                        }
                    }
                }

                if let click = message.click.flatMap(URL.init(string:)) {
                    Button("Öffnen", systemImage: "safari") { openURL(click) }
                }

                let tags = Emoji.plainTags(message.tags)
                if !tags.isEmpty {
                    Text(tags.map { "#\($0)" }.joined(separator: " "))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding()
        }
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button("Löschen", systemImage: "trash", role: .destructive) {
                    Task {
                        await sync.delete(message.id)
                        dismiss()
                    }
                }
            }
        }
        .task(id: message.id) {
            if message.readAt == nil { await sync.markRead(message.id) }
        }
        .fullScreenCover(item: Binding(get: { viewerIndex.map(IndexBox.init) }, set: { viewerIndex = $0?.value })) { box in
            ImageViewer(images: images, selection: box.value)
        }
        .sheet(item: Binding(get: { sharing.map(URLBox.init) }, set: { sharing = $0?.url })) { box in
            ShareSheet(items: [box.url])
        }
    }

    private func actionButton(_ action: MessageAction, message: Message) -> some View {
        Button {
            Task {
                runningAction = action.index
                defer { runningAction = nil }
                do {
                    let result = try await sync.runAction(messageId: message.id, index: action.index)
                    actionError = result.ok ? nil : result.status == 0 ? "Ziel nicht erreicht." : "Ziel antwortete \(result.status)."
                } catch {
                    actionError = error.localizedDescription
                }
            }
        } label: {
            HStack {
                Text(action.label)
                Spacer()
                if runningAction == action.index {
                    ProgressView()
                } else if let status = action.lastStatus {
                    Image(systemName: (200..<300).contains(status) ? "checkmark.circle.fill" : "xmark.circle.fill")
                        .foregroundStyle((200..<300).contains(status) ? .green : .red)
                }
            }
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
        .disabled(runningAction != nil)
    }

    /// Markdown light: emphasis, code and links, line breaks as written.
    static func markdown(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}

private struct IndexBox: Identifiable {
    let value: Int
    var id: Int { value }
}

private struct URLBox: Identifiable {
    let url: URL
    var id: URL { url }
}

struct ShareSheet: UIViewControllerRepresentable {
    let items: [Any]

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: items, applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}
