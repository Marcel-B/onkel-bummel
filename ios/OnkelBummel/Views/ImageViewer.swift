import OnkelBummelKit
import Photos
import SwiftUI
import UIKit

/// Full screen pictures of a message: swipe between them, pinch or double-tap to zoom, share or save the original.
struct ImageViewer: View {
    let images: [Attachment]
    @State var selection: Int

    @Environment(\.dismiss) private var dismiss
    @State private var sharing: URL?
    @State private var status: String?

    var body: some View {
        NavigationStack {
            TabView(selection: $selection) {
                ForEach(Array(images.enumerated()), id: \.element.id) { index, image in
                    FullImage(attachment: image).tag(index)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: images.count > 1 ? .automatic : .never))
            .background(.black)
            .ignoresSafeArea(edges: .bottom)
            .toolbarBackground(.visible, for: .navigationBar, .bottomBar)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Fertig") { dismiss() }
                }
                ToolbarItemGroup(placement: .bottomBar) {
                    Button("Teilen", systemImage: "square.and.arrow.up") {
                        Task { sharing = try? await ImageLoader.shared.file(for: images[selection]) }
                    }
                    Spacer()
                    if let status { Text(status).font(.caption) }
                    Spacer()
                    Button("Sichern", systemImage: "square.and.arrow.down") {
                        Task { await save(images[selection]) }
                    }
                }
            }
            .sheet(item: Binding(get: { sharing.map(SharedFile.init) }, set: { sharing = $0?.url })) { file in
                ShareSheet(items: [file.url])
            }
        }
    }

    /// Saves the original as it is (HEIC stays HEIC), asking only for permission to add.
    private func save(_ attachment: Attachment) async {
        guard await PHPhotoLibrary.requestAuthorization(for: .addOnly) == .authorized else {
            status = "Kein Zugriff auf Fotos"
            return
        }
        do {
            let file = try await ImageLoader.shared.file(for: attachment)
            try await PHPhotoLibrary.shared().performChanges {
                PHAssetCreationRequest.forAsset().addResource(with: .photo, fileURL: file, options: nil)
            }
            status = "Gesichert"
        } catch {
            status = "Nicht gesichert"
        }
    }
}

private struct SharedFile: Identifiable {
    let url: URL
    var id: URL { url }
}

/// The original picture, shown on top of its thumbnail while it loads.
private struct FullImage: View {
    let attachment: Attachment
    @State private var image: UIImage?

    var body: some View {
        Group {
            if let image {
                ZoomableImage(image: image)
            } else if let thumbnail = attachment.thumbnailUrl {
                RemoteImage(path: thumbnail, contentMode: .fit).overlay { ProgressView() }
            }
        }
        .task { image = try? await ImageLoader.shared.image(attachment.url) }
    }
}

/// UIScrollView zooms more naturally than SwiftUI gestures: pinch, pan, double tap.
private struct ZoomableImage: UIViewRepresentable {
    let image: UIImage

    func makeUIView(context: Context) -> UIScrollView {
        let scroll = UIScrollView()
        scroll.delegate = context.coordinator
        scroll.maximumZoomScale = 6
        scroll.showsHorizontalScrollIndicator = false
        scroll.showsVerticalScrollIndicator = false
        let view = UIImageView(image: image)
        view.contentMode = .scaleAspectFit
        view.translatesAutoresizingMaskIntoConstraints = false
        scroll.addSubview(view)
        NSLayoutConstraint.activate([
            view.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor),
            view.heightAnchor.constraint(equalTo: scroll.frameLayoutGuide.heightAnchor),
            view.leadingAnchor.constraint(equalTo: scroll.contentLayoutGuide.leadingAnchor),
            view.trailingAnchor.constraint(equalTo: scroll.contentLayoutGuide.trailingAnchor),
            view.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor),
            view.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor),
        ])
        let doubleTap = UITapGestureRecognizer(target: context.coordinator, action: #selector(Coordinator.doubleTapped(_:)))
        doubleTap.numberOfTapsRequired = 2
        scroll.addGestureRecognizer(doubleTap)
        context.coordinator.imageView = view
        return scroll
    }

    func updateUIView(_ scroll: UIScrollView, context: Context) {
        context.coordinator.imageView?.image = image
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class Coordinator: NSObject, UIScrollViewDelegate {
        weak var imageView: UIImageView?

        func viewForZooming(in scrollView: UIScrollView) -> UIView? { imageView }

        @objc func doubleTapped(_ gesture: UITapGestureRecognizer) {
            guard let scroll = gesture.view as? UIScrollView else { return }
            if scroll.zoomScale > 1 {
                scroll.setZoomScale(1, animated: true)
            } else {
                let point = gesture.location(in: imageView)
                let size = CGSize(width: scroll.bounds.width / 3, height: scroll.bounds.height / 3)
                scroll.zoom(to: CGRect(origin: CGPoint(x: point.x - size.width / 2, y: point.y - size.height / 2), size: size), animated: true)
            }
        }
    }
}
