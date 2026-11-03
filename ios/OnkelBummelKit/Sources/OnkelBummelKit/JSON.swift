import Foundation

public enum JSON {
    /// The server writes UTC times with up to seven fractional digits (.NET), which ISO8601DateFormatter does not take.
    public static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            guard let date = parseDate(text) else {
                throw DecodingError.dataCorruptedError(in: container, debugDescription: "No ISO 8601 date: \(text)")
            }
            return date
        }
        return decoder
    }()

    public static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }()

    public static func parseDate(_ text: String) -> Date? {
        var text = text
        // Cut the fraction to milliseconds; more precision is of no use here.
        if let dot = text.firstIndex(of: ".") {
            let fractionEnd = text[dot...].dropFirst().firstIndex { !$0.isNumber } ?? text.endIndex
            let digits = text[text.index(after: dot)..<fractionEnd]
            let millis = String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
            text = String(text[..<dot]) + "." + millis + String(text[fractionEnd...])
        }
        if !text.hasSuffix("Z") && !text.contains("+") && text.filter({ $0 == "-" }).count < 3 {
            text += "Z"
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = text.contains(".") ? [.withInternetDateTime, .withFractionalSeconds] : [.withInternetDateTime]
        return formatter.date(from: text)
    }
}
