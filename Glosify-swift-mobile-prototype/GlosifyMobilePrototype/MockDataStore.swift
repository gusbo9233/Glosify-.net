import Foundation

actor MockDataStore {
    private let configuration: MockConfiguration
    private var account: Account?
    private var collections: [QuizCollection]
    private var quizzes: [Quiz]
    private var anki: [AnkiCollection]
    private var shared: [SharedQuiz]
    private var bookItems: [BookDocument]
    private var transcriptItems: [Transcript]
    private var chatItems: [AssistantChat]
    private let creditPackages: [CreditPackage]
    private var nextID = 10_000

    init(configuration: MockConfiguration = .init()) {
        self.configuration = configuration
        let seed = SeedData.make()
        account = seed.account
        collections = seed.collections
        quizzes = seed.quizzes
        anki = seed.anki
        shared = seed.shared
        bookItems = seed.books
        transcriptItems = seed.transcripts
        chatItems = seed.chats
        creditPackages = seed.packages
        if configuration.startsEmpty {
            collections = []
            quizzes = []
            anki = []
            shared = []
            bookItems = []
            transcriptItems = []
            chatItems = []
        }
    }

    private func prepare(_ operation: MockOperation) async throws {
        if configuration.latency != .zero {
            try await Task.sleep(for: configuration.latency)
        }
        if configuration.failingOperations.contains(operation) {
            throw PrototypeError.injectedFailure(operation)
        }
    }

    private func id() -> UUID {
        nextID += 1
        return SeedID.uuid(nextID)
    }

    private static func cleaned(_ value: String, field: String, maximum: Int = 160) throws -> String {
        let result = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !result.isEmpty else { throw PrototypeError.invalidInput("\(field) is required.") }
        guard result.count <= maximum else { throw PrototypeError.invalidInput("\(field) is too long.") }
        return result
    }
}

extension MockDataStore: AuthenticationRepository {
    func currentAccount() async throws -> Account? {
        try await prepare(.authentication)
        return account
    }

    func signIn(email: String, password: String) async throws -> Account {
        try await prepare(.authentication)
        let email = try Self.cleaned(email, field: "Email", maximum: 254)
        guard email.contains("@") else { throw PrototypeError.invalidInput("Enter a valid email address.") }
        guard password.count >= 6 else { throw PrototypeError.invalidInput("Password must contain at least 6 characters.") }
        var demo = SeedData.account
        demo.email = email
        account = demo
        return demo
    }

    func register(name: String, email: String, password: String) async throws -> Account {
        try await prepare(.authentication)
        let name = try Self.cleaned(name, field: "Name")
        let email = try Self.cleaned(email, field: "Email", maximum: 254)
        guard email.contains("@") else { throw PrototypeError.invalidInput("Enter a valid email address.") }
        guard password.count >= 6 else { throw PrototypeError.invalidInput("Password must contain at least 6 characters.") }
        let created = Account(id: id(), email: email, displayName: name, credits: 500, selectedLanguageCode: "pl")
        account = created
        return created
    }

    func signOut() async throws {
        try await prepare(.authentication)
        account = nil
    }

    func requestPasswordReset(email: String) async throws {
        try await prepare(.authentication)
        guard email.contains("@") else { throw PrototypeError.invalidInput("Enter a valid email address.") }
    }

    func selectLanguage(code: String) async throws -> Account {
        try await prepare(.authentication)
        guard LanguageCatalog.all.contains(where: { $0.code == code }), var current = account else {
            throw PrototypeError.invalidInput("Choose a supported learning mode.")
        }
        current.selectedLanguageCode = code
        account = current
        return current
    }
}

extension MockDataStore: QuizRepository {
    func quizLibrary() async throws -> (collections: [QuizCollection], quizzes: [Quiz]) {
        try await prepare(.quizzes)
        return (collections.sorted { $0.createdAt < $1.createdAt }, quizzes.sorted { $0.createdAt < $1.createdAt })
    }

    func createQuiz(name: String, sourceLanguage: String, targetLanguage: String, collectionID: UUID?) async throws -> Quiz {
        try await prepare(.quizzes)
        let item = Quiz(id: id(), name: try Self.cleaned(name, field: "Quiz name"), sourceLanguage: sourceLanguage, targetLanguage: targetLanguage, collectionID: collectionID, isPublic: false, createdAt: Date(), words: [], sentences: [])
        quizzes.append(item)
        return item
    }

