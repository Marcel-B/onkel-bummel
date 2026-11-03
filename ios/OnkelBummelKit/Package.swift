// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "OnkelBummelKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "OnkelBummelKit", targets: ["OnkelBummelKit"]),
    ],
    targets: [
        .target(name: "OnkelBummelKit"),
        .testTarget(name: "OnkelBummelKitTests", dependencies: ["OnkelBummelKit"]),
    ]
)
