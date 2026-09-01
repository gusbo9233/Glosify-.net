import SwiftUI

@main
struct GlosifyMobilePrototypeApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView(model: model)
                .preferredColorScheme(.dark)
                .tint(GlosifyTheme.primary)
        }
    }
}

struct RootView: View {
    @Bindable var model: AppModel

    var body: some View {
        Group {
            if model.account == nil {
                AuthView(model: model)
            } else {
                AppShellView(model: model)
            }
        }
        .font(GlosifyTheme.body())
        .task { await model.loadAll() }
        .alert("Glosify", isPresented: Binding(get: { model.notice != nil }, set: { if !$0 { model.notice = nil } })) {
            Button("OK", role: .cancel) { model.notice = nil }
        } message: {
            Text(model.notice ?? "")
        }
    }
}

#Preview("Signed-in app") {
    RootView(model: AppModel(environment: .prototype(configuration: .immediate)))
        .preferredColorScheme(.dark)
}

#Preview("Empty library") {
    RootView(model: AppModel(environment: .prototype(configuration: MockConfiguration(latency: .zero, startsEmpty: true))))
        .preferredColorScheme(.dark)
}

#Preview("Compact iPhone") {
    RootView(model: AppModel(environment: .prototype(configuration: .immediate)))
        .preferredColorScheme(.dark)
        .previewLayout(.fixed(width: 320, height: 568))
}
