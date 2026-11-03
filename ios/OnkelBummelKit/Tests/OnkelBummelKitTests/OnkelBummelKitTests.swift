import Foundation
import Testing
@testable import OnkelBummelKit

@Suite struct JSONTests {
    @Test func readsDotNetTimesWithSevenFractionalDigits() throws {
        let date = try #require(JSON.parseDate("2026-11-03T08:31:29.1234567Z"))
        #expect(abs(date.timeIntervalSince1970 - 1_793_694_689.123) < 0.001)
        #expect(JSON.parseDate("2026-11-03T08:31:29Z") != nil)
        #expect(JSON.parseDate("2026-11-03T08:31:29.5") != nil)
        #expect(JSON.parseDate("gestern") == nil)
    }

    @Test func decodesAMessageAsTheServerSendsIt() throws {
        let json = """
        {"id":42,"topic":"filomat","title":"Druck fertig","message":"Benchy","priority":4,"tags":["tada"],"click":null,
         "source":"api","createdAt":"2026-11-03T08:31:29.1234567Z","readAt":null,
         "attachments":[{"id":"a1","fileName":"snap.jpg","contentType":"image/jpeg","size":1234,"width":1200,"height":800,
           "url":"/api/v1/attachments/a1","thumbnailUrl":"/api/v1/attachments/a1/thumb"}],
         "actions":[{"index":0,"label":"Nächster","method":"POST","url":"http://x","clear":true,"lastStatus":null,"lastRunAt":null}]}
        """
        let message = try JSON.decoder.decode(Message.self, from: Data(json.utf8))
        #expect(message.id == 42)
        #expect(message.attachments.first?.isImage == true)
        #expect(message.actions.first?.clear == true)
        #expect(message.readAt == nil)
    }
}

@Suite struct OAuthTests {
    @Test func derivesTheS256Challenge() {
        #expect(PKCE(verifier: "dBjftJeZ4CVP-mJ92K9iQ5x9yHGH3MO2XNkFjdCZy8Q").challenge == "YXOlfsIw5biHUZcTWQGLjtVATiJa4IRRIfcxFCns018")
        #expect(PKCE.random().verifier.count == 43)
    }

    @Test func buildsTheAuthorizationRequestAndReadsTheCallback() throws {
        let client = OAuthClient(
            discovery: OIDCDiscovery(issuer: URL(string: "https://identity.example/")!,
                                     authorizationEndpoint: URL(string: "https://identity.example/connect/authorize")!,
                                     tokenEndpoint: URL(string: "https://identity.example/connect/token")!),
            clientId: "onkel-bummel-ios",
            redirectURI: URL(string: "com.b-velop.onkel-bummel:/oauth/callback")!,
            scopes: ["openid", "offline_access", "onkel-bummel-api"])
        let pkce = PKCE.random()
        let url = client.authorizationURL(pkce: pkce, state: "s1")
        let items = Dictionary(uniqueKeysWithValues: URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!.map { ($0.name, $0.value!) })
        #expect(items["code_challenge"] == pkce.challenge)
        #expect(items["code_challenge_method"] == "S256")
        #expect(items["scope"] == "openid offline_access onkel-bummel-api")
        #expect(items["redirect_uri"] == "com.b-velop.onkel-bummel:/oauth/callback")

        #expect(try client.code(from: URL(string: "com.b-velop.onkel-bummel:/oauth/callback?code=abc&state=s1")!, state: "s1") == "abc")
        #expect(throws: OAuthError.stateMismatch) {
            try client.code(from: URL(string: "com.b-velop.onkel-bummel:/oauth/callback?code=abc&state=s2")!, state: "s1")
        }
        #expect(throws: OAuthError.authorizationFailed("access_denied")) {
            try client.code(from: URL(string: "com.b-velop.onkel-bummel:/oauth/callback?error=access_denied&state=s1")!, state: "s1")
        }
    }

    @Test func encodesFormBodies() {
        let body = String(decoding: OAuthClient.formBody(["b": "x y+z", "a": "com.b-velop.onkel-bummel:/oauth/callback"]), as: UTF8.self)
        #expect(body == "a=com.b-velop.onkel-bummel%3A%2Foauth%2Fcallback&b=x%20y%2Bz")
    }

    @Test func refreshesAMinuteEarly() {
        let now = Date()
        #expect(TokenSet(accessToken: "a", refreshToken: nil, idToken: nil, expiresAt: now.addingTimeInterval(30)).needsRefresh(now: now))
        #expect(!TokenSet(accessToken: "a", refreshToken: nil, idToken: nil, expiresAt: now.addingTimeInterval(300)).needsRefresh(now: now))
    }
}

