import SwiftUI

enum AppTab: Hashable {
    case home, quizzes, anki, explore, library

    var title: String {
        switch self { case .home: "Home"; case .quizzes: "Quizzes"; case .anki: "Anki"; case .explore: "Explore"; case .library: "Library" }
    }
    var icon: String {
        switch self { case .home: "house"; case .quizzes: "questionmark.bubble"; case .anki: "rectangle.stack"; case .explore: "safari"; case .library: "books.vertical" }
    }
}

struct AppShellView: View {
    @Bindable var model: AppModel
    @State private var tab: AppTab = .home
    @State private var showingAssistant = false
    @State private var showingProfile = false

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            TabView(selection: $tab) {
                tabRoot(.home) { HomeView(model: model, selectedTab: $tab) }
                tabRoot(.quizzes) { QuizLibraryView(model: model) }
                tabRoot(.anki) { AnkiLibraryView(model: model) }
                tabRoot(.explore) { ExploreView(model: model) }
                tabRoot(.library) { LibraryView(model: model) }
            }
            .toolbarBackground(GlosifyTheme.surfaceLowest, for: .tabBar)
            .toolbarBackground(.visible, for: .tabBar)
            Button { showingAssistant = true } label: {
                Image(systemName: "bubble.left.and.sparkles.fill")
                    .font(.title3.bold())
                    .frame(width: 56, height: 56)
                    .foregroundStyle(GlosifyTheme.onPrimary)
                    .background(GlosifyTheme.primary)
                    .clipShape(Circle())
                    .shadow(color: GlosifyTheme.primary.opacity(0.35), radius: 16)
            }
            .padding(.trailing, 18).padding(.bottom, 72)
            .accessibilityLabel("Open study assistant")
            .accessibilityIdentifier("assistant-button")
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            AppTopBar(model: model, profileAction: { showingProfile = true })
        }
        .sheet(isPresented: $showingAssistant) { AssistantView(model: model, context: model.assistantContext) }
        .sheet(isPresented: $showingProfile) { ProfileView(model: model) }
        .onChange(of: tab) { _, newTab in model.assistantContext = newTab.title }
        .overlay {
            if model.isLoading {
                ZStack {
                    Color.black.opacity(0.28).ignoresSafeArea()
                    ProgressView("Loading mock library…").padding(20).background(GlosifyTheme.surface).clipShape(RoundedRectangle(cornerRadius: 16)).tint(GlosifyTheme.primary)
                }.accessibilityIdentifier("loading-state")
            }
        }
        .overlay(alignment: .top) {
            if let error = model.errorMessage {
                ErrorBanner(message: error).padding().onTapGesture { model.errorMessage = nil }
            }
        }
    }

    @ViewBuilder
    private func tabRoot<Content: View>(_ item: AppTab, @ViewBuilder content: () -> Content) -> some View {
        NavigationStack { content() }
            .tabItem { Label(item.title, systemImage: item.icon) }
            .tag(item)
    }
}

private struct AppTopBar: View {
    @Bindable var model: AppModel
    let profileAction: () -> Void
    var body: some View {
        HStack(spacing: 12) {
            Text("Glosify").font(GlosifyTheme.display(22)).italic().foregroundStyle(GlosifyTheme.primary)
            Spacer()
            Label("\(model.account?.credits ?? 0)", systemImage: "bolt.fill")
                .font(GlosifyTheme.body(12, weight: .bold))
                .foregroundStyle(GlosifyTheme.primary)
                .padding(.horizontal, 11).frame(height: 36)
                .background(GlosifyTheme.surfaceHigh).clipShape(Capsule())
            Button(action: profileAction) {
                Image(systemName: "person.crop.circle.fill").font(.title2).frame(width: 44, height: 44)
            }
            .accessibilityLabel("Open account")
            .accessibilityIdentifier("profile-button")
        }
        .padding(.horizontal, 16).frame(height: 54)
        .background(GlosifyTheme.surfaceLowest)
    }
}
