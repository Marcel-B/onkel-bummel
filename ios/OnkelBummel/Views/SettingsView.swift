import OnkelBummelKit
import SwiftData
import SwiftUI
import UserNotifications

struct SettingsView: View {
    @Environment(AppModel.self) private var app
    @Environment(SyncService.self) private var sync
    @Environment(\.openURL) private var openURL
    @Query(sort: \CachedTopic.name) private var topics: [CachedTopic]
    @Query private var messages: [CachedMessage]
    @State private var authorization = UNAuthorizationStatus.notDetermined
    @State private var subscriptionError: String?
    @State private var confirmSignOut = false

    var body: some View {
        NavigationStack {
            Form {
                pushSection
                subscriptionsSection
                serverSection
                diagnosticsSection
                Section {
                    Button("Abmelden", role: .destructive) { confirmSignOut = true }
                }
            }
            .navigationTitle("Einstellungen")
            .task { authorization = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus }
            .confirmationDialog("Abmelden?", isPresented: $confirmSignOut, titleVisibility: .visible) {
                Button("Abmelden", role: .destructive) { Task { await app.signOut() } }
                Button("Anderen Server wählen", role: .destructive) { Task { await app.forgetServer() } }
            } message: {
                Text("Das Gerät bekommt danach keine Pushes mehr, der lokale Verlauf wird gelöscht.")
            }
        }
    }

    private var pushSection: some View {
        Section {
            switch authorization {
            case .authorized, .provisional, .ephemeral:
                Label("Mitteilungen erlaubt", systemImage: "bell.badge")
            case .denied:
                Button("Mitteilungen in den Einstellungen erlauben", systemImage: "bell.slash") {
                    if let url = URL(string: UIApplication.openNotificationSettingsURLString) { openURL(url) }
                }
            default:
                Button("Mitteilungen erlauben", systemImage: "bell") {
                    Task {
                        await PushService.requestAuthorization()
                        authorization = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
                    }
                }
            }
            if let device = app.device {
                LabeledContent("Gerät", value: device.name)
                LabeledContent("APNs", value: device.environment == APNsEnvironment.sandbox ? "Sandbox" : "Produktion")
            } else {
                Label("Gerät noch nicht registriert", systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
            }
            Button("Erneut registrieren") { Task { await app.registerDevice() } }
        } header: {
            Text("Push")
        } footer: {
            Text("Der Text einer Mitteilung kommt auch ohne Tailscale an, Bilder, Verlauf und Actions brauchen es.")
        }
    }

    private var subscriptionsSection: some View {
        Section {
            let subscribed = Set(app.device?.topics ?? [Topics.all])
            Toggle("Alle Topics", isOn: Binding(
                get: { subscribed.contains(Topics.all) },
                set: { all in setSubscriptions(all ? [Topics.all] : topics.map(\.name)) }))
            if !subscribed.contains(Topics.all) {
                ForEach(topics) { cached in
                    Toggle(cached.topic?.title ?? cached.name, isOn: Binding(
                        get: { subscribed.contains(cached.name) },
                        set: { on in
                            var topics = subscribed
                            if on { topics.insert(cached.name) } else { topics.remove(cached.name) }
                            setSubscriptions(topics.sorted())
                        }))
                }
            }
            if let subscriptionError {
                Text(subscriptionError).font(.caption).foregroundStyle(.red)
            }
        } header: {
            Text("Abos")
        } footer: {
            Text("Pushes kommen für abonnierte Topics, sofern das Topic nicht stumm ist. Der Verlauf zeigt immer alles.")
        }
        .disabled(app.device == nil)
    }

    private var serverSection: some View {
        Section("Server") {
            if let setup = app.setup {
                LabeledContent("Adresse", value: setup.serverURL.host() ?? setup.serverURL.absoluteString)
                LabeledContent("Anmeldung", value: setup.discovery.issuer.host() ?? "")
            }
            if let user = app.userName {
                LabeledContent("Benutzer", value: user)
            }
        }
    }

    private var diagnosticsSection: some View {
        Section("Diagnose") {
            LabeledContent("Live-Verbindung", value: sync.liveState.rawValue)
            LabeledContent("Letzter Abgleich", value: sync.lastSync?.formatted(date: .omitted, time: .standard) ?? "–")
            LabeledContent("Im Cache", value: "\(messages.count) Nachrichten")
            if let error = sync.lastError {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            Button("Jetzt abgleichen") { Task { await sync.refresh() } }
            LabeledContent("Version", value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
        }
    }

    private func setSubscriptions(_ topics: [String]) {
        Task {
            do {
                try await app.setSubscriptions(topics)
                subscriptionError = nil
            } catch {
                subscriptionError = error.localizedDescription
            }
        }
    }
}