@Suite struct LiveEventTests {
    @Test func splitsTheStreamIntoEventsAtEmptyLines() throws {
        let stream = "event: ping\ndata: {}\n\nevent: read\r\ndata: {\"ids\":[1,2],\"topic\":null,\"upToId\":null,\"readAt\":\"2026-11-03T08:00:00Z\"}\r\n\r\n: comment\n\n"
        var parser = ServerSentEventParser()
        let events = Array(stream.utf8).compactMap { parser.feed(byte: $0) }
        #expect(events.map(\.type) == ["ping", "read"])
        guard case let .read(ids, topic, upToId, _) = try #require(LiveEvent(events[1])) else {
            Issue.record("no read event")
            return
        }
        #expect(ids == [1, 2] && topic == nil && upToId == nil)
        #expect(LiveEvent(events[0]) == .ping)
    }
}

@Suite struct PushContentTests {
    @Test func readsThePayloadAndGroupsCategoriesByLabels() throws {
        let userInfo: [AnyHashable: Any] = [
            "aps": ["alert": ["title": "x"]],
            "ob": ["id": 7, "topic": "kamera", "thumbnail": "https://ob/api/v1/attachments/a/push-thumb?expires=1&signature=s",
                   "actions": [["index": 0, "label": "Licht an"], ["index": 1, "label": "Alarm"]]],
        ]
        let content = try #require(PushContent(userInfo: userInfo))
        #expect(content.messageId == 7)
        #expect(content.actions.map(\.label) == ["Licht an", "Alarm"])
        #expect(content.categoryIdentifier.hasPrefix(NotificationIdentifiers.actionCategoryPrefix))
        var other = content
        other.messageId = 8
        #expect(other.categoryIdentifier == content.categoryIdentifier)
        #expect(PushContent(userInfo: ["ob": ["id": 1, "topic": "t"]])?.categoryIdentifier == "message")
        #expect(PushContent(userInfo: ["aps": [:]]) == nil)
        #expect(NotificationIdentifiers.actionIndex("action-2") == 2)
        #expect(NotificationIdentifiers.actionIndex("read") == nil)
    }

    @Test func takesServerAddressesAsTypedOrScanned() {
        #expect(ServerAddress.parse(" onkel-bummel.tail6105c0.ts.net ")?.absoluteString == "https://onkel-bummel.tail6105c0.ts.net")
        #expect(ServerAddress.parse("https://Onkel-Bummel.tail6105c0.ts.net/inbox?x=1")?.absoluteString == "https://onkel-bummel.tail6105c0.ts.net")
        #expect(ServerAddress.parse("http://localhost:8080")?.absoluteString == "http://localhost:8080")
        #expect(ServerAddress.parse("ftp://x") == nil)
        #expect(ServerAddress.parse("") == nil)
    }

    @Test func knowsTheApnsEnvironmentFromTheProvisioningProfile() {
        let profile = Data("garbage<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Entitlements</key><dict><key>aps-environment</key><string>development</string></dict></dict></plist>garbage".utf8)
        #expect(APNsEnvironment.from(provisioningProfile: profile) == "sandbox")
        #expect(APNsEnvironment.from(provisioningProfile: nil) == "production")
    }

    @Test func putsEmojisInFrontOfTheTitle() {
        #expect(Emoji.title("Druck fertig", tags: ["tada", "drucker"], fallback: "filomat") == "🎉 Druck fertig")
        #expect(Emoji.title(nil, tags: ["drucker"], fallback: "filomat") == "filomat")
        #expect(Emoji.plainTags(["tada", "drucker"]) == ["drucker"])
    }
}
