import SwiftUI

struct ExploreView: View {
    @Bindable var model: AppModel
    @State private var query = ""
    @State private var copiedID: UUID?

    private var filtered: [SharedQuiz] {
        query.isEmpty ? model.sharedQuizzes : model.sharedQuizzes.filter { $0.quiz.name.localizedCaseInsensitiveContains(query) || $0.author.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                ScreenHeader(eyebrow: "Community library", title: "Discover and learn", subtitle: "Browse public material and copy it into your in-memory quiz library.")
                TextField("Search public quizzes", text: $query).textFieldStyle(.roundedBorder)
                if filtered.isEmpty { EmptyState(icon: "magnifyingglass", title: "Nothing found", message: "Try a broader quiz name or creator.") }
                ForEach(filtered) { item in
                    NavigationLink { SharedQuizDetailView(model: model, itemID: item.id) } label: { sharedCard(item) }.buttonStyle(.plain)
                }
            }.padding().padding(.bottom, 90)
        }.glosifyScreen().navigationBarHidden(true).accessibilityIdentifier("explore-screen")
    }

    private func sharedCard(_ item: SharedQuiz) -> some View {
        VStack(alignment: .leading, spacing: 13) {
            HStack { Label("PUBLIC QUIZ", systemImage: "globe").font(.caption.bold()).foregroundStyle(GlosifyTheme.primary); Spacer(); Image(systemName: "arrow.up.right") }
            Text(item.quiz.name).font(GlosifyTheme.display(21))
            Text("\(LanguageCatalog.find(item.quiz.sourceLanguage).name) → \(LanguageCatalog.find(item.quiz.targetLanguage).name) · \(item.quiz.words.count) words").font(.caption).foregroundStyle(GlosifyTheme.muted)
            HStack { Label(item.author, systemImage: "person"); Spacer(); Label("\(item.copyCount) copies", systemImage: "square.on.square") }.font(.caption).foregroundStyle(GlosifyTheme.muted)
        }.glosifyCard()
    }
}

private struct SharedQuizDetailView: View {
    @Bindable var model: AppModel
    let itemID: UUID
    @State private var copied = false
    private var item: SharedQuiz? { model.sharedQuizzes.first { $0.id == itemID } }

    var body: some View {
        ScrollView {
            if let item {
                VStack(spacing: 18) {
                    ScreenHeader(eyebrow: "Shared by \(item.author)", title: item.quiz.name, subtitle: "Preview the learning material before adding your own private copy.")
                    Button(copied ? "Copied to your library" : "Copy quiz") {
                        Task {
                            let ok = await model.perform {
                                _ = try await model.environment.explore.copySharedQuiz(id: item.id)
                                model.sharedQuizzes = try await model.environment.explore.sharedQuizzes(languageCode: model.selectedLanguage.code)
                                await model.refreshLibrary()
                            }
                            copied = ok
                        }
                    }.buttonStyle(PrimaryButtonStyle()).disabled(copied).accessibilityIdentifier("copy-shared-quiz")
                    ForEach(item.quiz.words) { word in HStack { Text(word.source).fontWeight(.bold); Spacer(); Text(word.translation).foregroundStyle(GlosifyTheme.muted) }.glosifyCard(padding: 14) }
                }.padding()
            }
        }.glosifyScreen().navigationTitle("Explore").navigationBarTitleDisplayMode(.inline)
    }
}