    func createCollection(name: String, languageCode: String, parentID: UUID?) async throws -> QuizCollection {
        try await prepare(.quizzes)
        let item = QuizCollection(id: id(), name: try Self.cleaned(name, field: "Collection name"), languageCode: languageCode, parentID: parentID, isPublic: false, createdAt: Date())
        collections.append(item)
        return item
    }

    func updateCollection(_ collection: QuizCollection) async throws -> QuizCollection {
        try await prepare(.quizzes)
        guard let index = collections.firstIndex(where: { $0.id == collection.id }) else { throw PrototypeError.notFound("Collection not found.") }
        var updated = collection
        updated.name = try Self.cleaned(collection.name, field: "Collection name")
        collections[index] = updated
        return updated
    }

    func deleteCollection(id collectionID: UUID) async throws {
        try await prepare(.quizzes)
        guard collections.contains(where: { $0.id == collectionID }) else { throw PrototypeError.notFound("Collection not found.") }
        var removed: Set<UUID> = [collectionID]
        var changed = true
        while changed {
            let before = removed.count
            removed.formUnion(collections.filter { $0.parentID.map(removed.contains) == true }.map(\.id))
            changed = before != removed.count
        }
        collections.removeAll { removed.contains($0.id) }
        for index in quizzes.indices where quizzes[index].collectionID.map(removed.contains) == true {
            quizzes[index].collectionID = nil
        }
    }

    func updateQuiz(_ quiz: Quiz) async throws -> Quiz {
        try await prepare(.quizzes)
        guard let index = quizzes.firstIndex(where: { $0.id == quiz.id }) else { throw PrototypeError.notFound("Quiz not found.") }
        var updated = quiz
        updated.name = try Self.cleaned(quiz.name, field: "Quiz name")
        quizzes[index] = updated
        return updated
    }

    func deleteQuiz(id quizID: UUID) async throws {
        try await prepare(.quizzes)
        guard quizzes.contains(where: { $0.id == quizID }) else { throw PrototypeError.notFound("Quiz not found.") }
        quizzes.removeAll { $0.id == quizID }
    }

    func moveQuiz(id quizID: UUID, collectionID: UUID?) async throws {
        try await prepare(.quizzes)
        guard let index = quizzes.firstIndex(where: { $0.id == quizID }) else { throw PrototypeError.notFound("Quiz not found.") }
        quizzes[index].collectionID = collectionID
    }

    func addWord(quizID: UUID, source: String, translation: String) async throws -> Quiz {
        try await prepare(.quizzes)
        guard let index = quizzes.firstIndex(where: { $0.id == quizID }) else { throw PrototypeError.notFound("Quiz not found.") }
        let word = VocabularyWord(id: id(), source: try Self.cleaned(source, field: "Word", maximum: 200), translation: try Self.cleaned(translation, field: "Translation", maximum: 500), createdAt: Date())
        quizzes[index].words.append(word)
        return quizzes[index]
    }

    func addSentence(quizID: UUID, source: String, translation: String) async throws -> Quiz {
        try await prepare(.quizzes)
        guard let index = quizzes.firstIndex(where: { $0.id == quizID }) else { throw PrototypeError.notFound("Quiz not found.") }
        quizzes[index].sentences.append(QuizSentence(id: id(), source: try Self.cleaned(source, field: "Sentence", maximum: 500), translation: try Self.cleaned(translation, field: "Translation", maximum: 500)))
        return quizzes[index]
    }

    func deleteWord(quizID: UUID, wordID: UUID) async throws -> Quiz {
        try await prepare(.quizzes)
        guard let index = quizzes.firstIndex(where: { $0.id == quizID }) else { throw PrototypeError.notFound("Quiz not found.") }
        quizzes[index].words.removeAll { $0.id == wordID }
        return quizzes[index]
    }

    func deleteSentence(quizID: UUID, sentenceID: UUID) async throws -> Quiz {
        try await prepare(.quizzes)
        guard let index = quizzes.firstIndex(where: { $0.id == quizID }) else { throw PrototypeError.notFound("Quiz not found.") }
        quizzes[index].sentences.removeAll { $0.id == sentenceID }
        return quizzes[index]
    }

