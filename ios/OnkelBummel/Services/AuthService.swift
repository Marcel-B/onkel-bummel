import AuthenticationServices
import Foundation
import OnkelBummelKit
import UIKit

/// Keeps the tokens from the identity server: the refresh token in the keychain, a fresh access token for each call.
/// The identity server rotates refresh tokens, so only one refresh runs at a time.
actor AuthService: AccessTokenSource {
    static let redirectURI = URL(string: "com.b-velop.onkel-bummel:/oauth/callback")!
    static let callbackScheme = "com.b-velop.onkel-bummel"
    private static let keychainKey = "tokens"

    private var client: OAuthClient?
    private var tokens: TokenSet?
    private var refreshing: Task<TokenSet, Error>?
    private let onSignInRequired: @Sendable () async -> Void

    init(client: OAuthClient?, onSignInRequired: @escaping @Sendable () async -> Void) {
        self.client = client
        self.onSignInRequired = onSignInRequired
        tokens = Keychain.data(for: Self.keychainKey).flatMap { try? JSONDecoder().decode(TokenSet.self, from: $0) }
    }

    var isSignedIn: Bool { tokens?.refreshToken != nil || tokens.map { !$0.needsRefresh() } == true }

    var claims: [String: String] {
        (tokens?.idTokenClaims ?? [:]).compactMapValues { $0 as? String }
    }

    func configure(_ client: OAuthClient) {
        self.client = client
    }

    func signIn(with authenticator: WebAuthenticator) async throws {
        guard let client else { throw OAuthError.discoveryFailed }
        let pkce = PKCE.random()
        let state = randomState()
        let callback = try await authenticator.authenticate(
            url: client.authorizationURL(pkce: pkce, state: state), callbackScheme: Self.callbackScheme)
        let code = try client.code(from: callback, state: state)
        store(try await client.exchange(code: code, pkce: pkce))
    }

    func signOut() {
        refreshing?.cancel()
        refreshing = nil
        store(nil)
    }

    func accessToken() async throws -> String {
        guard let current = tokens else { throw APIError.notSignedIn }
        if !current.needsRefresh() { return current.accessToken }
        return try await refresh().accessToken
    }

    func tokenRejected(_ token: String) async {
        if tokens?.accessToken == token { tokens?.expiresAt = .distantPast }
    }

    private func refresh() async throws -> TokenSet {
        if let refreshing { return try await refreshing.value }
        guard let client, let current = tokens else { throw APIError.notSignedIn }
        let task = Task { try await client.refresh(current) }
        refreshing = task
        defer { refreshing = nil }
        do {
            let fresh = try await task.value
            store(fresh)
            return fresh
        } catch OAuthError.signInRequired {
            store(nil)
            await onSignInRequired()
            throw OAuthError.signInRequired
        }
    }

    private func store(_ tokens: TokenSet?) {
        self.tokens = tokens
        Keychain.set(tokens.flatMap { try? JSONEncoder().encode($0) }, for: Self.keychainKey)
    }
}

/// Runs the login page of the identity server in an `ASWebAuthenticationSession`.
@MainActor
final class WebAuthenticator: NSObject, ASWebAuthenticationPresentationContextProviding {
    private var session: ASWebAuthenticationSession?

    func authenticate(url: URL, callbackScheme: String) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(url: url, callbackURLScheme: callbackScheme) { callback, error in
                if let callback {
                    continuation.resume(returning: callback)
                } else if let error = error as? ASWebAuthenticationSessionError, error.code == .canceledLogin {
                    continuation.resume(throwing: OAuthError.cancelled)
                } else {
                    continuation.resume(throwing: error ?? OAuthError.cancelled)
                }
            }
            session.presentationContextProvider = self
            self.session = session
            session.start()
        }
    }

    nonisolated func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        MainActor.assumeIsolated {
            UIApplication.shared.connectedScenes.compactMap { ($0 as? UIWindowScene)?.keyWindow }.first ?? ASPresentationAnchor()
        }
    }
}
