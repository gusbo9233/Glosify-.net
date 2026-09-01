import XCTest

final class GlosifyMobilePrototypeUITests: XCTestCase {
    override func setUpWithError() throws {
        continueAfterFailure = false
    }

    func testSeededLearnerJourney() throws {
        let app = XCUIApplication()
        app.launch()
        XCTAssertTrue(app.otherElements["home-screen"].waitForExistence(timeout: 5))
        app.tabBars.buttons["Quizzes"].tap()
        XCTAssertTrue(app.otherElements["quiz-library-screen"].waitForExistence(timeout: 2))
        app.tabBars.buttons["Anki"].tap()
        XCTAssertTrue(app.otherElements["anki-library-screen"].waitForExistence(timeout: 2))
        app.tabBars.buttons["Explore"].tap()
        XCTAssertTrue(app.otherElements["explore-screen"].waitForExistence(timeout: 2))
        app.buttons["Open study assistant"].tap()
        XCTAssertTrue(app.navigationBars["Study assistant"].waitForExistence(timeout: 2))
    }

    func testSignOutAndMockSignIn() throws {
        let app = XCUIApplication()
        app.launch()
        app.buttons["Open account"].tap()
        app.buttons["Sign out"].tap()
        XCTAssertTrue(app.buttons["auth-submit"].waitForExistence(timeout: 3))
        app.buttons["auth-submit"].tap()
        XCTAssertTrue(app.otherElements["home-screen"].waitForExistence(timeout: 5))
    }

    func testQuizPracticeAndAnkiReview() throws {
        let app = XCUIApplication()
        app.launch()
        app.tabBars.buttons["Quizzes"].tap()
        app.staticTexts["Common Polish Verbs"].tap()
        app.buttons["Start practice"].tap()
        app.buttons["Start session"].tap()
        XCTAssertTrue(app.buttons["Show answer"].waitForExistence(timeout: 2))
        app.buttons["Close"].tap()

        app.tabBars.buttons["Anki"].tap()
        app.staticTexts["Everyday Polish"].tap()
        app.buttons["Study due cards"].tap()
        XCTAssertTrue(app.buttons["anki-reveal"].waitForExistence(timeout: 2))
    }

    func testExploreCopyAndCreditPurchase() throws {
        let app = XCUIApplication()
        app.launch()
        app.tabBars.buttons["Explore"].tap()
        app.staticTexts["Polish café essentials"].tap()
        app.buttons["copy-shared-quiz"].tap()
        XCTAssertTrue(app.buttons["Copied to your library"].waitForExistence(timeout: 2))

        app.buttons["Open account"].tap()
        app.buttons["Credits and packages"].tap()
        app.buttons["purchase-starter"].tap()
        XCTAssertTrue(app.alerts["Mock purchase complete"].waitForExistence(timeout: 2))
    }

    func testLibraryBooksAndTranscripts() throws {
        let app = XCUIApplication()
        app.launch()
        app.tabBars.buttons["Library"].tap()
        app.staticTexts["A practical introduction to Polish"].tap()
        XCTAssertTrue(app.buttons["Read aloud"].waitForExistence(timeout: 2))
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.segmentedControls.buttons["Transcripts"].tap()
        app.staticTexts["Polish travel podcast"].tap()
        XCTAssertTrue(app.staticTexts["Welcome to today's programme."].waitForExistence(timeout: 2))
    }
}
