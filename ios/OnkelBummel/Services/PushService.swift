import OnkelBummelKit
import UIKit
import UserNotifications

/// Registers for pushes and handles what the user does with a notification: „Gelesen“, „Öffnen“ and the
/// message's own actions, which the app runs through the API, in the background if need be.
final class AppDelegate: NSObject, UIApplicationDelegate, UNUserNotificationCenterDelegate {
    func application(_ application: UIApplication, didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        Task { await PushService.registerCategories() }
        application.registerForRemoteNotifications()
        return true
    }

    func application(_ application: UIApplication, didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {
        Task { await AppModel.shared.didRegister(deviceToken: deviceToken) }
    }

    func application(_ application: UIApplication, didFailToRegisterForRemoteNotificationsWithError error: Error) {
        AppModel.shared.sync.lastError = "Keine Pushes: \(error.localizedDescription)"
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification)
        async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard let content = PushContent(userInfo: response.notification.request.content.userInfo) else { return }
        let identifier = response.actionIdentifier
        await PushService.handle(identifier, content: content)
    }
}

@MainActor
enum PushService {
    static func requestAuthorization() async {
        _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .badge, .sound])
    }

    /// The fixed category every push names; the extension adds categories with the actions of a message.
    nonisolated static func registerCategories() async {
        let center = UNUserNotificationCenter.current()
        var categories = await center.notificationCategories().filter { $0.identifier != NotificationIdentifiers.messageCategory }
        categories.insert(messageCategory)
        center.setNotificationCategories(categories)
    }

    nonisolated static var messageCategory: UNNotificationCategory {
        UNNotificationCategory(identifier: NotificationIdentifiers.messageCategory, actions: [readAction, openAction],
                               intentIdentifiers: [], options: [])
    }

    nonisolated static var readAction: UNNotificationAction {
        UNNotificationAction(identifier: NotificationIdentifiers.read, title: "Gelesen", options: [],
                             icon: UNNotificationActionIcon(systemImageName: "checkmark"))
    }

    nonisolated static var openAction: UNNotificationAction {
        UNNotificationAction(identifier: NotificationIdentifiers.open, title: "Öffnen", options: [.foreground],
                             icon: UNNotificationActionIcon(systemImageName: "arrow.up.forward.app"))
    }

    static func handle(_ identifier: String, content: PushContent) async {
        let app = AppModel.shared
        switch identifier {
        case NotificationIdentifiers.read:
            await app.sync.markRead(content.messageId)
        case UNNotificationDefaultActionIdentifier, NotificationIdentifiers.open:
            app.open(messageId: content.messageId)
        case UNNotificationDismissActionIdentifier:
            break
        default:
            if let index = NotificationIdentifiers.actionIndex(identifier) {
                _ = try? await app.sync.runAction(messageId: content.messageId, index: index)
            }
        }
        app.sync.updateBadge()
    }
}
