import XCTest
@testable import GlosifyMobilePrototype

final class MockDataStoreTests: XCTestCase {
    func testFreshStoreRestoresDeterministicSeed() async throws {
        let first = MockDataStore(configuration: .immediate)
        let second = MockDataStore(configuration: .immediate)
        let firstLibrary = try await first.quizLibrary()
        let secondLibrary = try await second.quizLibrary()
        XCTAssertEqual(firstLibrary.quizzes, secondLibrary.quizzes)
        XCTAssertEqual(firstLibrary.collections, secondLibrary.collections)
        let firstAccount = try await first.currentAccount()
        let secondAccount = try await second.currentAccount()
        XCTAssertEqual(firstAccount, secondAccount)
    }

    func testQuizCollectionCRUDAndMove() async throws {
        let store = MockDataStore(configuration: .immediate)
        let collection = try await store.createCollection(name: "Grammar", languageCode: "pl", parentID: nil)
        let quiz = try await store.createQuiz(name: "Cases", sourceLanguage: "en", targetLanguage: "pl", collectionID: nil)
        try await store.moveQuiz(id: quiz.id, collectionID: collection.id)
        let withWord = try await store.addWord(quizID: quiz.id, source: "house", translation: "dom")
        XCTAssertEqual(withWord.words.last?.translation, "dom")
        let wordID = try XCTUnwrap(withWord.words.last?.id)
        let withoutWord = try await store.deleteWord(quizID: quiz.id, wordID: wordID)
        XCTAssertTrue(withoutWord.words.isEmpty)
        try await store.deleteCollection(id: collection.id)
        let libraryAfterCollectionDelete = try await store.quizLibrary()
        let movedToRoot = libraryAfterCollectionDelete.quizzes.first(where: { $0.id == quiz.id })
        XCTAssertNil(movedToRoot?.collectionID)
        try await store.deleteQuiz(id: quiz.id)
        let libraryAfterDelete = try await store.quizLibrary()
        XCTAssertFalse(libraryAfterDelete.quizzes.contains(where: { $0.id == quiz.id }))
    }

    func testValidationAndInjectedFailure() async throws {
        let store = MockDataStore(configuration: .immediate)
        do {
            _ = try await store.createQuiz(name: " ", sourceLanguage: "en", targetLanguage: "pl", collectionID: nil)
            XCTFail("Expected validation failure")
        } catch let error as PrototypeError {
            XCTAssertEqual(error, .invalidInput("Quiz name is required."))
        }

        let failing = MockDataStore(configuration: MockConfiguration(latency: .zero, failingOperations: [.explore]))
        await XCTAssertThrowsErrorAsync { _ = try await failing.sharedQuizzes() }
    }

    func testEmptyScenarioCoversRepositoryEmptyStates() async throws {
        let store = MockDataStore(configuration: MockConfiguration(latency: .zero, startsEmpty: true))
        let library = try await store.quizLibrary()
        let anki = try await store.ankiCollections()
        let shared = try await store.sharedQuizzes()
        let books = try await store.books()
        let transcripts = try await store.transcripts()
        let chats = try await store.chats()
        XCTAssertTrue(library.quizzes.isEmpty)
        XCTAssertTrue(library.collections.isEmpty)
        XCTAssertTrue(anki.isEmpty)
        XCTAssertTrue(shared.isEmpty)
        XCTAssertTrue(books.isEmpty)
        XCTAssertTrue(transcripts.isEmpty)
        XCTAssertTrue(chats.isEmpty)
    }

    func testEveryRepositoryCanInjectFailure() async {
        for operation in MockOperation.allCases {
            let store = MockDataStore(configuration: MockConfiguration(latency: .zero, failingOperations: [operation]))
            await XCTAssertThrowsErrorAsync {
                switch operation {
                case .authentication: _ = try await store.currentAccount()
                case .quizzes: _ = try await store.quizLibrary()
                case .anki: _ = try await store.ankiCollections()
                case .explore: _ = try await store.sharedQuizzes()
                case .books: _ = try await store.books()
                case .transcripts: _ = try await store.transcripts()
                case .assistant: _ = try await store.chats()
                case .credits: _ = try await store.packages()
                }
            }
        }
    }

