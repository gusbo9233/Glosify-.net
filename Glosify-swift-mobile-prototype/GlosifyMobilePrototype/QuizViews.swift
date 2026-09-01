import PhotosUI
import SwiftUI

struct QuizLibraryView: View {
    @Bindable var model: AppModel
    var body: some View {
        QuizFolderView(model: model, collectionID: nil)
            .navigationBarHidden(true)
            .accessibilityIdentifier("quiz-library-screen")
    }
}

private struct QuizFolderView: View {
    enum CreateKind: Identifiable { case quiz, collection, json; var id: String { String(describing: self) } }
    @Bindable var model: AppModel
    let collectionID: UUID?
    @State private var createKind: CreateKind?
    @State private var editingCollection: QuizCollection?
    @Environment(\.dismiss) private var dismiss

    private var collection: QuizCollection? { model.collections.first { $0.id == collectionID } }
    private var children: [QuizCollection] { model.collections.filter { $0.parentID == collectionID } }
    private var items: [Quiz] { model.quizzes.filter { $0.collectionID == collectionID && ($0.targetLanguage == model.selectedLanguage.code || model.selectedLanguage.code == "free") } }

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                VStack(alignment: .leading, spacing: 13) {
                    if collection != nil { Button { dismiss() } label: { Label("Back", systemImage: "arrow.left") }.foregroundStyle(GlosifyTheme.primary) }
                    ScreenHeader(eyebrow: "Quiz library / \(model.selectedLanguage.name)", title: collection?.name ?? "Select a quiz", subtitle: collection == nil ? "Build focused learning sets or choose one to practise." : "Organize nested collections and quizzes here.")
                    HStack { Label("\(children.count) collections", systemImage: "folder"); Label("\(items.count) quizzes", systemImage: "questionmark.square") }.font(GlosifyTheme.body(12, weight: .bold)).foregroundStyle(GlosifyTheme.muted)
                    if var current = collection {
                        Toggle("Public collection", isOn: Binding(get: { current.isPublic }, set: { value in current.isPublic = value; Task { _ = await model.perform { _ = try await model.environment.quizzes.updateCollection(current); await model.refreshLibrary() } } })).tint(GlosifyTheme.primary)
                    }
                }.glosifyCard()
                HStack(spacing: 8) {
                    Button { createKind = .json } label: { Label("JSON", systemImage: "curlybraces") }.buttonStyle(SecondaryButtonStyle())
                    Button { createKind = .collection } label: { Label("Folder", systemImage: "folder.badge.plus") }.buttonStyle(SecondaryButtonStyle())
                    Button { createKind = .quiz } label: { Label("Quiz", systemImage: "plus") }.buttonStyle(PrimaryButtonStyle())
                }
                if !children.isEmpty {
                    sectionTitle("Collections", count: children.count)
                    ForEach(children) { child in
                        NavigationLink { QuizFolderView(model: model, collectionID: child.id).navigationBarHidden(true) } label: {
                            collectionCard(child)
                        }.buttonStyle(.plain)
                    }
                }
                if !items.isEmpty {
                    sectionTitle("Your quizzes", count: items.count)
                    ForEach(items) { quiz in
                        NavigationLink { QuizDetailView(model: model, quizID: quiz.id) } label: { quizCard(quiz) }.buttonStyle(.plain)
                    }
                }
                if children.isEmpty && items.isEmpty { EmptyState(icon: "square.stack.3d.up.badge.plus", title: "Your first quiz starts here", message: "Create a quiz, collection, or paste a JSON learning set.") }
                if let collection {
                    Button(role: .destructive) {
                        Task { _ = await model.perform { try await model.environment.quizzes.deleteCollection(id: collection.id); await model.refreshLibrary(); dismiss() } }
                    } label: { Label("Delete collection", systemImage: "trash") }
                }
            }.padding(16).padding(.bottom, 90)
        }
        .glosifyScreen()
        .sheet(item: $createKind) { kind in
            switch kind {
            case .quiz: CreateQuizSheet(model: model, collectionID: collectionID)
            case .collection: CreateCollectionSheet(model: model, parentID: collectionID)
            case .json: JSONImportSheet(model: model, collectionID: collectionID)
            }
        }
    }

    private func sectionTitle(_ title: String, count: Int) -> some View {
        HStack { Text(title).font(GlosifyTheme.display(23)); Text("\(count)").font(.caption.bold()).foregroundStyle(GlosifyTheme.primary).padding(7).background(GlosifyTheme.surfaceHigh).clipShape(Circle()); Spacer() }
    }

    private func collectionCard(_ collection: QuizCollection) -> some View {
        HStack(spacing: 14) {
            Image(systemName: "folder.fill").font(.title2).foregroundStyle(.blue)
            VStack(alignment: .leading, spacing: 5) { Text(collection.name).font(GlosifyTheme.body(17, weight: .bold)); Text("\(model.quizzes.filter { $0.collectionID == collection.id }.count) quizzes · \(model.collections.filter { $0.parentID == collection.id }.count) nested").font(.caption).foregroundStyle(GlosifyTheme.muted) }
            Spacer(); Image(systemName: "arrow.right").foregroundStyle(GlosifyTheme.primary)
        }.glosifyCard(padding: 15)
    }

    private func quizCard(_ quiz: Quiz) -> some View {
        HStack(spacing: 14) {
            Image(systemName: "questionmark.bubble.fill").font(.title2).foregroundStyle(GlosifyTheme.primary)
            VStack(alignment: .leading, spacing: 5) { Text(quiz.name).font(GlosifyTheme.body(17, weight: .bold)); Text("\(quiz.words.count) words · \(quiz.sentences.count) sentences").font(.caption).foregroundStyle(GlosifyTheme.muted); Text("\(LanguageCatalog.find(quiz.sourceLanguage).name) → \(LanguageCatalog.find(quiz.targetLanguage).name)").font(.caption2).foregroundStyle(GlosifyTheme.primary) }
            Spacer(); Image(systemName: "arrow.up.right").foregroundStyle(GlosifyTheme.primary)
        }.glosifyCard(padding: 15)
    }
}

