import OnkelBummelKit
import SwiftUI
import VisionKit

/// Server address (typed or scanned), then sign-in at the identity server.
struct SetupView: View {
    @Environment(AppModel.self) private var app
    // Prefilled by the launch argument "-setupServer <address>", e.g. for tests in the simulator.
    @State private var address = UserDefaults.standard.string(forKey: "setupServer") ?? ""
    @State private var busy = false
    @State private var error: String?
    @State private var scanning = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Onkel Bummel").font(.largeTitle.bold())
                        Text("Nachrichten von deinen Diensten, mit Verlauf und Bildern.")
                            .foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 8)
                }

                if let setup = app.setup, app.phase == .signedOut {
                    signIn(setup)
                } else {
                    server
                    demo
                }

                if let error {
                    Section {
                        Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                    } footer: {
                        Text("Server und Anmeldung sind nur im Tailnet erreichbar: Tailscale muss an sein.")
                    }
                }

                Section {
                    Link("Datenschutz", destination: WebPages.privacy)
                    Link("Impressum", destination: WebPages.imprint)
                }
            }
            .disabled(busy)
            .sheet(isPresented: $scanning) {
                QRScannerView { code in
                    scanning = false
                    address = code
                    Task { await connect() }
                }
                .ignoresSafeArea()
            }
        }
    }

    private var server: some View {
        Section {
            TextField("onkel-bummel.tail….ts.net", text: $address)
                .textContentType(.URL)
                .keyboardType(.URL)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .onSubmit { Task { await connect() } }
            Button {
                Task { await connect() }
            } label: {
                HStack {
                    Text("Verbinden")
                    if busy { Spacer(); ProgressView() }
                }
            }
            .disabled(ServerAddress.parse(address) == nil)
            if DataScannerViewController.isSupported {
                Button("QR-Code scannen", systemImage: "qrcode.viewfinder") { scanning = true }
            }
        } header: {
            Text("Server")
        } footer: {
            Text("Die Adresse steht in der Web-Oberfläche unter Verwaltung, dort auch als QR-Code.")
        }
    }

    private var demo: some View {
        Section {
            Button("Ohne Server ausprobieren", systemImage: "play.circle") {
                Task { await app.startDemo() }
            }
        } footer: {
            Text("Zeigt die App mit Beispielnachrichten. Die Demo läuft nur auf diesem Gerät, nichts wird gesendet.")
        }
    }

    private func signIn(_ setup: ServerSetup) -> some View {
        Section {
            LabeledContent("Server", value: setup.serverURL.host() ?? setup.serverURL.absoluteString)
            LabeledContent("Anmeldung", value: setup.discovery.issuer.host() ?? "")
            Button {
                Task { await signIn() }
            } label: {
                HStack {
                    Text("Anmelden")
                    if busy { Spacer(); ProgressView() }
                }
            }
            Button("Anderen Server wählen", role: .destructive) {
                Task { await app.forgetServer() }
            }
        } header: {
            Text("Anmelden")
        }
    }

    private func connect() async {
        guard let url = ServerAddress.parse(address) else {
            error = "Das ist keine Server-Adresse."
            return
        }
        busy = true
        defer { busy = false }
        do {
            try await app.connect(to: url)
            error = nil
            await signIn()
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func signIn() async {
        busy = true
        defer { busy = false }
        do {
            try await app.signIn()
            error = nil
        } catch OAuthError.cancelled {
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }
}

/// Camera view that hands over the first QR code it reads.
struct QRScannerView: UIViewControllerRepresentable {
    let onCode: (String) -> Void

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let scanner = DataScannerViewController(
            recognizedDataTypes: [.barcode(symbologies: [.qr])], qualityLevel: .fast, isHighlightingEnabled: true)
        scanner.delegate = context.coordinator
        try? scanner.startScanning()
        return scanner
    }

    func updateUIViewController(_ scanner: DataScannerViewController, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(onCode: onCode) }

    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        let onCode: (String) -> Void
        private var done = false

        init(onCode: @escaping (String) -> Void) { self.onCode = onCode }

        func dataScanner(_ scanner: DataScannerViewController, didAdd items: [RecognizedItem], allItems: [RecognizedItem]) {
            for case let .barcode(code) in items {
                guard !done, let value = code.payloadStringValue else { continue }
                done = true
                scanner.stopScanning()
                onCode(value)
            }
        }
    }
}
