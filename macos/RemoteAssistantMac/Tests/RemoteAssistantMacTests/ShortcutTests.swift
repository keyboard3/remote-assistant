import XCTest
@testable import RemoteAssistantCore

final class ShortcutTests: XCTestCase {
    func testDefaultMacShortcut() throws {
        XCTAssertEqual(try Shortcut.parse("Fn").keys.count, 1)
    }

    func testConfigurableChord() throws {
        XCTAssertEqual(try Shortcut.parse("Command+Shift+R").keys.count, 3)
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