private struct CreateQuizSheet: View {
    @Bindable var model: AppModel
    let collectionID: UUID?
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var source = "en"

    var body: some View {
        FormSheet(title: "Create quiz", subtitle: "Add a focused learning set.") {
            TextField("Quiz name", text: $name)
            Picker("Source language", selection: $source) { ForEach(LanguageCatalog.all.filter(\.isLanguageLearning)) { Text("\($0.flag) \($0.name)").tag($0.code) } }
            Button("Create quiz") { Task { let ok = await model.perform { _ = try await model.environment.quizzes.createQuiz(name: name, sourceLanguage: source, targetLanguage: model.selectedLanguage.code, collectionID: collectionID); await model.refreshLibrary() }; if ok { dismiss() } } }.buttonStyle(PrimaryButtonStyle()).accessibilityIdentifier("create-quiz-submit")
        }
    }
}

private struct CreateCollectionSheet: View {
    @Bindable var model: AppModel
    let parentID: UUID?
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    var body: some View {
        FormSheet(title: "Create collection", subtitle: "Keep related quizzes together.") {
            TextField("Collection name", text: $name)
            Button("Create collection") { Task { let ok = await model.perform { _ = try await model.environment.quizzes.createCollection(name: name, languageCode: model.selectedLanguage.code, parentID: parentID); await model.refreshLibrary() }; if ok { dismiss() } } }.buttonStyle(PrimaryButtonStyle())
        }
    }
}

private struct JSONImportSheet: View {
    @Bindable var model: AppModel
    let collectionID: UUID?
    @Environment(\.dismiss) private var dismiss
    @State private var json = #"{"quizzes":[{"name":"Polish numbers","source_language":"en","words":[{"word":"one","translation":"jeden"},{"word":"two","translation":"dwa"}]}]}"#
    @State private var preview: String?
    var body: some View {
        FormSheet(title: "Import quiz JSON", subtitle: "Preview and apply local structured learning material.") {
            TextEditor(text: $json).font(.system(.caption, design: .monospaced)).frame(minHeight: 220).padding(8).background(GlosifyTheme.surfaceLow).clipShape(RoundedRectangle(cornerRadius: 12))
            if let preview { Label(preview, systemImage: "checkmark.circle").foregroundStyle(GlosifyTheme.primary) }
            Button("Preview") {
                if let data = json.data(using: .utf8), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let quizzes = object["quizzes"] as? [[String: Any]] { preview = "Ready to import \(quizzes.count) quiz\(quizzes.count == 1 ? "" : "zes")." } else { model.errorMessage = "The JSON preview could not be parsed." }
            }.buttonStyle(SecondaryButtonStyle())
            Button("Repair with mock AI") { json = #"{"quizzes":[{"name":"Repaired Polish basics","source_language":"en","words":[{"word":"hello","translation":"cześć"}]}]}"#; preview = "Mock repair produced valid JSON." }.buttonStyle(SecondaryButtonStyle())
            Button("Apply import") { Task { let ok = await model.perform { _ = try await model.environment.quizzes.importQuizJSON(json, collectionID: collectionID); await model.refreshLibrary() }; if ok { dismiss() } } }.buttonStyle(PrimaryButtonStyle())
        }
    }
}

struct FormSheet<Content: View>: View {
    let title: String
    let subtitle: String
    let content: Content
    @Environment(\.dismiss) private var dismiss

