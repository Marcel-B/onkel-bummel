import SwiftUI

struct InboxView: View {
    @Environment(AppModel.self) private var app
    @Environment(SyncService.self) private var sync
    @AppStorage("inboxUnreadOnly") private var unreadOnly = false
    @State private var path: [Int64] = []

    var body: some View {
        NavigationStack(path: $path) {
            MessageList(unreadOnly: unreadOnly)
                .id(unreadOnly)
                .navigationTitle(unreadOnly ? "Ungelesen" : "Inbox")
                .navigationDestination(for: Int64.self) { MessageDetailView(id: $0) }
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        LiveIndicator()
                    }
                    ToolbarItem(placement: .topBarTrailing) {
                        Menu {
                            Toggle("Nur ungelesene", systemImage: "circle.fill", isOn: $unreadOnly)
                            Button("Alle als gelesen markieren", systemImage: "checkmark.circle") {
                                Task { await sync.markAllRead(topic: nil) }
                            }
                        } label: {
                            Image(systemName: "line.3.horizontal.decrease.circle")
                        }
                    }
                }
        }
        .onChange(of: app.openMessageId, initial: true) { _, id in
            guard let id else { return }
            path = [id]
            app.openMessageId = nil
        }
    }
}

/// A dot that shows whether live updates are coming in.
struct LiveIndicator: View {
    @Environment(SyncService.self) private var sync

    var body: some View {
        Image(systemName: "circle.fill")
            .font(.system(size: 8))
            .foregroundStyle(sync.liveState == .connected ? .green : sync.liveState == .off ? .gray : .orange)
            .accessibilityLabel("Live-Verbindung \(sync.liveState.rawValue)")
    }
}
