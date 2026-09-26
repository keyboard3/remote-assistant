import XCTest
@testable import RemoteAssistantCore

final class ShortcutTests: XCTestCase {
    func testDefaultMacShortcut() throws {
        XCTAssertEqual(try Shortcut.parse("Fn").keys.count, 1)
    }

    func testConfigurableChord() throws {
        XCTAssertEqual(try Shortcut.parse("Command+Shift+R").keys.count, 3)
    }

    func testBuiltInClipboardShortcuts() throws {
        let screenshot = try Shortcut.parse("Control+Command+Shift+4")
        XCTAssertEqual(screenshot.keys.map(\.code), [59, 55, 56, 21])
        XCTAssertEqual(try Shortcut.parse("Command+C").keys.map(\.code), [55, 8])
        XCTAssertEqual(try Shortcut.parse("Command+V").keys.map(\.code), [55, 9])
    }

    func testRecorderNamesRetainModifierSides() {
        XCTAssertEqual(Shortcut.name(forKeyCode: 55), "LeftCommand")
        XCTAssertEqual(Shortcut.name(forKeyCode: 54), "RightCommand")
        XCTAssertEqual(Shortcut.name(forKeyCode: 21), "4")
        XCTAssertEqual(Shortcut.name(forKeyCode: 8), "C")
        XCTAssertNil(Shortcut.name(forKeyCode: 999))
    }

    func testDoesNotSilentlyTranslateWindowsKey() {
        XCTAssertThrowsError(try Shortcut.parse("RightWin+LeftShift"))
    }

    func testRejectsRepeatedPhysicalKey() {
        XCTAssertThrowsError(try Shortcut.parse("Command+LeftCommand+R"))
    }

    func testRejectsTwoSidesOfSameModifier() {
        XCTAssertThrowsError(try Shortcut.parse("LeftShift+RightShift+R"))
    }

    func testRejectsNonModifierBeforeEnd() {
        XCTAssertThrowsError(try Shortcut.parse("R+Command"))
    }
}
