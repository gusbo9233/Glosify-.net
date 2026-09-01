import SwiftUI

struct AnkiLibraryView: View {
    @Bindable var model: AppModel
    @State private var showingCreate = false

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                ScreenHeader(eyebrow: "Tracked flashcards", title: "Anki collections", subtitle: "Build lasting recall with deterministic prototype scheduling.")
                Button { showingCreate = true } label: { Label("New collection", systemImage: "plus") }.buttonStyle(PrimaryButtonStyle())
                if model.ankiCollections.isEmpty { EmptyState(icon: "rectangle.stack.badge.plus", title: "Your first collection starts here", message: "Create a study space and add a whole quiz.") }
                ForEach(model.ankiCollections) { collection in
                    NavigationLink { AnkiCollectionView(model: model, collectionID: collection.id) } label: { collectionCard(collection) }.buttonStyle(.plain)
                }
            }.padding(16).padding(.bottom, 90)
        }.glosifyScreen().navigationBarHidden(true).sheet(isPresented: $showingCreate) { CreateAnkiCollectionView(model: model) }.accessibilityIdentifier("anki-library-screen")
    }

    private func collectionCard(_ collection: AnkiCollection) -> some View {
        let due = collection.cards.filter { $0.dueAt <= Date() }.count
        let new = collection.cards.filter { $0.reviewCount == 0 }.count
        return VStack(alignment: .leading, spacing: 14) {
            HStack { Image(systemName: "rectangle.stack.fill").foregroundStyle(GlosifyTheme.primary); Text("\(collection.sourceLanguage) → \(collection.targetLanguage)").font(.caption.bold()).foregroundStyle(GlosifyTheme.muted); Spacer(); Image(systemName: "arrow.right") }
            Text(collection.name).font(GlosifyTheme.display(21))
            HStack { stat("Due", due); stat("New", new); stat("Total", collection.cards.count) }
        }.glosifyCard()
    }

    private func stat(_ label: String, _ value: Int) -> some View {
        VStack { Text("\(value)").font(GlosifyTheme.display(18)).foregroundStyle(GlosifyTheme.primary); Text(label).font(.caption).foregroundStyle(GlosifyTheme.muted) }.frame(maxWidth: .infinity).padding(8).background(GlosifyTheme.surfaceHigh).clipShape(RoundedRectangle(cornerRadius: 12))
    }
}

private struct CreateAnkiCollectionView: View {
    @Bindable var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var source = "English"
    var body: some View {
        FormSheet(title: "Create Anki collection", subtitle: "The learning language follows your active mode.") {
            TextField("Collection name", text: $name)
            TextField("Source language", text: $source)
            Button("Create") { Task { let ok = await model.perform { let created = try await model.environment.anki.createAnkiCollection(name: name, sourceLanguage: source, targetLanguage: model.selectedLanguage.name); model.ankiCollections.append(created) }; if ok { dismiss() } } }.buttonStyle(PrimaryButtonStyle())
        }
    }
}

private struct AnkiCollectionView: View {
    @Bindable var model: AppModel
    let collectionID: UUID
    @State private var showingStudy = false
    @State private var showingQuizPicker = false
    private var collection: AnkiCollection? { model.ankiCollections.first { $0.id == collectionID } }

    var body: some View {
        ScrollView {
            if let collection {
                VStack(spacing: 18) {
                    ScreenHeader(eyebrow: "\(collection.sourceLanguage) → \(collection.targetLanguage)", title: collection.name, subtitle: "\(collection.cards.count) tracked cards · mock intervals update as you review.")
                    Button { showingStudy = true } label: { Label("Study due cards", systemImage: "play.fill") }.buttonStyle(PrimaryButtonStyle()).disabled(collection.cards.isEmpty)
                    Button { showingQuizPicker = true } label: { Label("Add a quiz", systemImage: "square.stack.3d.up.badge.plus") }.buttonStyle(SecondaryButtonStyle())
                    stats(collection)
                    ForEach(collection.cards) { card in
                        HStack { VStack(alignment: .leading) { Text(card.prompt).font(GlosifyTheme.body(16, weight: .bold)); Text(card.answer).foregroundStyle(GlosifyTheme.muted) }; Spacer(); VStack(alignment: .trailing) { Text(card.dueAt <= Date() ? "Due" : "In \(max(card.intervalDays, 1))d").foregroundStyle(card.dueAt <= Date() ? GlosifyTheme.primary : GlosifyTheme.muted); Text("\(card.reviewCount) reviews").font(.caption2).foregroundStyle(GlosifyTheme.muted) } }.glosifyCard(padding: 13)
                    }
                }.padding().padding(.bottom, 40)
            }
        }.glosifyScreen().navigationTitle("Anki").navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $showingStudy) { AnkiStudyView(model: model, collectionID: collectionID) }
        .sheet(isPresented: $showingQuizPicker) { AnkiQuizPicker(model: model, collectionID: collectionID) }
        .onAppear { model.assistantContext = collection?.name ?? "Anki" }
        .onDisappear { model.assistantContext = "Anki" }
    }

    private func stats(_ collection: AnkiCollection) -> some View {
        let reviewed = collection.cards.reduce(0) { $0 + $1.reviewCount }
        return VStack(alignment: .leading, spacing: 12) {
            Text("Review activity").font(GlosifyTheme.display(19))
            HStack(alignment: .bottom, spacing: 7) {
                ForEach(0..<7, id: \.self) { index in RoundedRectangle(cornerRadius: 5).fill(index < min(reviewed, 7) ? GlosifyTheme.primary : GlosifyTheme.surfaceHighest).frame(height: CGFloat(20 + index * 6)) }
            }.frame(maxWidth: .infinity)
            Text("Prototype statistics are derived from this session's ratings.").font(.caption).foregroundStyle(GlosifyTheme.muted)
        }.glosifyCard()
    }
}

