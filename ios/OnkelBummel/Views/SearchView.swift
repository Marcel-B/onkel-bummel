import OnkelBummelKit
import SwiftData
import SwiftUI

/// Full-text search on the server over title, text and tags, each word as a prefix.
struct SearchView: View {
    @Environment(AppModel.self) private var app
    @Environment(SyncService.self) private var sync
    @Query private var topics: [CachedTopic]
    @State private var text = ""
    @State private var topic: String?
    @State private var unreadOnly = false
    @State private var imagesOnly = false
    @State private var results: [Message] = []
    @State private var hasMore = false
    @State private var searching = false
    @State private var error: String?

    private var query: MessageQuery {
        MessageQuery(topics: topic.map { [$0] }, search: text, unreadOnly: unreadOnly, imagesOnly: imagesOnly, limit: 50)
    }

    private var isEmpty: Bool { text.trimmingCharacters(in: .whitespaces).isEmpty && !unreadOnly && !imagesOnly && topic == nil }

    var body: some View {
        let names = Dictionary(topics.compactMap(\.topic).map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })
        NavigationStack {
            List {
                Section {
                    filters
                }
                ForEach(results) { message in
                    NavigationLink(value: message.id) {
                        MessageRow(message: message, topic: names[message.topic])
                    }
                }
                if hasMore {
                    ProgressView().frame(maxWidth: .infinity).task { await loadMore() }
                }
            }
            .listStyle(.plain)
            .overlay {
                if let error {
                    ContentUnavailableView("Suche gescheitert", systemImage: "exclamationmark.triangle", description: Text(error))
                } else if !isEmpty && results.isEmpty && !searching {
                    ContentUnavailableView.search(text: text)
                }
            }
            .searchable(text: $text, prompt: "Titel, Text, Tags")
            .navigationTitle("Suche")
            .navigationDestination(for: Int64.self) { MessageDetailView(id: $0) }
            .task(id: query) { await search() }
        }
    }

    private var filters: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack {
                Menu {
                    Button("Alle Topics") { topic = nil }
                    ForEach(topics.compactMap(\.topic)) { item in
                        Button(item.title) { topic = item.name }
                    }
                } label: {
                    FilterChip(title: topic.flatMap { names(for: $0) } ?? "Alle Topics", active: topic != nil, systemImage: "square.stack")
                }
                Button { unreadOnly.toggle() } label: {
                    FilterChip(title: "Ungelesen", active: unreadOnly, systemImage: "circle.fill")
                }
                Button { imagesOnly.toggle() } label: {
                    FilterChip(title: "Mit Bildern", active: imagesOnly, systemImage: "photo")
                }
            }
            .buttonStyle(.plain)
        }
        .listRowSeparator(.hidden)
    }

    private func names(for name: String) -> String? {
        topics.first { $0.name == name }?.topic?.title ?? name
    }

    private func search() async {
        guard !isEmpty else {
            results = []
            hasMore = false
            error = nil
            return
        }
        // Waits for the typing to pause; a newer query cancels this one.
        try? await Task.sleep(for: .milliseconds(300))
        guard !Task.isCancelled, let api = app.api else { return }
        searching = true
        defer { searching = false }
        do {
            let page = try await api.messages(query)
            guard !Task.isCancelled else { return }
            results = page.messages
            hasMore = page.hasMore
            error = nil
        } catch is CancellationError {
        } catch {
            if !Task.isCancelled { self.error = error.localizedDescription }
        }
    }

    private func loadMore() async {
        guard let api = app.api, let last = results.last?.id else { return }
        var next = query
        next.before = last
        guard let page = try? await api.messages(next) else { hasMore = false; return }
        results += page.messages
        hasMore = page.hasMore
    }
}

private struct FilterChip: View {
    let title: String
    let active: Bool
    let systemImage: String

    var body: some View {
        Label(title, systemImage: systemImage)
            .font(.subheadline)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(active ? Color.accentColor.opacity(0.2) : Color.secondary.opacity(0.12), in: Capsule())
    }
}