    init(title: String, subtitle: String, @ViewBuilder content: () -> Content) { self.title = title; self.subtitle = subtitle; self.content = content() }
    var body: some View {
        NavigationStack {
            ScrollView { VStack(alignment: .leading, spacing: 18) { ScreenHeader(eyebrow: "Quiz library", title: title, subtitle: subtitle); content }.padding() }.glosifyScreen().toolbar { Button("Cancel") { dismiss() } }
        }.preferredColorScheme(.dark)
    }
}

struct QuizDetailView: View {
    @Bindable var model: AppModel
    let quizID: UUID
    @Environment(\.dismiss) private var dismiss
    @State private var tab = 0
    @State private var source = ""
    @State private var translation = ""
    @State private var selectedPhoto: PhotosPickerItem?
    @State private var showingPractice = false
    @State private var showingMove = false

    private var quiz: Quiz? { model.quizzes.first { $0.id == quizID } }

    var body: some View {
        ScrollView {
            if let quiz {
                VStack(spacing: 18) {
                    VStack(alignment: .leading, spacing: 12) {
                        Eyebrow(text: "\(LanguageCatalog.find(quiz.sourceLanguage).name) → \(LanguageCatalog.find(quiz.targetLanguage).name)")
                        Text(quiz.name).font(GlosifyTheme.display(31))
                        HStack { Label("\(quiz.words.count) words", systemImage: "character.book.closed"); Label("\(quiz.sentences.count) sentences", systemImage: "quote.bubble") }.font(.caption).foregroundStyle(GlosifyTheme.muted)
                        Toggle("Public quiz", isOn: Binding(get: { quiz.isPublic }, set: { value in var update = quiz; update.isPublic = value; Task { _ = await model.perform { _ = try await model.environment.quizzes.updateQuiz(update); await model.refreshLibrary() } } })).tint(GlosifyTheme.primary)
                    }.glosifyCard()
                    Button { showingPractice = true } label: { Label("Start practice", systemImage: "play.fill") }.buttonStyle(PrimaryButtonStyle())
                    HStack { Button("Move") { showingMove = true }.buttonStyle(SecondaryButtonStyle()); PhotosPicker(selection: $selectedPhoto, matching: .images) { Label("Scan", systemImage: "camera.viewfinder") }.buttonStyle(SecondaryButtonStyle()) }
                    Picker("Content", selection: $tab) { Text("Words").tag(0); Text("Sentences").tag(1) }.pickerStyle(.segmented)
                    addBar(quiz)
                    if tab == 0 { ForEach(quiz.words) { wordCard($0, quiz: quiz) } } else { ForEach(quiz.sentences) { sentenceCard($0, quiz: quiz) } }
                    Button(role: .destructive) { Task { let ok = await model.perform { try await model.environment.quizzes.deleteQuiz(id: quiz.id); await model.refreshLibrary() }; if ok { dismiss() } } } label: { Label("Delete quiz", systemImage: "trash") }
                }.padding(16).padding(.bottom, 90)
            } else { EmptyState(icon: "questionmark", title: "Quiz unavailable", message: "It may have been deleted during this prototype session.").padding() }
        }.glosifyScreen().navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $showingPractice) { if let quiz { PracticeSettingsView(model: model, quiz: quiz) } }
        .sheet(isPresented: $showingMove) { MoveQuizView(model: model, quizID: quizID) }
        .onChange(of: selectedPhoto) { _, item in if item != nil { source = "hello"; translation = "cześć"; model.notice = "Mock image extraction found a word pair." } }
        .onAppear { model.assistantContext = quiz?.name ?? "Quizzes" }
        .onDisappear { model.assistantContext = "Quizzes" }
    }

