import SwiftUI

@main
struct OnkelBummelApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @Environment(\.scenePhase) private var scenePhase
    @State private var app = AppModel.shared

    init() {
        // Attachments never change under their id; the server sends them as immutable.
        URLCache.shared = URLCache(memoryCapacity: 32 << 20, diskCapacity: 512 << 20)
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(app)
                .environment(app.sync)
        }
        .modelContainer(app.container)
        .onChange(of: scenePhase) { _, phase in
            switch phase {
            case .active: Task { await app.didBecomeActive() }
            case .background: app.didEnterBackground()
            default: break
            }
        }
    }
}
