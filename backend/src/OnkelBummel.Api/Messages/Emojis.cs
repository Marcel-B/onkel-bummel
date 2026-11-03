namespace OnkelBummel.Api.Messages;

/// <summary>
/// Tags with a known short name show as an emoji in front of the title, the others as tags. The clients keep the
/// same list (the iOS app in <c>Emoji.swift</c>, the web interface in <c>web/src/lib/emojis.ts</c>).
/// </summary>
public static class Emojis
{
    public static readonly IReadOnlyDictionary<string, string> ByShortName = new Dictionary<string, string>
    {
        ["+1"] = "👍",
        ["-1"] = "👎",
        ["thumbsup"] = "👍",
        ["thumbsdown"] = "👎",
        ["tada"] = "🎉",
        ["partying_face"] = "🥳",
        ["white_check_mark"] = "✅",
        ["heavy_check_mark"] = "✔️",
        ["x"] = "❌",
        ["warning"] = "⚠️",
        ["rotating_light"] = "🚨",
        ["no_entry"] = "⛔",
        ["fire"] = "🔥",
        ["boom"] = "💥",
        ["zap"] = "⚡",
        ["bell"] = "🔔",
        ["loudspeaker"] = "📢",
        ["information_source"] = "ℹ️",
        ["question"] = "❓",
        ["exclamation"] = "❗",
        ["bulb"] = "💡",
        ["skull"] = "💀",
        ["bug"] = "🐛",
        ["wrench"] = "🔧",
        ["hammer"] = "🔨",
        ["gear"] = "⚙️",
        ["package"] = "📦",
        ["rocket"] = "🚀",
        ["computer"] = "💻",
        ["floppy_disk"] = "💾",
        ["printer"] = "🖨️",
        ["camera"] = "📷",
        ["movie_camera"] = "🎥",
        ["house"] = "🏠",
        ["door"] = "🚪",
        ["key"] = "🔑",
        ["lock"] = "🔒",
        ["unlock"] = "🔓",
        ["car"] = "🚗",
        ["boat"] = "⛵",
        ["sailboat"] = "⛵",
        ["anchor"] = "⚓",
        ["droplet"] = "💧",
        ["sunny"] = "☀️",
        ["cloud"] = "☁️",
        ["umbrella"] = "☔",
        ["snowflake"] = "❄️",
        ["thermometer"] = "🌡️",
        ["battery"] = "🔋",
        ["electric_plug"] = "🔌",
        ["chart_with_upwards_trend"] = "📈",
        ["chart_with_downwards_trend"] = "📉",
        ["calendar"] = "📅",
        ["alarm_clock"] = "⏰",
        ["hourglass"] = "⌛",
        ["mailbox"] = "📫",
        ["email"] = "📧",
        ["envelope"] = "✉️",
        ["moneybag"] = "💰",
        ["shopping_cart"] = "🛒",
        ["heart"] = "❤️",
        ["star"] = "⭐",
        ["eyes"] = "👀",
        ["wave"] = "👋",
        ["smile"] = "😄",
        ["cry"] = "😢",
        ["red_circle"] = "🔴",
        ["orange_circle"] = "🟠",
        ["yellow_circle"] = "🟡",
        ["green_circle"] = "🟢",
        ["blue_circle"] = "🔵",
        ["white_circle"] = "⚪",
        ["black_circle"] = "⚫",
    };

    /// <summary>The emojis of the tags, in their order, followed by a space; empty when no tag is one.</summary>
    public static string Prefix(IEnumerable<string> tags)
    {
        var emojis = string.Concat(tags.Select(t => ByShortName.GetValueOrDefault(t.ToLowerInvariant())).OfType<string>());
        return emojis.Length == 0 ? "" : emojis + " ";
    }
}