    private func addBar(_ quiz: Quiz) -> some View {
        VStack(spacing: 10) {
            TextField(tab == 0 ? "Word or prompt" : "Sentence", text: $source).textFieldStyle(.roundedBorder)
            TextField("Translation or answer", text: $translation).textFieldStyle(.roundedBorder)
            Button(tab == 0 ? "Add word" : "Add sentence") {
                let first = source, second = translation
                Task {
                    let ok = await model.perform {
                        if tab == 0 { _ = try await model.environment.quizzes.addWord(quizID: quiz.id, source: first, translation: second) }
                        else { _ = try await model.environment.quizzes.addSentence(quizID: quiz.id, source: first, translation: second) }
                        await model.refreshLibrary()
                    }
                    if ok { source = ""; translation = "" }
                }
            }.buttonStyle(PrimaryButtonStyle())
        }.glosifyCard(padding: 14)
    }

    private func wordCard(_ word: VocabularyWord, quiz: Quiz) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "translate").foregroundStyle(GlosifyTheme.primary).frame(width: 32)
            VStack(alignment: .leading) { Text(word.source).font(GlosifyTheme.body(17, weight: .bold)); Text(word.translation).foregroundStyle(GlosifyTheme.muted) }
            Spacer()
            Button { model.speech.speak(word.source, locale: LanguageCatalog.find(quiz.sourceLanguage).locale) } label: { Image(systemName: "speaker.wave.2.fill").frame(width: 44, height: 44) }.accessibilityLabel("Speak \(word.source)")
            Button(role: .destructive) { Task { _ = await model.perform { _ = try await model.environment.quizzes.deleteWord(quizID: quiz.id, wordID: word.id); await model.refreshLibrary() } } } label: { Image(systemName: "trash").frame(width: 44, height: 44) }
        }.glosifyCard(padding: 12)
    }

    private func sentenceCard(_ sentence: QuizSentence, quiz: Quiz) -> some View {
        HStack { VStack(alignment: .leading, spacing: 5) { Text(sentence.source).font(GlosifyTheme.body(16, weight: .bold)); Text(sentence.translation).foregroundStyle(GlosifyTheme.muted) }; Spacer(); Button(role: .destructive) { Task { _ = await model.perform { _ = try await model.environment.quizzes.deleteSentence(quizID: quiz.id, sentenceID: sentence.id); await model.refreshLibrary() } } } label: { Image(systemName: "trash").frame(width: 44, height: 44) } }.glosifyCard(padding: 13)
    }
}

private struct MoveQuizView: View {
    @Bindable var model: AppModel
    let quizID: UUID
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack { List { Button("Quiz library root") { move(nil) }; ForEach(model.collections) { collection in Button(collection.name) { move(collection.id) } } }.navigationTitle("Move quiz").toolbar { Button("Cancel") { dismiss() } } }.preferredColorScheme(.dark)
    }
    private func move(_ collectionID: UUID?) { Task { let ok = await model.perform { try await model.environment.quizzes.moveQuiz(id: quizID, collectionID: collectionID); await model.refreshLibrary() }; if ok { dismiss() } } }
}

private struct PracticeSettingsView: View {
    @Bindable var model: AppModel
    let quiz: Quiz
    @Environment(\.dismiss) private var dismiss
    @State private var configuration = PracticeConfiguration()
    @State private var started = false
    var body: some View {
        NavigationStack {
            if started { PracticeSessionView(model: model, quiz: quiz, configuration: configuration) }
            else {
                Form {
                    Section("Practice mode") { Picker("Mode", selection: $configuration.mode) { Text("Flashcards").tag(PracticeMode.flashcards); Text("Typing").tag(PracticeMode.typing) }.pickerStyle(.segmented) }
                    Section("Direction") { Picker("Direction", selection: $configuration.direction) { ForEach(PracticeDirection.allCases) { Text($0.title).tag($0) } } }
                    Section("Session") { Stepper("\(configuration.itemCount) items", value: $configuration.itemCount, in: 1...max(quiz.words.count, 1)); Toggle("Include sentences", isOn: $configuration.includesSentences) }
                    Section { Button("Start session") { started = true }.buttonStyle(PrimaryButtonStyle()).disabled(quiz.words.isEmpty && quiz.sentences.isEmpty) }
                }.scrollContentBackground(.hidden).background(GlosifyTheme.background).navigationTitle(quiz.name).toolbar { Button("Close") { dismiss() } }
            }
        }.preferredColorScheme(.dark)
    }
}