    func importQuizJSON(_ json: String, collectionID: UUID?) async throws -> [Quiz] {
        try await prepare(.quizzes)
        struct ImportEnvelope: Decodable { let quizzes: [ImportQuiz] }
        struct ImportQuiz: Decodable { let name: String; let source_language: String?; let words: [ImportWord]?; let sentences: [ImportSentence]? }
        struct ImportWord: Decodable { let word: String; let translation: String }
        struct ImportSentence: Decodable { let text: String; let translation: String }
        struct ValidatedQuiz {
            let name: String
            let sourceLanguage: String
            let words: [(source: String, translation: String)]
            let sentences: [(source: String, translation: String)]
        }
        guard let data = json.data(using: .utf8) else { throw PrototypeError.invalidInput("Paste valid UTF-8 JSON.") }
        let envelope: ImportEnvelope
        do { envelope = try JSONDecoder().decode(ImportEnvelope.self, from: data) }
        catch { throw PrototypeError.invalidInput("JSON must contain a quizzes array with names and learning items.") }
        guard !envelope.quizzes.isEmpty else { throw PrototypeError.invalidInput("Import at least one quiz.") }
        let currentTarget = account?.selectedLanguageCode ?? "pl"
        let validated = try envelope.quizzes.prefix(20).map { source in
            ValidatedQuiz(
                name: try Self.cleaned(source.name, field: "Quiz name"),
                sourceLanguage: source.source_language ?? "en",
                words: try (source.words ?? []).map { (try Self.cleaned($0.word, field: "Word", maximum: 200), try Self.cleaned($0.translation, field: "Translation", maximum: 500)) },
                sentences: try (source.sentences ?? []).map { (try Self.cleaned($0.text, field: "Sentence", maximum: 500), try Self.cleaned($0.translation, field: "Translation", maximum: 500)) }
            )
        }
        let imported = validated.map { source in
            Quiz(
                id: id(),
                name: source.name,
                sourceLanguage: source.sourceLanguage,
                targetLanguage: currentTarget,
                collectionID: collectionID,
                isPublic: false,
                createdAt: Date(),
                words: source.words.map { VocabularyWord(id: id(), source: $0.source, translation: $0.translation, createdAt: Date()) },
                sentences: source.sentences.map { QuizSentence(id: id(), source: $0.source, translation: $0.translation) }
            )
        }
        quizzes.append(contentsOf: imported)
        return imported
    }
}

extension MockDataStore: AnkiRepository {
    func ankiCollections() async throws -> [AnkiCollection] {
        try await prepare(.anki)
        return anki
    }

    func createAnkiCollection(name: String, sourceLanguage: String, targetLanguage: String) async throws -> AnkiCollection {
        try await prepare(.anki)
        let item = AnkiCollection(id: id(), name: try Self.cleaned(name, field: "Collection name"), sourceLanguage: sourceLanguage, targetLanguage: targetLanguage, cards: [])
        anki.append(item)
        return item
    }

    func addQuiz(_ quizID: UUID, to collectionID: UUID) async throws -> AnkiCollection {
        try await prepare(.anki)
        guard let quiz = quizzes.first(where: { $0.id == quizID }), let index = anki.firstIndex(where: { $0.id == collectionID }) else { throw PrototypeError.notFound("Quiz or Anki collection not found.") }
        let existing = Set(anki[index].cards.map { "\($0.prompt)|\($0.answer)" })
        anki[index].cards.append(contentsOf: quiz.words.filter { !existing.contains("\($0.source)|\($0.translation)") }.map {
            AnkiCard(id: id(), prompt: $0.source, answer: $0.translation, promptLanguage: quiz.sourceLanguage, answerLanguage: quiz.targetLanguage, dueAt: Date(), intervalDays: 0, reviewCount: 0)
        })
        return anki[index]
    }

    func rateCard(collectionID: UUID, cardID: UUID, rating: Int) async throws -> AnkiCollection {
        try await prepare(.anki)
        guard let collectionIndex = anki.firstIndex(where: { $0.id == collectionID }), let cardIndex = anki[collectionIndex].cards.firstIndex(where: { $0.id == cardID }) else { throw PrototypeError.notFound("Card not found.") }
        let intervals = [1: 0, 2: 1, 3: 3, 4: 7]
        let days = intervals[rating] ?? 1
        anki[collectionIndex].cards[cardIndex].intervalDays = days
        anki[collectionIndex].cards[cardIndex].reviewCount += 1
        anki[collectionIndex].cards[cardIndex].dueAt = Calendar.current.date(byAdding: .day, value: days, to: Date()) ?? Date()
        return anki[collectionIndex]
    }
}