    func testJSONImport() async throws {
        let store = MockDataStore(configuration: .immediate)
        let imported = try await store.importQuizJSON(#"{"quizzes":[{"name":"Numbers","words":[{"word":"one","translation":"jeden"}]}]}"#, collectionID: nil)
        XCTAssertEqual(imported.count, 1)
        XCTAssertEqual(imported[0].words.first?.source, "one")
    }

    func testPracticeScoringIsTrimmedAndCaseInsensitive() {
        XCTAssertTrue(PracticeScorer.matches("  DOM ", expected: "dom"))
        XCTAssertFalse(PracticeScorer.matches("domu", expected: "dom"))
    }

    func testAnkiRatingUpdatesIntervalAndReviewCount() async throws {
        let store = MockDataStore(configuration: .immediate)
        let collections = try await store.ankiCollections()
        let collection = try XCTUnwrap(collections.first)
        let card = try XCTUnwrap(collection.cards.first)
        let updated = try await store.rateCard(collectionID: collection.id, cardID: card.id, rating: 4)
        let reviewed = try XCTUnwrap(updated.cards.first(where: { $0.id == card.id }))
        XCTAssertEqual(reviewed.intervalDays, 7)
        XCTAssertEqual(reviewed.reviewCount, card.reviewCount + 1)
    }

    func testExploreCopyCreatesPrivateLibraryQuiz() async throws {
        let store = MockDataStore(configuration: .immediate)
        let sharedItems = try await store.sharedQuizzes()
        let shared = try XCTUnwrap(sharedItems.first)
        let copied = try await store.copySharedQuiz(id: shared.id)
        XCTAssertFalse(copied.isPublic)
        let library = try await store.quizLibrary()
        XCTAssertTrue(library.quizzes.contains(where: { $0.id == copied.id }))
    }

    func testTranscriptRenameAndDelete() async throws {
        let store = MockDataStore(configuration: .immediate)
        let initialTranscripts = try await store.transcripts()
        let transcript = try XCTUnwrap(initialTranscripts.first)
        let renamed = try await store.renameTranscript(id: transcript.id, title: "Renamed")
        XCTAssertEqual(renamed.title, "Renamed")
        try await store.deleteTranscript(id: transcript.id)
        let remainingTranscripts = try await store.transcripts()
        XCTAssertTrue(remainingTranscripts.isEmpty)
    }

    func testBookImportAndDelete() async throws {
        let store = MockDataStore(configuration: .immediate)
        let imported = try await store.importBook(title: "Local book", fileName: "local.pdf", data: Data([1, 2, 3]), pageTexts: ["Page one", "Page two"])
        XCTAssertEqual(imported.pageCount, 2)
        XCTAssertEqual(imported.pages.first?.sourceText, "Page one")
        try await store.deleteBook(id: imported.id)
        let books = try await store.books()
        XCTAssertFalse(books.contains(where: { $0.id == imported.id }))
    }

    func testAssistantApplyCreatesQuizAndFeedbackPersists() async throws {
        let store = MockDataStore(configuration: .immediate)
        let chat = try await store.createChat(context: "Quizzes")
        let response = try await store.sendMessage(chatID: chat.id, text: "Create a quiz", context: "Quizzes")
        let assistantMessage = try XCTUnwrap(response.messages.last)
        let change = try XCTUnwrap(assistantMessage.pendingChange)
        _ = try await store.resolveChange(chatID: chat.id, changeID: change.id, apply: true)
        let library = try await store.quizLibrary()
        XCTAssertTrue(library.quizzes.contains(where: { $0.name == change.quizName }))
        let feedback = try await store.saveFeedback(chatID: chat.id, messageID: assistantMessage.id, rating: 1)
        XCTAssertEqual(feedback.messages.last?.feedback, 1)
    }

    func testCreditPurchaseAndAuthentication() async throws {
        let store = MockDataStore(configuration: .immediate)
        let current = try await store.currentAccount()
        let before = try XCTUnwrap(current)
        let purchased = try await store.purchase(packageID: "starter")
        XCTAssertEqual(purchased.credits, before.credits + 500)
        try await store.signOut()
        let signedOut = try await store.currentAccount()
        XCTAssertNil(signedOut)
        let signedIn = try await store.signIn(email: "test@example.com", password: "secret")
        XCTAssertEqual(signedIn.email, "test@example.com")
    }
}

private func XCTAssertThrowsErrorAsync(
    _ expression: () async throws -> Void,
    file: StaticString = #filePath,
    line: UInt = #line
) async {
    do {
        try await expression()
        XCTFail("Expected an error", file: file, line: line)
    } catch { }
}
