import Foundation

/// What the server puts under `ob` in a push, next to `aps`.
public struct PushContent: Sendable, Equatable {
    public struct Action: Sendable, Equatable {
        public var index: Int
        public var label: String
    }

    public var messageId: Int64
    public var topic: String
    public var click: URL?
    /// Signed link to the thumbnail, absolute or relative to the server.
    public var thumbnail: String?
    public var actions: [Action]

    public init?(userInfo: [AnyHashable: Any]) {
        guard let ob = userInfo["ob"] as? [String: Any],
              let id = (ob["id"] as? NSNumber)?.int64Value,
              let topic = ob["topic"] as? String else { return nil }
        messageId = id
        self.topic = topic
        click = (ob["click"] as? String).flatMap(URL.init(string:))
        thumbnail = ob["thumbnail"] as? String
        actions = (ob["actions"] as? [[String: Any]] ?? []).compactMap { action in
            guard let index = (action["index"] as? NSNumber)?.intValue, let label = action["label"] as? String else { return nil }
            return Action(index: index, label: label)
        }
    }

    /// Category for the action buttons of this push. Messages with the same labels share one, so the list of
    /// categories stays short.
    public var categoryIdentifier: String {
        guard !actions.isEmpty else { return NotificationIdentifiers.messageCategory }
        let labels = actions.map { "\($0.index)=\($0.label)" }.joined(separator: "\u{1F}")
        return NotificationIdentifiers.actionCategoryPrefix + Base64URL.encode(Data(labels.utf8)).prefix(120)
    }
}

/// Identifiers shared by the app (which handles the buttons) and the extension (which sets them up).
public enum NotificationIdentifiers {
    /// Registered by the app: „Gelesen“ and „Öffnen“; the server puts it into every push.
    public static let messageCategory = "message"
    public static let actionCategoryPrefix = "message-actions-"
    public static let read = "read"
    public static let open = "open"
    public static let actionPrefix = "action-"

    public static func action(_ index: Int) -> String { actionPrefix + String(index) }

    /// The index of a message action button, nil for „Gelesen“ and „Öffnen“.
    public static func actionIndex(_ identifier: String) -> Int? {
        identifier.hasPrefix(actionPrefix) ? Int(identifier.dropFirst(actionPrefix.count)) : nil
    }
}

/// Settings the app shares with its Notification Service Extension through the app group.
public enum SharedSettings {
    public static let appGroup = "group.com.b-velop.onkel-bummel"
    private static let serverURLKey = "serverURL"

    public static var defaults: UserDefaults { UserDefaults(suiteName: appGroup) ?? .standard }

    public static var serverURL: URL? {
        get { defaults.string(forKey: serverURLKey).flatMap(URL.init(string:)) }
        set { defaults.set(newValue?.absoluteString, forKey: serverURLKey) }
    }
}

public enum ServerAddress {
    /// Makes a server address out of what was typed or scanned: adds https, drops paths and a trailing slash.
    /// "onkel-bummel.tail6105c0.ts.net" → https://onkel-bummel.tail6105c0.ts.net
    public static func parse(_ input: String) -> URL? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if !text.contains("://") { text = "https://" + text }
        guard var components = URLComponents(string: text), let scheme = components.scheme?.lowercased(),
              scheme == "https" || scheme == "http", let host = components.host, !host.isEmpty else { return nil }
        components.scheme = scheme
        components.host = host.lowercased()
        components.path = ""
        components.query = nil
        components.fragment = nil
        return components.url
    }
}

public enum APNsEnvironment {
    public static let sandbox = "sandbox"
    public static let production = "production"

    /// Development builds carry `aps-environment` = development in their provisioning profile and get their
    /// pushes from the sandbox; TestFlight and App Store builds have no profile left, or one saying production.
    public static func from(provisioningProfile: Data?) -> String {
        guard let profile = provisioningProfile else { return production }
        // The profile is a signed CMS envelope around a plain XML plist; the plist can be read without unwrapping.
        let text = String(decoding: profile, as: UTF8.self)
        guard let start = text.range(of: "<?xml"), let end = text.range(of: "</plist>", range: start.upperBound..<text.endIndex),
              let plist = try? PropertyListSerialization.propertyList(
                  from: Data(text[start.lowerBound..<end.upperBound].utf8), format: nil) as? [String: Any],
              let entitlements = plist["Entitlements"] as? [String: Any],
              let environment = entitlements["aps-environment"] as? String else { return production }
        return environment == "development" ? sandbox : production
    }
}