extension MockDataStore: ExploreRepository {
    func sharedQuizzes(languageCode: String) async throws -> [SharedQuiz] {
        try await prepare(.explore)
        return shared.filter { $0.quiz.targetLanguage == languageCode }
    }

    func copySharedQuiz(id sharedID: UUID) async throws -> Quiz {
        try await prepare(.explore)
        guard let index = shared.firstIndex(where: { $0.id == sharedID }) else { throw PrototypeError.notFound("Shared quiz not found.") }
        var copy = shared[index].quiz
        copy = Quiz(id: id(), name: copy.name, sourceLanguage: copy.sourceLanguage, targetLanguage: copy.targetLanguage, collectionID: nil, isPublic: false, createdAt: Date(), words: copy.words.map { VocabularyWord(id: id(), source: $0.source, translation: $0.translation, createdAt: Date()) }, sentences: copy.sentences.map { QuizSentence(id: id(), source: $0.source, translation: $0.translation) })
        quizzes.append(copy)
        shared[index].copyCount += 1
        return copy
    }
}

extension MockDataStore: BookRepository {
    func books() async throws -> [BookDocument] {
        try await prepare(.books)
        return bookItems
    }

    func importBook(title: String, fileName: String, data: Data, pageTexts: [String]) async throws -> BookDocument {
        try await prepare(.books)
        let pages = pageTexts.enumerated().map { offset, text in BookPage(number: offset + 1, sourceText: text.isEmpty ? "This page contains visual material." : text, mockTranslation: "[Mock English translation]\n\n\(text)") }
        let item = BookDocument(id: id(), title: try Self.cleaned(title, field: "Book title"), originalFileName: fileName, createdAt: Date(), pageCount: max(pageTexts.count, 1), pages: pages, pdfData: data)
        bookItems.append(item)
        return item
    }

    func deleteBook(id bookID: UUID) async throws {
        try await prepare(.books)
        guard bookItems.contains(where: { $0.id == bookID }) else { throw PrototypeError.notFound("Book not found.") }
        bookItems.removeAll { $0.id == bookID }
    }
}

extension MockDataStore: TranscriptRepository {
    func transcripts() async throws -> [Transcript] {
        try await prepare(.transcripts)
        return transcriptItems
    }

    func renameTranscript(id transcriptID: UUID, title: String) async throws -> Transcript {
        try await prepare(.transcripts)
        guard let index = transcriptItems.firstIndex(where: { $0.id == transcriptID }) else { throw PrototypeError.notFound("Transcript not found.") }
        transcriptItems[index].title = try Self.cleaned(title, field: "Transcript title")
        transcriptItems[index].updatedAt = Date()
        return transcriptItems[index]
    }

    func deleteTranscript(id transcriptID: UUID) async throws {
        try await prepare(.transcripts)
        guard transcriptItems.contains(where: { $0.id == transcriptID }) else { throw PrototypeError.notFound("Transcript not found.") }
        transcriptItems.removeAll { $0.id == transcriptID }
    }
}

extension MockDataStore: AssistantRepository {
    func chats() async throws -> [AssistantChat] {
        try await prepare(.assistant)
        return chatItems.sorted { $0.updatedAt > $1.updatedAt }
    }

    func createChat(context: String) async throws -> AssistantChat {
        try await prepare(.assistant)
        let chat = AssistantChat(id: id(), title: "New study chat", contextLabel: context, messages: [], updatedAt: Date())
        chatItems.append(chat)
        return chat
    }

