import CryptoKit
import Foundation

/// The parts of the identity server's discovery document the app needs.
public struct OIDCDiscovery: Codable, Sendable, Equatable {
    public var issuer: URL
    public var authorizationEndpoint: URL
    public var tokenEndpoint: URL
    public var endSessionEndpoint: URL?

    enum CodingKeys: String, CodingKey {
        case issuer
        case authorizationEndpoint = "authorization_endpoint"
        case tokenEndpoint = "token_endpoint"
        case endSessionEndpoint = "end_session_endpoint"
    }

    public static func load(issuer: URL, session: URLSession = .shared) async throws -> OIDCDiscovery {
        let url = issuer.appending(path: ".well-known/openid-configuration")
        let (data, response) = try await session.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw OAuthError.discoveryFailed }
        return try JSONDecoder().decode(OIDCDiscovery.self, from: data)
    }
}

/// Proof Key for Code Exchange (RFC 7636), S256.
public struct PKCE: Sendable {
    public let verifier: String
    public let challenge: String

    public init(verifier: String) {
        self.verifier = verifier
        challenge = Base64URL.encode(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    public static func random() -> PKCE {
        PKCE(verifier: Base64URL.encode(randomBytes(32)))
    }
}

public enum OAuthError: Error, LocalizedError, Equatable {
    case discoveryFailed
    case cancelled
    case stateMismatch
    case authorizationFailed(String)
    /// The refresh token is no longer valid (`invalid_grant`); the user has to sign in again.
    case signInRequired
    case tokenRequestFailed(Int, String?)

    public var errorDescription: String? {
        switch self {
        case .discoveryFailed: "Der Identity-Server antwortet nicht. Ist Tailscale an?"
        case .cancelled: "Anmeldung abgebrochen."
        case .stateMismatch: "Die Antwort des Identity-Servers passt nicht zur Anmeldung."
        case let .authorizationFailed(error): "Anmeldung abgelehnt: \(error)"
        case .signInRequired: "Bitte neu anmelden."
        case let .tokenRequestFailed(status, error): "Token-Anfrage gescheitert (\(status)\(error.map { ", \($0)" } ?? ""))."
        }
    }
}

/// Tokens as they are kept in the keychain.
public struct TokenSet: Codable, Sendable, Equatable {
    public var accessToken: String
    public var refreshToken: String?
    public var idToken: String?
    public var expiresAt: Date

    public init(accessToken: String, refreshToken: String?, idToken: String?, expiresAt: Date) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.idToken = idToken
        self.expiresAt = expiresAt
    }

    /// Expired, or about to; refreshed a minute early so a request never goes out with a dying token.
    public func needsRefresh(now: Date = .now) -> Bool {
        expiresAt.timeIntervalSince(now) < 60
    }

    /// Claims of the ID token, such as `name` or `preferred_username`, without checking the signature: only for display.
    public var idTokenClaims: [String: Any] {
        guard let idToken, case let parts = idToken.split(separator: "."), parts.count == 3,
              let data = Base64URL.decode(String(parts[1])),
              let claims = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        return claims
    }
}

/// Authorization code flow with PKCE for a public client.
public struct OAuthClient: Sendable {
    public let discovery: OIDCDiscovery
    public let clientId: String
    public let redirectURI: URL
    public let scopes: [String]
    private let session: URLSession

    public init(discovery: OIDCDiscovery, clientId: String, redirectURI: URL, scopes: [String], session: URLSession = .shared) {
        self.discovery = discovery
        self.clientId = clientId
        self.redirectURI = redirectURI
        self.scopes = scopes
        self.session = session
    }

    public func authorizationURL(pkce: PKCE, state: String) -> URL {
        var components = URLComponents(url: discovery.authorizationEndpoint, resolvingAgainstBaseURL: false)!
        components.queryItems = (components.queryItems ?? []) + [
            .init(name: "response_type", value: "code"),
            .init(name: "client_id", value: clientId),
            .init(name: "redirect_uri", value: redirectURI.absoluteString),
            .init(name: "scope", value: scopes.joined(separator: " ")),
            .init(name: "state", value: state),
            .init(name: "code_challenge", value: pkce.challenge),
            .init(name: "code_challenge_method", value: "S256"),
        ]
        return components.url!
    }

    /// Takes the code out of the redirect the browser came back with.
    public func code(from callback: URL, state: String) throws -> String {
        let items = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        if let error = value("error") {
            throw OAuthError.authorizationFailed(value("error_description") ?? error)
        }
        guard value("state") == state else { throw OAuthError.stateMismatch }
        guard let code = value("code") else { throw OAuthError.authorizationFailed("kein Code") }
        return code
    }

    public func exchange(code: String, pkce: PKCE, now: Date = .now) async throws -> TokenSet {
        try await tokenRequest([
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": redirectURI.absoluteString,
            "client_id": clientId,
            "code_verifier": pkce.verifier,
        ], previous: nil, now: now)
    }

    /// The identity server rotates refresh tokens: the answer carries a new one, the old one is used up.
    public func refresh(_ tokens: TokenSet, now: Date = .now) async throws -> TokenSet {
        guard let refreshToken = tokens.refreshToken else { throw OAuthError.signInRequired }
        return try await tokenRequest([
            "grant_type": "refresh_token",
            "refresh_token": refreshToken,
            "client_id": clientId,
        ], previous: tokens, now: now)
    }

    public static func formBody(_ fields: [String: String]) -> Data {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return fields.sorted { $0.key < $1.key }
            .map { "\($0.key)=\($0.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")" }
            .joined(separator: "&")
            .data(using: .utf8)!
    }

    private func tokenRequest(_ fields: [String: String], previous: TokenSet?, now: Date) async throws -> TokenSet {
        var request = URLRequest(url: discovery.tokenEndpoint)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = Self.formBody(fields)
        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0

        struct Answer: Decodable {
            var access_token: String?
            var refresh_token: String?
            var id_token: String?
            var expires_in: Double?
            var error: String?
        }
        let answer = try? JSONDecoder().decode(Answer.self, from: data)
        guard status == 200, let accessToken = answer?.access_token else {
            if answer?.error == "invalid_grant" { throw OAuthError.signInRequired }
            throw OAuthError.tokenRequestFailed(status, answer?.error)
        }
        return TokenSet(
            accessToken: accessToken,
            refreshToken: answer?.refresh_token ?? previous?.refreshToken,
            idToken: answer?.id_token ?? previous?.idToken,
            expiresAt: now.addingTimeInterval(answer?.expires_in ?? 3600))
    }
}

public enum Base64URL {
    public static func encode(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    public static func decode(_ text: String) -> Data? {
        var base64 = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        return Data(base64Encoded: base64)
    }
}

func randomBytes(_ count: Int) -> Data {
    var generator = SystemRandomNumberGenerator()
    return Data((0..<count).map { _ in UInt8.random(in: .min ... .max, using: &generator) })
}

public func randomState() -> String { Base64URL.encode(randomBytes(16)) }
