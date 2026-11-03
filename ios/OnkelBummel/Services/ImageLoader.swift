import OnkelBummelKit
import SwiftUI
import UIKit

/// Loads pictures from the server with the access token; AsyncImage cannot send one.
@MainActor
final class ImageLoader {
    static let shared = ImageLoader()

    private let cache = NSCache<NSString, UIImage>()

    func cached(_ path: String) -> UIImage? { cache.object(forKey: path as NSString) }

    func image(_ path: String) async throws -> UIImage {
        if let image = cached(path) { return image }
        guard let api = AppModel.shared.api else { throw APIError.notSignedIn }
        let data = try await api.data(at: path)
        guard let image = UIImage(data: data) else { throw APIError.invalidResponse }
        cache.setObject(image, forKey: path as NSString)
        return image
    }

    /// The original file, written to a temporary file under its own name, for sharing.
    func file(for attachment: Attachment) async throws -> URL {
        guard let api = AppModel.shared.api else { throw APIError.notSignedIn }
        let data = try await api.data(at: attachment.url)
        let folder = FileManager.default.temporaryDirectory.appending(path: attachment.id, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appending(path: attachment.fileName.replacingOccurrences(of: "/", with: "_"))
        try data.write(to: url)
        return url
    }
}

/// A picture from the server, with a placeholder while it loads.
struct RemoteImage: View {
    let path: String
    var contentMode = ContentMode.fill

    @State private var image: UIImage?
    @State private var failed = false

    var body: some View {
        ZStack {
            if let image = image ?? ImageLoader.shared.cached(path) {
                Image(uiImage: image)
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
            } else {
                Rectangle()
                    .fill(.quaternary)
                    .overlay {
                        if failed {
                            Image(systemName: "photo.badge.exclamationmark").foregroundStyle(.secondary)
                        } else {
                            ProgressView()
                        }
                    }
            }
        }
        .task(id: path) {
            guard image == nil else { return }
            do {
                image = try await ImageLoader.shared.image(path)
            } catch {
                failed = true
            }
        }
    }
}
