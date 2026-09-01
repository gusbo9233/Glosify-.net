import Foundation
import Observation

@MainActor
@Observable
final class AppModel {
    let environment: AppEnvironment
    let speech: any SpeechProviding

    var account: Account?
    var collections: [QuizCollection] = []
    var quizzes: [Quiz] = []
    var ankiCollections: [AnkiCollection] = []
    var sharedQuizzes: [SharedQuiz] = []
    var books: [BookDocument] = []
    var transcripts: [Transcript] = []
    var chats: [AssistantChat] = []
    var packages: [CreditPackage] = []
    var isLoading = false
    var errorMessage: String?
    var notice: String?
    var assistantContext = "Home"

    init(environment: AppEnvironment = .prototype(), speech: any SpeechProviding = SpeechService()) {
        self.environment = environment
        self.speech = speech
        account = Account(id: SeedID.uuid(1), email: "learner@glosify.se", displayName: "Demo Learner", credits: 1_564, selectedLanguageCode: "pl")
    }

    var selectedLanguage: LanguageOption {
        LanguageCatalog.find(account?.selectedLanguageCode ?? "pl")
    }

    func loadAll() async {
        guard !isLoading else { return }
        isLoading = true
        errorMessage = nil
        defer { isLoading = false }
        do {
            let currentAccount = try await environment.authentication.currentAccount()
            account = currentAccount
            async let library = environment.quizzes.quizLibrary()
            async let anki = environment.anki.ankiCollections()
            async let explore = environment.explore.sharedQuizzes(languageCode: currentAccount?.selectedLanguageCode ?? "pl")
            async let books = environment.books.books()
            async let transcripts = environment.transcripts.transcripts()
            async let chats = environment.assistant.chats()
            async let packages = environment.credits.packages()
            let loadedLibrary = try await library
            collections = loadedLibrary.collections
            quizzes = loadedLibrary.quizzes
            ankiCollections = try await anki
            sharedQuizzes = try await explore
            self.books = try await books
            self.transcripts = try await transcripts
            self.chats = try await chats
            self.packages = try await packages
        } catch { show(error) }
    }

    func refreshLibrary() async {
        do {
            let value = try await environment.quizzes.quizLibrary()
            collections = value.collections
            quizzes = value.quizzes
        } catch { show(error) }
    }

    func refreshExplore() async {
        do {
            sharedQuizzes = try await environment.explore.sharedQuizzes(languageCode: selectedLanguage.code)
        } catch { show(error) }
    }

    func signIn(email: String, password: String) async -> Bool {
        await perform {
            account = try await environment.authentication.signIn(email: email, password: password)
            await loadAll()
        }
    }

    func register(name: String, email: String, password: String) async -> Bool {
        await perform {
            account = try await environment.authentication.register(name: name, email: email, password: password)
            await loadAll()
        }
    }

    func signOut() async {
        _ = await perform { try await environment.authentication.signOut(); account = nil }
    }

    func selectLanguage(_ code: String) async {
        _ = await perform {
            account = try await environment.authentication.selectLanguage(code: code)
            sharedQuizzes = try await environment.explore.sharedQuizzes(languageCode: code)
            notice = "Learning mode updated."
        }
    }

    @discardableResult
    func perform(_ work: () async throws -> Void) async -> Bool {
        errorMessage = nil
        do { try await work(); return true }
        catch { show(error); return false }
    }

    func show(_ error: Error) {
        errorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }
}
