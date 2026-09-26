import XCTest
@testable import RemoteAssistantCore

final class ActionSettingsTests: XCTestCase {
    private func temporaryStore() throws -> (URL, ActionSettingsStore) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return (directory, ActionSettingsStore(fileURL: directory.appendingPathComponent("settings.json")))
    }

    func testCreatesFourDefaults() throws {
        let (_, store) = try temporaryStore()
        let loaded = store.loadOrCreate()
        XCTAssertNil(loaded.error)
        XCTAssertEqual(loaded.document.actions.map(\.shortcut), ["Control+Command+Shift+4", "Command+C", "Command+V", "Fn"])
        XCTAssertEqual(loaded.document.schemaVersion, 4)
        let saved = try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: store.fileURL)) as? [String: Any])
        XCTAssertEqual((saved["actions"] as? [[String: Any]])?.count, 4)
    }

    func testMigratesAndBacksUpLegacyTypelessValue() throws {
        let (directory, store) = try temporaryStore()
        let source = "{\"actionTitle\":\"Dictation\",\"actionHotkey\":\"LeftOption\",\"typelessHotkey\":\"RightOption\"}"
        try Data(source.utf8).write(to: store.fileURL)
        let loaded = store.loadOrCreate()
        XCTAssertNil(loaded.error)
        XCTAssertNotNil(loaded.backupURL)
        XCTAssertTrue(FileManager.default.fileExists(atPath: loaded.backupURL!.path))
        XCTAssertEqual(loaded.document.actions[3].title, "Dictation")
        XCTAssertEqual(loaded.document.actions[3].shortcut, "LeftOption")
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: directory.path).filter { $0.hasSuffix(".bak") }.count, 1)
    }

    func testSavePreservesOrderAndDeletion() throws {
        let (_, store) = try temporaryStore()
        var loaded = store.loadOrCreate().document
        let removedID = loaded.actions[0].id
        loaded.actions.reverse()
        loaded.actions.removeAll { $0.id == removedID }
        let saved = try store.save(loaded)
        XCTAssertEqual(saved.actions.map(\.id), loaded.actions.map(\.id))
        XCTAssertFalse(store.loadOrCreate().document.actions.contains { $0.id == removedID })
    }

    func testPreservesUnknownTopLevelField() throws {
        let (_, store) = try temporaryStore()
        _ = store.loadOrCreate()
        var object = try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: store.fileURL)) as? [String: Any])
        object["futureField"] = ["enabled": true]
        try JSONSerialization.data(withJSONObject: object).write(to: store.fileURL)
        _ = try store.save(ActionSettingsDocument())
        let saved = try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: store.fileURL)) as? [String: Any])
        XCTAssertNotNil(saved["futureField"])
    }

    func testRefusesToOverwriteFutureActionSchema() throws {
        let (_, store) = try temporaryStore()
        let source = Data("{\"schemaVersion\":5,\"actions\":[{\"id\":\"future\"}]}".utf8)
        try source.write(to: store.fileURL)
        XCTAssertNotNil(store.loadOrCreate().error)
        XCTAssertThrowsError(try store.save(ActionSettingsDocument()))
        XCTAssertEqual(try Data(contentsOf: store.fileURL), source)
    }
}