    func sendMessage(chatID: UUID, text: String, context: String) async throws -> AssistantChat {
        try await prepare(.assistant)
        guard let index = chatItems.firstIndex(where: { $0.id == chatID }) else { throw PrototypeError.notFound("Chat not found.") }
        let prompt = try Self.cleaned(text, field: "Message", maximum: 8_000)
        chatItems[index].messages.append(AssistantMessage(id: id(), role: .user, text: prompt, createdAt: Date(), pendingChange: nil, feedback: nil))
        let lower = prompt.lowercased()
        let pending: PendingLibraryChange?
        let response: String
        if lower.contains("quiz") || lower.contains("words") {
            let words = [
                VocabularyWord(id: id(), source: "dzień dobry", translation: "good morning", createdAt: Date()),
                VocabularyWord(id: id(), source: "dziękuję", translation: "thank you", createdAt: Date()),
                VocabularyWord(id: id(), source: "proszę", translation: "please / you're welcome", createdAt: Date())
            ]
            pending = PendingLibraryChange(id: id(), summary: "Create a three-word Polish travel quiz", quizName: "Polish travel essentials", words: words, state: .pending)
            response = "I prepared a focused starter quiz from your request. Review the proposed library change below."
        } else {
            pending = nil
            response = "Here is a mock coaching response for \(context). Try recalling the answer before revealing it, then revisit anything that felt uncertain."
        }
        chatItems[index].messages.append(AssistantMessage(id: id(), role: .assistant, text: response, createdAt: Date(), pendingChange: pending, feedback: nil))
        chatItems[index].title = String(prompt.prefix(42))
        chatItems[index].contextLabel = context
        chatItems[index].updatedAt = Date()
        return chatItems[index]
    }

    func resolveChange(chatID: UUID, changeID: UUID, apply: Bool) async throws -> AssistantChat {
        try await prepare(.assistant)
        guard let chatIndex = chatItems.firstIndex(where: { $0.id == chatID }), let messageIndex = chatItems[chatIndex].messages.firstIndex(where: { $0.pendingChange?.id == changeID }), var change = chatItems[chatIndex].messages[messageIndex].pendingChange else { throw PrototypeError.notFound("Pending change not found.") }
        change.state = apply ? .applied : .rejected
        chatItems[chatIndex].messages[messageIndex].pendingChange = change
        if apply {
            quizzes.append(Quiz(id: id(), name: change.quizName, sourceLanguage: "en", targetLanguage: account?.selectedLanguageCode ?? "pl", collectionID: nil, isPublic: false, createdAt: Date(), words: change.words, sentences: []))
        }
        return chatItems[chatIndex]
    }

    func saveFeedback(chatID: UUID, messageID: UUID, rating: Int) async throws -> AssistantChat {
        try await prepare(.assistant)
        guard let chatIndex = chatItems.firstIndex(where: { $0.id == chatID }), let messageIndex = chatItems[chatIndex].messages.firstIndex(where: { $0.id == messageID }) else { throw PrototypeError.notFound("Message not found.") }
        chatItems[chatIndex].messages[messageIndex].feedback = rating
        return chatItems[chatIndex]
    }

    func deleteChat(id chatID: UUID) async throws {
        try await prepare(.assistant)
        chatItems.removeAll { $0.id == chatID }
    }
}

extension MockDataStore: CreditRepository {
    func packages() async throws -> [CreditPackage] {
        try await prepare(.credits)
        return creditPackages
    }

    func purchase(packageID: String) async throws -> Account {
        try await prepare(.credits)
        guard let package = creditPackages.first(where: { $0.id == packageID }), var current = account else { throw PrototypeError.notFound("Credit package not found.") }
        current.credits += package.credits
        account = current
        return current
    }
}

private enum SeedData {
    struct Values {
        let account: Account
        let collections: [QuizCollection]
        let quizzes: [Quiz]
        let anki: [AnkiCollection]
        let shared: [SharedQuiz]
        let books: [BookDocument]
        let transcripts: [Transcript]
        let chats: [AssistantChat]
        let packages: [CreditPackage]
    }

    static let account = Account(id: SeedID.uuid(1), email: "learner@glosify.se", displayName: "Demo Learner", credits: 1_564, selectedLanguageCode: "pl")

