import UIKit

/// The pictures of the demo messages, drawn on the device: a colour gradient with a symbol and the label
/// „Beispielbild“, so nobody takes them for a real photo.
enum DemoImages {
    private struct Look {
        var symbol: String
        var colors: [UIColor]
    }

    private static func look(_ name: String) -> Look? {
        switch name {
        case "print": Look(symbol: "printer.fill", colors: [.systemOrange, .systemPink])
        case "print-failed": Look(symbol: "exclamationmark.triangle.fill", colors: [.systemRed, .systemOrange])
        case "camera": Look(symbol: "car.fill", colors: [.systemBlue, .systemIndigo])
        default: nil
        }
    }

    static func jpeg(_ name: String) -> Data? {
        guard let look = look(name) else { return nil }
        let size = CGSize(width: 1200, height: 800)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        let image = UIGraphicsImageRenderer(size: size, format: format).image { context in
            let colors = look.colors.map(\.cgColor) as CFArray
            if let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors, locations: [0, 1]) {
                context.cgContext.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: size.width, y: size.height), options: [])
            }
            let configuration = UIImage.SymbolConfiguration(pointSize: 260, weight: .semibold)
            if let symbol = UIImage(systemName: look.symbol, withConfiguration: configuration)?
                .withTintColor(.white.withAlphaComponent(0.9), renderingMode: .alwaysOriginal) {
                let origin = CGPoint(x: (size.width - symbol.size.width) / 2, y: (size.height - symbol.size.height) / 2 - 40)
                symbol.draw(at: origin)
            }
            let label = NSAttributedString(string: "Beispielbild", attributes: [
                .font: UIFont.systemFont(ofSize: 44, weight: .medium),
                .foregroundColor: UIColor.white.withAlphaComponent(0.85),
            ])
            let labelSize = label.size()
            label.draw(at: CGPoint(x: (size.width - labelSize.width) / 2, y: size.height - labelSize.height - 70))
        }
        return image.jpegData(compressionQuality: 0.8)
    }
}
