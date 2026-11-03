import Foundation

/// Tags with a known short name show as an emoji in front of the title, the others as tags. Same list as the
/// server's `Emojis.cs`, which puts them into push titles.
public enum Emoji {
    public static let byShortName: [String: String] = [
        "+1": "👍",
        "-1": "👎",
        "thumbsup": "👍",
        "thumbsdown": "👎",
        "tada": "🎉",
        "partying_face": "🥳",
        "white_check_mark": "✅",
        "heavy_check_mark": "✔️",
        "x": "❌",
        "warning": "⚠️",
        "rotating_light": "🚨",
        "no_entry": "⛔",
        "fire": "🔥",
        "boom": "💥",
        "zap": "⚡",
        "bell": "🔔",
        "loudspeaker": "📢",
        "information_source": "ℹ️",
        "question": "❓",
        "exclamation": "❗",
        "bulb": "💡",
        "skull": "💀",
        "bug": "🐛",
        "wrench": "🔧",
        "hammer": "🔨",
        "gear": "⚙️",
        "package": "📦",
        "rocket": "🚀",
        "computer": "💻",
        "floppy_disk": "💾",
        "printer": "🖨️",
        "camera": "📷",
        "movie_camera": "🎥",
        "house": "🏠",
        "door": "🚪",
        "key": "🔑",
        "lock": "🔒",
        "unlock": "🔓",
        "car": "🚗",
        "boat": "⛵",
        "sailboat": "⛵",
        "anchor": "⚓",
        "droplet": "💧",
        "sunny": "☀️",
        "cloud": "☁️",
        "umbrella": "☔",
        "snowflake": "❄️",
        "thermometer": "🌡️",
        "battery": "🔋",
        "electric_plug": "🔌",
        "chart_with_upwards_trend": "📈",
        "chart_with_downwards_trend": "📉",
        "calendar": "📅",
        "alarm_clock": "⏰",
        "hourglass": "⌛",
        "mailbox": "📫",
        "email": "📧",
        "envelope": "✉️",
        "moneybag": "💰",
        "shopping_cart": "🛒",
        "heart": "❤️",
        "star": "⭐",
        "eyes": "👀",
        "wave": "👋",
        "smile": "😄",
        "cry": "😢",
        "red_circle": "🔴",
        "orange_circle": "🟠",
        "yellow_circle": "🟡",
        "green_circle": "🟢",
        "blue_circle": "🔵",
        "white_circle": "⚪",
        "black_circle": "⚫",
    ]

    public static func emojis(in tags: [String]) -> String {
        tags.compactMap { byShortName[$0.lowercased()] }.joined()
    }

    /// The tags that are no emoji, to show as tags.
    public static func plainTags(_ tags: [String]) -> [String] {
        tags.filter { byShortName[$0.lowercased()] == nil }
    }

    /// "🎉 Druck fertig", or the fallback (usually the topic) when the message has no title.
    public static func title(_ title: String?, tags: [String], fallback: String) -> String {
        let emojis = emojis(in: tags)
        let text = title ?? fallback
        return emojis.isEmpty ? text : "\(emojis) \(text)"
    }
}