    static func make() -> Values {
        let now = Date(timeIntervalSince1970: 1_777_800_000)
        let travelID = SeedID.uuid(10)
        let verbs = [
            ("to be", "być"), ("to have", "mieć"), ("to do", "robić"), ("to go", "iść"),
            ("to want", "chcieć"), ("to know", "wiedzieć"), ("to see", "widzieć"), ("to speak", "mówić")
        ].enumerated().map { index, pair in VocabularyWord(id: SeedID.uuid(100 + index), source: pair.0, translation: pair.1, createdAt: now.addingTimeInterval(Double(index) * 60)) }
        let basics = Quiz(id: SeedID.uuid(20), name: "Common Polish Verbs", sourceLanguage: "en", targetLanguage: "pl", collectionID: nil, isPublic: false, createdAt: now, words: verbs, sentences: [QuizSentence(id: SeedID.uuid(201), source: "I want to learn Polish.", translation: "Chcę uczyć się polskiego.")])
        let travel = Quiz(id: SeedID.uuid(21), name: "Travel phrases", sourceLanguage: "en", targetLanguage: "pl", collectionID: travelID, isPublic: true, createdAt: now.addingTimeInterval(3_600), words: [VocabularyWord(id: SeedID.uuid(210), source: "Where is the station?", translation: "Gdzie jest stacja?", createdAt: now), VocabularyWord(id: SeedID.uuid(211), source: "One ticket, please", translation: "Jeden bilet, proszę", createdAt: now)], sentences: [])
        let collection = QuizCollection(id: travelID, name: "Polish for travel", languageCode: "pl", parentID: nil, isPublic: false, createdAt: now)
        let cards = verbs.prefix(5).enumerated().map { index, word in AnkiCard(id: SeedID.uuid(300 + index), prompt: word.source, answer: word.translation, promptLanguage: "en", answerLanguage: "pl", dueAt: now.addingTimeInterval(index < 3 ? -300 : 86_400), intervalDays: index, reviewCount: index) }
        let anki = AnkiCollection(id: SeedID.uuid(30), name: "Everyday Polish", sourceLanguage: "English", targetLanguage: "Polish", cards: cards)
        let publicQuiz = Quiz(id: SeedID.uuid(40), name: "Polish café essentials", sourceLanguage: basics.sourceLanguage, targetLanguage: basics.targetLanguage, collectionID: nil, isPublic: true, createdAt: now, words: basics.words, sentences: basics.sentences)
        let shared = SharedQuiz(id: SeedID.uuid(41), author: "Marta", quiz: publicQuiz, copyCount: 128)
        let pages = [BookPage(number: 1, sourceText: "Polish uses seven grammatical cases. Start by noticing how endings change inside complete phrases.", mockTranslation: "Polish uses seven grammatical cases. Begin by noticing changing endings in complete phrases."), BookPage(number: 2, sourceText: "Dzień dobry. Jak się masz? These short greetings are useful every day.", mockTranslation: "Good morning. How are you? These short greetings are useful every day.")]
        let book = BookDocument(id: SeedID.uuid(50), title: "A practical introduction to Polish", originalFileName: "polish-introduction.pdf", createdAt: now, pageCount: pages.count, pages: pages, pdfData: nil)
        let segments = [
            TranscriptSegment(id: SeedID.uuid(601), capturedAt: now, sourceText: "Witamy w dzisiejszym programie.", translatedText: "Welcome to today's programme."),
            TranscriptSegment(id: SeedID.uuid(602), capturedAt: now.addingTimeInterval(12), sourceText: "Porozmawiamy o podróżach.", translatedText: "We will talk about travel."),
            TranscriptSegment(id: SeedID.uuid(603), capturedAt: now.addingTimeInterval(26), sourceText: "Zaczynamy.", translatedText: "Let's begin.")
        ]
        let transcript = Transcript(id: SeedID.uuid(60), title: "Polish travel podcast", sourceLanguage: "pl", targetLanguage: "en", stream: "Original + translation", createdAt: now, updatedAt: now.addingTimeInterval(26), segments: segments)
        let welcome = AssistantMessage(id: SeedID.uuid(701), role: .assistant, text: "Welcome back. I can help you create quizzes, explain vocabulary, or plan a study session.", createdAt: now, pendingChange: nil, feedback: nil)
        let chat = AssistantChat(id: SeedID.uuid(70), title: "Polish study plan", contextLabel: "Glosify", messages: [welcome], updatedAt: now)
        return Values(account: account, collections: [collection], quizzes: [basics, travel], anki: [anki], shared: [shared], books: [book], transcripts: [transcript], chats: [chat], packages: [.init(id: "starter", name: "Starter", credits: 500, priceSEK: 29), .init(id: "learner", name: "Learner", credits: 2_000, priceSEK: 89), .init(id: "power", name: "Power learner", credits: 5_000, priceSEK: 179)])
    }
}