private struct AnkiQuizPicker: View {
    @Bindable var model: AppModel
    let collectionID: UUID
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            List(model.quizzes) { quiz in
                Button {
                    Task {
                        let ok = await model.perform {
                            let updated = try await model.environment.anki.addQuiz(quiz.id, to: collectionID)
                            replace(updated)
                        }
                        if ok { dismiss() }
                    }
                } label: { VStack(alignment: .leading) { Text(quiz.name); Text("\(quiz.words.count) cards").font(.caption).foregroundStyle(GlosifyTheme.muted) } }
            }.scrollContentBackground(.hidden).background(GlosifyTheme.background).navigationTitle("Add quiz").toolbar { Button("Cancel") { dismiss() } }
        }.preferredColorScheme(.dark)
    }
    private func replace(_ value: AnkiCollection) { if let index = model.ankiCollections.firstIndex(where: { $0.id == value.id }) { model.ankiCollections[index] = value } }
}

private struct AnkiStudyView: View {
    @Bindable var model: AppModel
    let collectionID: UUID
    @Environment(\.dismiss) private var dismiss
    @State private var queue: [AnkiCard] = []
    @State private var index = 0
    @State private var revealed = false
    private var collection: AnkiCollection? { model.ankiCollections.first { $0.id == collectionID } }

    var body: some View {
        NavigationStack {
            VStack(spacing: 20) {
                if index >= queue.count {
                    Spacer(); Image(systemName: "checkmark.circle.fill").font(.system(size: 60)).foregroundStyle(GlosifyTheme.primary); Eyebrow(text: "Session complete"); Text("You’re done for now").font(GlosifyTheme.display(28)); Text("No active cards are waiting in this mock session.").foregroundStyle(GlosifyTheme.muted).multilineTextAlignment(.center); Button("Back to collection") { dismiss() }.buttonStyle(PrimaryButtonStyle()); Spacer()
                } else {
                    HStack { Text(collection?.name ?? "Anki"); Spacer(); Text("\(queue.count - index) remaining") }.font(.caption.bold()).foregroundStyle(GlosifyTheme.muted)
                    Spacer()
                    let card = queue[index]
                    VStack(spacing: 18) {
                        Eyebrow(text: "\(card.promptLanguage) · card")
                        HStack { Text(card.prompt).font(GlosifyTheme.display(31)); Button { model.speech.speak(card.prompt, locale: LanguageCatalog.all.first(where: { $0.name == card.promptLanguage || $0.code == card.promptLanguage })?.locale ?? "en-GB") } label: { Image(systemName: "speaker.wave.2") } }
                        if revealed {
                            Divider(); Text(card.answer).font(GlosifyTheme.display(25)).foregroundStyle(GlosifyTheme.primary)
                            Text("How well did you remember it?").foregroundStyle(GlosifyTheme.muted)
                            VStack(spacing: 8) { rating("Again", 1, "now"); rating("Hard", 2, "1d"); rating("Good", 3, "3d"); rating("Easy", 4, "7d") }
                        } else { Button("Show answer") { revealed = true }.buttonStyle(PrimaryButtonStyle()).accessibilityIdentifier("anki-reveal") }
                    }.glosifyCard()
                    Spacer()
                }
            }.padding().glosifyScreen().toolbar { ToolbarItem(placement: .cancellationAction) { Button("Close") { dismiss() } } }.onAppear {
                guard let collection else { return }
                queue = collection.cards.filter { $0.dueAt <= Date() || $0.reviewCount == 0 }
                if queue.isEmpty { queue = Array(collection.cards.prefix(3)) }
            }
        }.preferredColorScheme(.dark)
    }

    private func rating(_ title: String, _ value: Int, _ interval: String) -> some View {
        Button {
            let card = queue[index]
            Task {
                _ = await model.perform {
                    let updated = try await model.environment.anki.rateCard(collectionID: collectionID, cardID: card.id, rating: value)
                    if let position = model.ankiCollections.firstIndex(where: { $0.id == updated.id }) { model.ankiCollections[position] = updated }
                }
                index += 1; revealed = false
            }
        } label: { HStack { Text(title).fontWeight(.bold); Spacer(); Text(interval).foregroundStyle(GlosifyTheme.muted) } }.buttonStyle(SecondaryButtonStyle())
    }
}
