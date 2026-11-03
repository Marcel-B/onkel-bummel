import Foundation

/// Hands out access tokens for the API; the app refreshes them in the background.
public protocol AccessTokenSource: Sendable {
    func accessToken() async throws -> String
    /// The server turned the token away (401); the next call should bring a fresh one.
    func tokenRejected(_ token: String) async
}

public enum APIError: Error, LocalizedError, Equatable {
    case notSignedIn
    case http(status: Int, detail: String?)
    case invalidResponse

    public var errorDescription: String? {
        switch self {
        case .notSignedIn: "Nicht angemeldet."
        case let .http(status, detail): detail ?? HTTPURLResponse.localizedString(forStatusCode: status)
        case .invalidResponse: "Unerwartete Antwort vom Server."
        }
    }
}

/// Filters of `GET messages`; nil means no filter.
public struct MessageQuery: Sendable, Equatable {
    public var topics: [String]?
    public var search: String?
    public var unreadOnly = false
    public var imagesOnly = false
    public var before: Int64?
    public var after: Int64?
    public var limit = 50

    public init(topics: [String]? = nil, search: String? = nil, unreadOnly: Bool = false, imagesOnly: Bool = false,
                before: Int64? = nil, after: Int64? = nil, limit: Int = 50) {
        self.topics = topics
        self.search = search
        self.unreadOnly = unreadOnly
        self.imagesOnly = imagesOnly
        self.before = before
        self.after = after
        self.limit = limit
    }

    var queryItems: [URLQueryItem] {
        var items: [URLQueryItem] = []
        if let topics, !topics.isEmpty { items.append(.init(name: "topics", value: topics.joined(separator: ","))) }
        if let search, !search.trimmingCharacters(in: .whitespaces).isEmpty { items.append(.init(name: "q", value: search)) }
        if unreadOnly { items.append(.init(name: "unread", value: "1")) }
        if imagesOnly { items.append(.init(name: "has_image", value: "1")) }
        if let before { items.append(.init(name: "before", value: String(before))) }
        if let after { items.append(.init(name: "after", value: String(after))) }
        items.append(.init(name: "limit", value: String(limit)))
        return items
    }
}

public struct RegisterDevice: Encodable, Sendable {
    public var id: String?
    public var name: String
    public var apnsToken: String
    public var environment: String
    public var topics: [String]?

    public init(id: String?, name: String, apnsToken: String, environment: String, topics: [String]? = nil) {
        self.id = id
        self.name = name
        self.apnsToken = apnsToken
        self.environment = environment
        self.topics = topics
    }
}

/// The REST API under `/api/v1`.
public struct APIClient: Sendable {
    public let baseURL: URL
    private let tokens: (any AccessTokenSource)?
    private let session: URLSession

    public init(baseURL: URL, tokens: (any AccessTokenSource)?, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.tokens = tokens
        self.session = session
    }

    // MARK: Setup

    public func config() async throws -> ServerConfig {
        try await send("GET", "config", authorized: false)
    }

    // MARK: Messages

    public func messages(_ query: MessageQuery) async throws -> MessagePage {
        try await send("GET", "messages", query: query.queryItems)
    }

    public func message(_ id: Int64) async throws -> Message {
        try await send("GET", "messages/\(id)")
    }

    public func deleteMessage(_ id: Int64) async throws {
        try await sendEmpty("DELETE", "messages/\(id)")
    }

    public func markRead(_ id: Int64) async throws {
        try await sendEmpty("POST", "messages/\(id)/read")
    }

    /// Marks a topic (or everything) as read, up to the newest message the app has seen.
    @discardableResult
    public func markAllRead(topic: String?, upToId: Int64?) async throws -> MarkReadResult {
        struct Body: Encodable { var topic: String?; var upToId: Int64? }
        return try await send("POST", "messages/read", body: Body(topic: topic, upToId: upToId))
    }

    public func runAction(messageId: Int64, index: Int) async throws -> ActionRunResult {
        try await send("POST", "messages/\(messageId)/actions/\(index)")
    }

    // MARK: Topics

    public func topics() async throws -> [Topic] {
        try await send("GET", "topics")
    }

    public func setMuted(_ muted: Bool, topic: String) async throws -> Topic {
        struct Body: Encodable { var muted: Bool }
        return try await send("PATCH", "topics/\(topic)", body: Body(muted: muted))
    }

    // MARK: Devices

    public func devices() async throws -> [Device] {
        try await send("GET", "devices")
    }

    public func register(_ device: RegisterDevice) async throws -> Device {
        try await send("POST", "devices", body: device)
    }

    public func setSubscriptions(deviceId: String, topics: [String]) async throws -> Device {
        struct Body: Encodable { var topics: [String] }
        return try await send("PUT", "devices/\(deviceId)/subscriptions", body: Body(topics: topics))
    }

    public func deleteDevice(_ id: String) async throws {
        try await sendEmpty("DELETE", "devices/\(id)")
    }

    // MARK: Files and live events

    /// A file under the server, such as an attachment's `url` or `thumbnailUrl`.
    public func data(at path: String) async throws -> Data {
        let (data, _) = try await perform(request(method: "GET", url: resolve(path)), authorized: true)
        return data
    }

    /// The request for `GET stream`; the caller reads the server-sent events.
    public func streamRequest() async throws -> URLRequest {
        var request = request(method: "GET", url: url("stream"))
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 90
        try await authorize(&request)
        return request
    }

    public func resolve(_ path: String) -> URL {
        URL(string: path, relativeTo: baseURL)?.absoluteURL ?? baseURL
    }

    // MARK: Plumbing

    private func url(_ path: String, query: [URLQueryItem] = []) -> URL {
        var components = URLComponents(url: baseURL.appending(path: "api/v1/" + path), resolvingAgainstBaseURL: false)!
        if !query.isEmpty { components.queryItems = query }
        return components.url!
    }

    private func request(method: String, url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }

    private func send<T: Decodable>(_ method: String, _ path: String, query: [URLQueryItem] = [],
                                    body: (any Encodable)? = nil, authorized: Bool = true) async throws -> T {
        var request = request(method: method, url: url(path, query: query))
        if let body {
            request.httpBody = try JSON.encoder.encode(body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, _) = try await perform(request, authorized: authorized)
        return try JSON.decoder.decode(T.self, from: data)
    }

    private func sendEmpty(_ method: String, _ path: String) async throws {
        _ = try await perform(request(method: method, url: url(path)), authorized: true)
    }

    private func perform(_ request: URLRequest, authorized: Bool) async throws -> (Data, HTTPURLResponse) {
        var request = request
        var token: String?
        if authorized { token = try await authorize(&request) }
        var (data, response) = try await exchange(request)
        // A token the server no longer takes (signing key rolled, revoked): one more try with a fresh one.
        if response.statusCode == 401, let token, let tokens {
            await tokens.tokenRejected(token)
            try await authorize(&request)
            (data, response) = try await exchange(request)
        }
        guard (200..<300).contains(response.statusCode) else {
            let problem = try? JSON.decoder.decode(Problem.self, from: data)
            throw APIError.http(status: response.statusCode, detail: problem?.detail ?? problem?.title)
        }
        return (data, response)
    }

    private func exchange(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIError.invalidResponse }
        return (data, http)
    }

    @discardableResult
    private func authorize(_ request: inout URLRequest) async throws -> String {
        guard let tokens else { throw APIError.notSignedIn }
        let token = try await tokens.accessToken()
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        return token
    }
}
