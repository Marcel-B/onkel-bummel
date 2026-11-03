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
    var selectedTab = Tab.inbox
    /// Set when a push was tapped; the inbox opens that message.
    var openMessageId: Int64?

    let auth: AuthService
    let container: ModelContainer
    let sync: SyncService

    private static let setupKey = "serverSetup"
    private static let deviceIdKey = "deviceId"
    private static let deviceTokenKey = "apnsToken"

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

        if setup != nil { Task { await self.restore() } }
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
        guard phase == .ready, let api, let deviceToken else { return }
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
        guard phase == .ready else { return }
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
