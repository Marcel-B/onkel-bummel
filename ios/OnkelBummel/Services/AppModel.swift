import Foundation
import OnkelBummelKit
import SwiftData
import UIKit
import UserNotifications

/// Server, identity server and discovery as found during setup; kept so the app starts without asking them again.
struct ServerSetup: Codable, Equatable {
    var serverURL: URL
    var config: ServerConfig
    var discovery: OIDCDiscovery
}

/// State of the whole app: setup, sign-in, the API, the device registration and where to navigate.
@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()

    enum Phase { case setup, signedOut, ready }

    enum Tab: Hashable { case inbox, topics, search, settings }

    private(set) var phase: Phase
    private(set) var setup: ServerSetup?
    private(set) var api: APIClient?
    private(set) var device: Device?
    private(set) var userName: String?
    /// The demo mode: sample data from `DemoServer`, no server, no sign-in, no pushes from outside.
    private(set) var isDemo = false
    var selectedTab = Tab.inbox
    /// Set when a push was tapped; the inbox opens that message.
    var openMessageId: Int64?

    let auth: AuthService
    let container: ModelContainer
    let sync: SyncService

    private static let setupKey = "serverSetup"
    private static let deviceIdKey = "deviceId"
    private static let deviceTokenKey = "apnsToken"
    private static let demoKey = "demo"

    private var deviceToken: String? {
        get { UserDefaults.standard.string(forKey: Self.deviceTokenKey) }
        set { UserDefaults.standard.set(newValue, forKey: Self.deviceTokenKey) }
    }

    private init() {
        let setup = UserDefaults.standard.data(forKey: Self.setupKey).flatMap { try? JSONDecoder().decode(ServerSetup.self, from: $0) }
        // Configured right away: a notification button may start the app in the background and call the API at once.
        auth = AuthService(client: setup.map(Self.oauthClient)) { await AppModel.shared.signInRequired() }
        do {
            container = try ModelContainer(for: CachedMessage.self, CachedTopic.self)
        } catch {
            fatalError("SwiftData store could not be opened: \(error)")
        }
        var api: APIClient?
        if let setup { api = APIClient(baseURL: setup.serverURL, tokens: auth) }
        self.setup = setup
        self.api = api
        sync = SyncService(context: container.mainContext) { AppModel.shared.api }
        phase = setup == nil ? .setup : .signedOut

        if setup != nil {
            Task { await self.restore() }
        } else if UserDefaults.standard.bool(forKey: Self.demoKey) {
            Task { await self.startDemo() }
        }
    }

    private func restore() async {
        if await auth.isSignedIn {
            await signedIn()
        }
    }

    // MARK: Setup and sign-in

    /// Asks the server where to sign in and loads the identity server's discovery document.
    func connect(to serverURL: URL) async throws {
        let config = try await APIClient(baseURL: serverURL, tokens: nil).config()
        guard let issuer = config.issuer else {
            throw APIError.http(status: 503, detail: "Am Server ist noch kein Identity-Server eingetragen (Oidc:Issuer).")
        }
        let discovery = try await OIDCDiscovery.load(issuer: issuer)
        let setup = ServerSetup(serverURL: serverURL, config: config, discovery: discovery)
        UserDefaults.standard.set(try JSONEncoder().encode(setup), forKey: Self.setupKey)
        SharedSettings.serverURL = serverURL
        self.setup = setup
        api = APIClient(baseURL: serverURL, tokens: auth)
        await auth.configure(Self.oauthClient(for: setup))
        phase = .signedOut
    }

    func signIn() async throws {
        try await auth.signIn(with: WebAuthenticator())
        await signedIn()
    }

    private func signedIn() async {
        let claims = await auth.claims
        userName = claims["name"] ?? claims["preferred_username"] ?? claims["sub"]
        phase = .ready
        await PushService.requestAuthorization()
        await registerDevice()
        await sync.refresh()
    }

    private func signInRequired() {
        sync.stopLive()
        phase = .signedOut
    }

    /// Signs the device off at the server and forgets tokens and cache; the server address stays.
    func signOut() async {
        if isDemo { return endDemo() }
        if let id = device?.id ?? UserDefaults.standard.string(forKey: Self.deviceIdKey) {
            try? await api?.deleteDevice(id)
        }
        UserDefaults.standard.removeObject(forKey: Self.deviceIdKey)
        device = nil
        await auth.signOut()
        sync.clear()
        phase = .signedOut
    }

    /// Back to entering a server address.
    func forgetServer() async {
        await signOut()
        UserDefaults.standard.removeObject(forKey: Self.setupKey)
        SharedSettings.serverURL = nil
        setup = nil
        api = nil
        phase = .setup
    }

    // MARK: Demo

    /// Shows the app with sample data, without a server; starts afresh every time.
    func startDemo() async {
        DemoServer.shared.reset()
        DemoServer.shared.setImages { DemoImages.jpeg($0) }
        UserDefaults.standard.set(true, forKey: Self.demoKey)
        sync.clear()
        isDemo = true
        api = .demo
        userName = "Demo"
        phase = .ready
        await sync.refresh()
    }

    /// Back to entering a server address; the sample data goes with it.
    func endDemo() {
        sync.clear()
        UserDefaults.standard.removeObject(forKey: Self.demoKey)
        isDemo = false
        api = nil
        userName = nil
        phase = .setup
    }

    /// A sample message, and a few seconds later the notification for it, so it also shows on the lock screen.
    func sendDemoMessage() async {
        guard isDemo else { return }
        await PushService.requestAuthorization()
        let message = DemoServer.shared.sendSample()
        let content = UNMutableNotificationContent()
        content.title = Emoji.title(message.title, tags: message.tags, fallback: message.topic)
        content.body = message.message
        content.sound = .default
        content.categoryIdentifier = NotificationIdentifiers.messageCategory
        content.interruptionLevel = message.priority >= 4 ? .timeSensitive : message.priority <= 2 ? .passive : .active
        content.userInfo = ["ob": ["id": NSNumber(value: message.id), "topic": message.topic] as [String: Any]]
        let trigger = UNTimeIntervalNotificationTrigger(timeInterval: 5, repeats: false)
        try? await UNUserNotificationCenter.current().add(
            UNNotificationRequest(identifier: "demo-\(message.id)", content: content, trigger: trigger))
        try? await Task.sleep(for: .seconds(5))
        await sync.refresh()
    }

    private nonisolated static func oauthClient(for setup: ServerSetup) -> OAuthClient {
        OAuthClient(discovery: setup.discovery, clientId: setup.config.clientId, redirectURI: AuthService.redirectURI,
                    scopes: setup.config.scopes)
    }

    // MARK: Device

    func didRegister(deviceToken: Data) async {
        self.deviceToken = deviceToken.map { String(format: "%02x", $0) }.joined()
        await registerDevice()
    }

    /// Tells the server the APNs token; also when it is unchanged, so the server sees the device is alive.
    func registerDevice() async {
        guard phase == .ready, !isDemo, let api, let deviceToken else { return }
        do {
            let device = try await api.register(RegisterDevice(
                id: UserDefaults.standard.string(forKey: Self.deviceIdKey),
                name: UIDevice.current.name,
                apnsToken: deviceToken,
                environment: Self.apnsEnvironment))
            UserDefaults.standard.set(device.id, forKey: Self.deviceIdKey)
            self.device = device
        } catch {
            sync.lastError = "Gerät nicht registriert: \(error.localizedDescription)"
        }
    }

    func setSubscriptions(_ topics: [String]) async throws {
        guard let api, let device else { return }
        self.device = try await api.setSubscriptions(deviceId: device.id, topics: topics)
    }

    static var apnsEnvironment: String {
        #if targetEnvironment(simulator)
        APNsEnvironment.sandbox
        #else
        APNsEnvironment.from(provisioningProfile: Bundle.main.url(forResource: "embedded", withExtension: "mobileprovision")
            .flatMap { try? Data(contentsOf: $0) })
        #endif
    }

    // MARK: Foreground

    func didBecomeActive() async {
        guard phase == .ready, !isDemo else { return }
        sync.startLive()
        await registerDevice()
    }

    func didEnterBackground() {
        sync.stopLive()
        sync.updateBadge()
    }

    func open(messageId: Int64) {
        selectedTab = .inbox
        openMessageId = messageId
    }
}
