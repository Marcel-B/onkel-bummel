import SwiftUI

struct RootView: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        @Bindable var app = app
        switch app.phase {
        case .setup, .signedOut:
            SetupView()
        case .ready:
            TabView(selection: $app.selectedTab) {
                InboxView()
                    .tabItem { Label("Inbox", systemImage: "tray") }
                    .tag(AppModel.Tab.inbox)
                TopicsView()
                    .tabItem { Label("Topics", systemImage: "square.stack") }
                    .tag(AppModel.Tab.topics)
                SearchView()
                    .tabItem { Label("Suche", systemImage: "magnifyingglass") }
                    .tag(AppModel.Tab.search)
                SettingsView()
                    .tabItem { Label("Einstellungen", systemImage: "gear") }
                    .tag(AppModel.Tab.settings)
            }
        }
    }
}
