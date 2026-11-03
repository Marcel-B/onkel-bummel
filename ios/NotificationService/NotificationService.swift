import OnkelBummelKit
import UserNotifications

/// Runs before a push is shown: attaches the thumbnail through the signed link and sets up buttons for the
/// message's actions. It never signs in; whatever does not work out in time, the push shows as text.
final class NotificationService: UNNotificationServiceExtension, @unchecked Sendable {
    /// Apple gives the extension about 30 seconds; a picture that takes longer than this is left out.
    private static let imageTimeout: TimeInterval = 5

    private var contentHandler: ((UNNotificationContent) -> Void)?
    private var content: UNMutableNotificationContent?

    override func didReceive(_ request: UNNotificationRequest, withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void) {
        self.contentHandler = contentHandler
        guard let mutable = request.content.mutableCopy() as? UNMutableNotificationContent,
              let push = PushContent(userInfo: request.content.userInfo) else {
            contentHandler(request.content)
            return
        }
        // Only this task touches the content until deliver() hands it over, once.
        nonisolated(unsafe) let content = mutable
        self.content = content

        Task {
            if !push.actions.isEmpty {
                await Self.registerCategory(for: push)
                content.categoryIdentifier = push.categoryIdentifier
            }
            if let thumbnail = push.thumbnail, let attachment = await Self.attachment(thumbnail, messageId: push.messageId) {
                content.attachments = [attachment]
            }
            self.deliver()
        }
    }

    override func serviceExtensionTimeWillExpire() {
        deliver()
    }

    private func deliver() {
        guard let contentHandler, let content else { return }
        self.contentHandler = nil
        contentHandler(content)
    }

    private static func attachment(_ link: String, messageId: Int64) async -> UNNotificationAttachment? {
        guard let url = URL(string: link, relativeTo: SharedSettings.serverURL)?.absoluteURL else { return nil }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = imageTimeout
        configuration.timeoutIntervalForResource = imageTimeout
        do {
            let (data, response) = try await URLSession(configuration: configuration).data(from: url)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
            let file = FileManager.default.temporaryDirectory.appending(path: "ob-\(messageId)-\(UUID().uuidString).jpg")
            try data.write(to: file)
            return try UNNotificationAttachment(identifier: "thumbnail", url: file,
                                                options: [UNNotificationAttachmentOptionsTypeHintKey: "public.jpeg"])
        } catch {
            return nil
        }
    }

    /// Adds a category with „Gelesen“, the message's actions and „Öffnen“; categories with the same labels are shared.
    private static func registerCategory(for push: PushContent) async {
        let center = UNUserNotificationCenter.current()
        let identifier = push.categoryIdentifier
        var categories = await center.notificationCategories()
        guard !categories.contains(where: { $0.identifier == identifier }) else { return }

        var actions = [UNNotificationAction(identifier: NotificationIdentifiers.read, title: "Gelesen", options: [],
                                            icon: UNNotificationActionIcon(systemImageName: "checkmark"))]
        actions += push.actions.map {
            UNNotificationAction(identifier: NotificationIdentifiers.action($0.index), title: $0.label, options: [])
        }
        actions.append(UNNotificationAction(identifier: NotificationIdentifiers.open, title: "Öffnen", options: [.foreground],
                                            icon: UNNotificationActionIcon(systemImageName: "arrow.up.forward.app")))
        // Keeps the list from growing without end: the newest 30 action categories stay.
        let dynamic = categories.filter { $0.identifier.hasPrefix(NotificationIdentifiers.actionCategoryPrefix) }
        if dynamic.count >= 30 { categories.subtract(dynamic.prefix(dynamic.count - 29)) }
        categories.insert(UNNotificationCategory(identifier: identifier, actions: actions, intentIdentifiers: [], options: []))
        center.setNotificationCategories(categories)
        // The new category has to be known before the push is shown; setNotificationCategories applies it later.
        for _ in 0..<10 where !(await center.notificationCategories()).contains(where: { $0.identifier == identifier }) {
            try? await Task.sleep(for: .milliseconds(50))
        }
    }
}