private struct PracticeItem: Identifiable { let id: UUID; let prompt: String; let answer: String; let locale: String }

private struct PracticeSessionView: View {
    @Bindable var model: AppModel
    let quiz: Quiz
    let configuration: PracticeConfiguration
    @Environment(\.dismiss) private var dismiss
    @State private var index = 0
    @State private var revealed = false
    @State private var answer = ""
    @State private var correct = 0
    @State private var checked: Bool?

    private var items: [PracticeItem] {
        var result = quiz.words.map { PracticeItem(id: $0.id, prompt: configuration.direction == .sourceToTarget ? $0.source : $0.translation, answer: configuration.direction == .sourceToTarget ? $0.translation : $0.source, locale: LanguageCatalog.find(configuration.direction == .sourceToTarget ? quiz.sourceLanguage : quiz.targetLanguage).locale) }
        if configuration.includesSentences { result += quiz.sentences.map { PracticeItem(id: $0.id, prompt: configuration.direction == .sourceToTarget ? $0.source : $0.translation, answer: configuration.direction == .sourceToTarget ? $0.translation : $0.source, locale: LanguageCatalog.find(configuration.direction == .sourceToTarget ? quiz.sourceLanguage : quiz.targetLanguage).locale) } }
        return Array(result.prefix(configuration.itemCount))
    }

    var body: some View {
        VStack(spacing: 20) {
            if index >= items.count {
                Spacer(); Text("\(items.isEmpty ? 0 : Int(Double(correct) / Double(items.count) * 100))%").font(GlosifyTheme.display(58)).foregroundStyle(GlosifyTheme.primary); Text("Session complete").font(GlosifyTheme.display(26)); Text("\(correct) of \(items.count) correct").foregroundStyle(GlosifyTheme.muted); Button("Done") { dismiss() }.buttonStyle(PrimaryButtonStyle()); Spacer()
            } else {
                let item = items[index]
                HStack { Text("\(index + 1) of \(items.count)"); Spacer(); Text("\(correct) correct") }.font(.caption.bold()).foregroundStyle(GlosifyTheme.muted)
                ProgressView(value: Double(index), total: Double(max(items.count, 1))).tint(GlosifyTheme.primary)
                Spacer()
                VStack(spacing: 20) {
                    Eyebrow(text: configuration.mode == .flashcards ? "Flashcard" : "Type the answer")
                    HStack { Text(item.prompt).font(GlosifyTheme.display(32)); Button { model.speech.speak(item.prompt, locale: item.locale) } label: { Image(systemName: "speaker.wave.2") } }
                    if configuration.mode == .flashcards {
                        if revealed {
                            Divider()
                            Text(item.answer).font(GlosifyTheme.display(25)).foregroundStyle(GlosifyTheme.primary)
                            LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 8) {
                                ForEach(["Again", "Hard", "Good", "Easy"], id: \.self) { rating in
                                    Button(rating) { if rating == "Good" || rating == "Easy" { correct += 1 }; next() }.buttonStyle(SecondaryButtonStyle())
                                }
                            }
                        }
                        else { Button("Show answer") { revealed = true }.buttonStyle(PrimaryButtonStyle()) }
                    } else {
                        TextField("Type your answer…", text: $answer).textFieldStyle(.roundedBorder).textInputAutocapitalization(.never)
                        if let checked { Text(checked ? "Correct" : "Answer: \(item.answer)").foregroundStyle(checked ? GlosifyTheme.primary : GlosifyTheme.error) }
                        Button(checked == nil ? "Check answer" : "Continue") { if checked == nil { let matches = PracticeScorer.matches(answer, expected: item.answer); checked = matches; if matches { correct += 1 } } else { next() } }.buttonStyle(PrimaryButtonStyle())
                    }
                }.glosifyCard()
                Spacer()
            }
        }.padding().glosifyScreen().navigationBarBackButtonHidden()
    }

    private func next() { index += 1; revealed = false; answer = ""; checked = nil }
}
