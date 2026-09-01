import SwiftUI

struct ProfileView: View {
    @Bindable var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var showingCredits = false
    @State private var legalPage: LegalPage?

    var filteredLanguages: [LanguageOption] {
        guard !query.isEmpty else { return LanguageCatalog.all }
        return LanguageCatalog.all.filter { $0.name.localizedCaseInsensitiveContains(query) || $0.nativeName.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    HStack(spacing: 14) {
                        Image(systemName: "person.crop.circle.fill").font(.system(size: 42)).foregroundStyle(GlosifyTheme.primary)
                        VStack(alignment: .leading) {
                            Text(model.account?.displayName ?? "Learner").font(GlosifyTheme.body(17, weight: .bold))
                            Text(model.account?.email ?? "").foregroundStyle(GlosifyTheme.muted)
                        }
                    }.listRowBackground(GlosifyTheme.surface)
                }
                Section("Learning mode") {
                    TextField("Filter languages", text: $query)
                    ForEach(filteredLanguages) { language in
                        Button {
                            Task { await model.selectLanguage(language.code) }
                        } label: {
                            HStack {
                                Text(language.flag).frame(width: 32)
                                VStack(alignment: .leading) {
                                    Text(language.name)
                                    Text(language.nativeName).font(.caption).foregroundStyle(GlosifyTheme.muted)
                                }
                                Spacer()
                                if model.account?.selectedLanguageCode == language.code { Image(systemName: "checkmark.circle.fill").foregroundStyle(GlosifyTheme.primary) }
                            }
                        }
                    }.listRowBackground(GlosifyTheme.surface)
                }
                Section("Account") {
                    Button { showingCredits = true } label: { Label("Credits and packages", systemImage: "bolt.fill") }
                    Button { legalPage = .privacy } label: { Label("Privacy", systemImage: "hand.raised") }
                    Button { legalPage = .terms } label: { Label("Terms", systemImage: "doc.text") }
                    Button { legalPage = .support } label: { Label("Support", systemImage: "questionmark.circle") }
                    Button(role: .destructive) { Task { await model.signOut(); dismiss() } } label: { Label("Sign out", systemImage: "rectangle.portrait.and.arrow.right") }
                }.listRowBackground(GlosifyTheme.surface)
            }
            .scrollContentBackground(.hidden).background(GlosifyTheme.background)
            .navigationTitle("Profile")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
            .sheet(isPresented: $showingCredits) { CreditsView(model: model) }
            .sheet(item: $legalPage) { LegalView(page: $0) }
        }.preferredColorScheme(.dark)
    }
}

private enum LegalPage: String, Identifiable { case privacy, terms, support; var id: String { rawValue } }

private struct LegalView: View {
    let page: LegalPage
    @Environment(\.dismiss) private var dismiss
    var title: String { page.rawValue.capitalized }
    var text: String {
        switch page {
        case .privacy: "This prototype stores all mock account and learning data only in memory. Imported PDFs and selected images are processed locally and are discarded when the app exits. No network request is made."
        case .terms: "This is a non-production prototype. Its translations, assistant responses, scheduling intervals, credit purchases, and account actions are simulated and must not be relied upon as a service."
        case .support: "Explore the five tabs, tap the floating assistant button for contextual help, and sign out from Profile to test authentication. Restart the app to restore the original seeded data."
        }
    }
    var body: some View {
        NavigationStack {
            ScrollView { VStack(alignment: .leading, spacing: 18) { ScreenHeader(eyebrow: "Glosify prototype", title: title, subtitle: "Mobile prototype information"); Text(text).font(GlosifyTheme.serif()).lineSpacing(8).glosifyCard() }.padding() }
                .glosifyScreen().toolbar { Button("Done") { dismiss() } }
        }.preferredColorScheme(.dark)
    }
}

struct CreditsView: View {
    @Bindable var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var purchased: CreditPackage?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
                    ScreenHeader(eyebrow: "AI credits", title: "Power your learning", subtitle: "Purchases are simulated and only change the in-memory balance.")
                    Label("\(model.account?.credits ?? 0) credits available", systemImage: "bolt.fill").font(GlosifyTheme.display(20)).foregroundStyle(GlosifyTheme.primary).glosifyCard()
                    ForEach(model.packages) { package in
                        VStack(alignment: .leading, spacing: 10) {
                            Text(package.name).font(GlosifyTheme.display(20))
                            Text("\(package.credits) credits").foregroundStyle(GlosifyTheme.muted)
                            Button("Buy for \(package.priceSEK) SEK") {
                                Task {
                                    let success = await model.perform { model.account = try await model.environment.credits.purchase(packageID: package.id) }
                                    if success { purchased = package }
                                }
                            }.buttonStyle(PrimaryButtonStyle()).accessibilityIdentifier("purchase-\(package.id)")
                        }.glosifyCard()
                    }
                }.padding().padding(.bottom, 30)
            }.glosifyScreen().navigationTitle("Credits").toolbar { Button("Done") { dismiss() } }
            .alert("Mock purchase complete", isPresented: Binding(get: { purchased != nil }, set: { if !$0 { purchased = nil } })) { Button("OK") { purchased = nil } } message: { Text("\(purchased?.credits ?? 0) credits were added for this session.") }
        }.preferredColorScheme(.dark)
    }
}
