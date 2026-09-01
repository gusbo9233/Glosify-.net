import SwiftUI

struct AuthView: View {
    enum Mode: String, CaseIterable, Identifiable { case signIn = "Sign in"; case register = "Register"; var id: String { rawValue } }
    @Bindable var model: AppModel
    @State private var mode: Mode = .signIn
    @State private var name = "Demo Learner"
    @State private var email = "learner@glosify.se"
    @State private var password = "prototype"
    @State private var showingReset = false
    @State private var isSubmitting = false

    var body: some View {
        ZStack {
            GlosifyTheme.background.ignoresSafeArea()
            Circle().fill(GlosifyTheme.primary.opacity(0.1)).frame(width: 340).blur(radius: 50).offset(x: -160, y: -330)
            ScrollView {
                VStack(spacing: 26) {
                    Spacer(minLength: 45)
                    VStack(spacing: 8) {
                        Text("Glosify").font(GlosifyTheme.display(40)).italic().foregroundStyle(GlosifyTheme.primary)
                        Text("Learn in context. Remember for longer.").font(GlosifyTheme.body()).foregroundStyle(GlosifyTheme.muted)
                    }
                    VStack(spacing: 18) {
                        Picker("Account action", selection: $mode) {
                            ForEach(Mode.allCases) { Text($0.rawValue).tag($0) }
                        }
                        .pickerStyle(.segmented)
                        if mode == .register { TextField("Name", text: $name).textContentType(.name).glosifyField() }
                        TextField("Email", text: $email).textContentType(.emailAddress).keyboardType(.emailAddress).textInputAutocapitalization(.never).glosifyField()
                        SecureField("Password", text: $password).textContentType(mode == .signIn ? .password : .newPassword).glosifyField()
                        if let error = model.errorMessage { ErrorBanner(message: error) }
                        Button {
                            Task {
                                isSubmitting = true
                                if mode == .signIn { _ = await model.signIn(email: email, password: password) }
                                else { _ = await model.register(name: name, email: email, password: password) }
                                isSubmitting = false
                            }
                        } label: {
                            if isSubmitting { ProgressView().tint(GlosifyTheme.onPrimary) }
                            else { Label(mode.rawValue, systemImage: "arrow.right") }
                        }
                        .buttonStyle(PrimaryButtonStyle())
                        .disabled(isSubmitting)
                        .accessibilityIdentifier("auth-submit")
                        Button("Forgot password?") { showingReset = true }.foregroundStyle(GlosifyTheme.primary)
                    }
                    .glosifyCard()
                    Text("Prototype credentials are never sent anywhere. Any valid email and a six-character password work.")
                        .font(GlosifyTheme.body(12)).foregroundStyle(GlosifyTheme.muted).multilineTextAlignment(.center)
                }
                .padding(22)
            }
        }
        .sheet(isPresented: $showingReset) { PasswordResetView(model: model, email: $email) }
    }
}

private struct PasswordResetView: View {
    @Bindable var model: AppModel
    @Binding var email: String
    @Environment(\.dismiss) private var dismiss
    @State private var sent = false

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 18) {
                ScreenHeader(eyebrow: "Mock account", title: "Reset password", subtitle: "We simulate the request without sending email.")
                TextField("Email", text: $email).keyboardType(.emailAddress).glosifyField()
                if let error = model.errorMessage { ErrorBanner(message: error) }
                if sent { Label("Mock reset instructions prepared.", systemImage: "checkmark.circle.fill").foregroundStyle(GlosifyTheme.primary) }
                Button("Request reset") {
                    Task {
                        sent = await model.perform { try await model.environment.authentication.requestPasswordReset(email: email) }
                    }
                }.buttonStyle(PrimaryButtonStyle())
                Spacer()
            }
            .padding(20).glosifyScreen()
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        }.preferredColorScheme(.dark)
    }
}

private extension View {
    func glosifyField() -> some View {
        self
            .padding(.horizontal, 14)
            .frame(minHeight: 50)
            .background(GlosifyTheme.surfaceLow)
            .clipShape(RoundedRectangle(cornerRadius: 13))
            .overlay(RoundedRectangle(cornerRadius: 13).stroke(GlosifyTheme.outline))
    }
}
