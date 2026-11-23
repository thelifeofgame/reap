// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "Reap",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "ReapCore"),
        .executableTarget(name: "Reap", dependencies: ["ReapCore"]),
        .testTarget(name: "ReapCoreTests", dependencies: ["ReapCore"]),
    ]
)
