// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "RemoteAssistantMac",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "RemoteAssistantMac", targets: ["RemoteAssistantMac"])],
    targets: [
        .target(name: "RemoteAssistantCore"),
        .executableTarget(name: "RemoteAssistantMac", dependencies: ["RemoteAssistantCore"]),
        .testTarget(name: "RemoteAssistantMacTests", dependencies: ["RemoteAssistantCore"])
    ]
)
