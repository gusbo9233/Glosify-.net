import SwiftUI

struct HomeView: View {
    @Bindable var model: AppModel
    @Binding var selectedTab: AppTab

    var body: some View {
        ScrollView {
            VStack(spacing: 22) {
                hero
                VStack(alignment: .leading, spacing: 14) {
                    Eyebrow(text: "Quick start")
                    Text("Choose your next move").font(GlosifyTheme.display(24))
                    actionCard(number: "01", icon: "square.stack.3d.up.fill", title: "Build vocabulary", text: "Create and organize words in focused quizzes.", tab: .quizzes)
                    actionCard(number: "02", icon: "brain.head.profile", title: "Practice recall", text: "Use flashcards or typing in either direction.", tab: .quizzes)
                    actionCard(number: "03", icon: "rectangle.stack.fill", title: "Review with Anki", text: "Return to cards when your mock schedule says they are due.", tab: .anki)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                connected
            }
            .padding(16).padding(.bottom, 90)
        }
        .glosifyScreen()
        .navigationBarHidden(true)
        .accessibilityIdentifier("home-screen")
    }

    private var hero: some View {
        VStack(alignment: .leading, spacing: 16) {
            Eyebrow(text: "Your learning loop")
            Text(model.selectedLanguage.isLanguageLearning ? "Learn \(model.selectedLanguage.name) with context" : "Study anything with Freestyle")
                .font(GlosifyTheme.display(34))
            Text(model.selectedLanguage.isLanguageLearning ? "Read, collect useful language, practise recall, and build durable memory." : "Build question banks and interactive exercises for any subject.")
                .foregroundStyle(GlosifyTheme.muted)
            HStack(spacing: 10) {
                Text(model.selectedLanguage.flag).font(.title2)
                VStack(alignment: .leading) {
                    Text(model.selectedLanguage.name).font(GlosifyTheme.body(15, weight: .bold))
                    Text("Active learning mode").font(GlosifyTheme.body(11)).foregroundStyle(GlosifyTheme.muted)
                }
                Spacer()
                Image(systemName: "arrow.triangle.2.circlepath").foregroundStyle(GlosifyTheme.primary)
            }
            .padding(14).background(GlosifyTheme.surfaceHigh).clipShape(RoundedRectangle(cornerRadius: 16))
            Button { selectedTab = .quizzes } label: { Label("Open your quizzes", systemImage: "arrow.right") }.buttonStyle(PrimaryButtonStyle())
        }
        .glosifyCard()
        .background(alignment: .topTrailing) { Circle().fill(GlosifyTheme.primary.opacity(0.09)).frame(width: 180).blur(radius: 20).offset(x: 55, y: -45) }
    }

    private func actionCard(number: String, icon: String, title: String, text: String, tab: AppTab) -> some View {
        Button { selectedTab = tab } label: {
            HStack(spacing: 14) {
                Text(number).font(GlosifyTheme.body(11, weight: .bold)).foregroundStyle(GlosifyTheme.primary)
                Image(systemName: icon).font(.title2).foregroundStyle(GlosifyTheme.primary).frame(width: 38)
                VStack(alignment: .leading, spacing: 4) {
                    Text(title).font(GlosifyTheme.body(16, weight: .bold)).foregroundStyle(GlosifyTheme.text)
                    Text(text).font(GlosifyTheme.body(12)).foregroundStyle(GlosifyTheme.muted).multilineTextAlignment(.leading)
                }
                Spacer()
                Image(systemName: "arrow.up.right").foregroundStyle(GlosifyTheme.primary)
            }.glosifyCard(padding: 15)
        }.buttonStyle(.plain)
    }

    private var connected: some View {
        VStack(alignment: .leading, spacing: 14) {
            Eyebrow(text: "Connected learning")
            Text("Everything becomes study material").font(GlosifyTheme.display(24))
            Text("Copy a community quiz, read a PDF, or revisit a saved transcript—then bring that context to the assistant.")
                .foregroundStyle(GlosifyTheme.muted)
            Button { selectedTab = .explore } label: { Label("Explore community material", systemImage: "safari") }.buttonStyle(SecondaryButtonStyle())
        }.glosifyCard()
    }
}
