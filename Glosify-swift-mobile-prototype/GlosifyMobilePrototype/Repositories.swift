import Foundation

enum MockOperation: String, Codable, Sendable, Hashable, CaseIterable {
    case authentication, quizzes, anki, explore, books, transcripts, assistant, credits
}

enum PrototypeError: LocalizedError, Equatable, Sendable {
    case invalidInput(String)
    case notFound(String)
    case injectedFailure(MockOperation)

    var errorDescription: String? {
        switch self {
        case .invalidInput(let message), .notFound(let message): message
        case .injectedFailure(let operation): "The mocked \(operation.rawValue) service is unavailable."
        }
    }
}

struct MockConfiguration: Sendable {
    var latency: Duration = .milliseconds(220)
    var failingOperations: Set<MockOperation> = []
    var startsEmpty = false

    static let immediate = MockConfiguration(latency: .zero)
}

protocol AuthenticationRepository: Sendable {
    func currentAccount() async throws -> Account?
    func signIn(email: String, password: String) async throws -> Account
    func register(name: String, email: String, password: String) async throws -> Account
    func signOut() async throws
    func requestPasswordReset(email: String) async throws
    func selectLanguage(code: String) async throws -> Account
}

protocol QuizRepository: Sendable {
    func quizLibrary() async throws -> (collections: [QuizCollection], quizzes: [Quiz])
    func createQuiz(name: String, sourceLanguage: String, targetLanguage: String, collectionID: UUID?) async throws -> Quiz
    func createCollection(name: String, languageCode: String, parentID: UUID?) async throws -> QuizCollection
    func updateCollection(_ collection: QuizCollection) async throws -> QuizCollection
    func deleteCollection(id: UUID) async throws
    func updateQuiz(_ quiz: Quiz) async throws -> Quiz
    func deleteQuiz(id: UUID) async throws
    func moveQuiz(id: UUID, collectionID: UUID?) async throws
    func addWord(quizID: UUID, source: String, translation: String) async throws -> Quiz
    func addSentence(quizID: UUID, source: String, translation: String) async throws -> Quiz
    func deleteWord(quizID: UUID, wordID: UUID) async throws -> Quiz
    func deleteSentence(quizID: UUID, sentenceID: UUID) async throws -> Quiz
    func importQuizJSON(_ json: String, collectionID: UUID?) async throws -> [Quiz]
}

protocol AnkiRepository: Sendable {
    func ankiCollections() async throws -> [AnkiCollection]
    func createAnkiCollection(name: String, sourceLanguage: String, targetLanguage: String) async throws -> AnkiCollection
    func addQuiz(_ quizID: UUID, to collectionID: UUID) async throws -> AnkiCollection
    func rateCard(collectionID: UUID, cardID: UUID, rating: Int) async throws -> AnkiCollection
}

protocol ExploreRepository: Sendable {
    func sharedQuizzes() async throws -> [SharedQuiz]
    func copySharedQuiz(id: UUID) async throws -> Quiz
}

protocol BookRepository: Sendable {
    func books() async throws -> [BookDocument]
    func importBook(title: String, fileName: String, data: Data, pageTexts: [String]) async throws -> BookDocument
    func deleteBook(id: UUID) async throws
}

protocol TranscriptRepository: Sendable {
    func transcripts() async throws -> [Transcript]
    func renameTranscript(id: UUID, title: String) async throws -> Transcript
    func deleteTranscript(id: UUID) async throws
}

protocol AssistantRepository: Sendable {
    func chats() async throws -> [AssistantChat]
    func createChat(context: String) async throws -> AssistantChat
    func sendMessage(chatID: UUID, text: String, context: String) async throws -> AssistantChat
    func resolveChange(chatID: UUID, changeID: UUID, apply: Bool) async throws -> AssistantChat
    func saveFeedback(chatID: UUID, messageID: UUID, rating: Int) async throws -> AssistantChat
    func deleteChat(id: UUID) async throws
}

protocol CreditRepository: Sendable {
    func packages() async throws -> [CreditPackage]
    func purchase(packageID: String) async throws -> Account
}

@MainActor
protocol SpeechProviding: AnyObject {
    func speak(_ text: String, locale: String)
    func stop()
}

protocol PDFProviding: Sendable {
    func pageTexts(from data: Data) throws -> [String]
}

struct AppEnvironment: Sendable {
    let authentication: any AuthenticationRepository
    let quizzes: any QuizRepository
    let anki: any AnkiRepository
    let explore: any ExploreRepository
    let books: any BookRepository
    let transcripts: any TranscriptRepository
    let assistant: any AssistantRepository
    let credits: any CreditRepository
    let pdf: any PDFProviding

    static func prototype(configuration: MockConfiguration = .init()) -> AppEnvironment {
        let store = MockDataStore(configuration: configuration)
        return AppEnvironment(
            authentication: store,
            quizzes: store,
            anki: store,
            explore: store,
            books: store,
            transcripts: store,
            assistant: store,
            credits: store,
            pdf: PDFService()
        )
    }
}
