import SwiftUI

enum GlosifyTheme {
    static let background = Color(hex: 0x041329)
    static let surfaceLowest = Color(hex: 0x010E24)
    static let surfaceLow = Color(hex: 0x0D1C32)
    static let surface = Color(hex: 0x112036)
    static let surfaceHigh = Color(hex: 0x1C2A41)
    static let surfaceHighest = Color(hex: 0x27354C)
    static let primary = Color(hex: 0x53E076)
    static let primaryContainer = Color(hex: 0x1DB954)
    static let onPrimary = Color(hex: 0x003914)
    static let text = Color(hex: 0xD6E3FF)
    static let muted = Color(hex: 0xBCCBB9)
    static let outline = Color(hex: 0x3D4A3D)
    static let error = Color(hex: 0xFFB4AB)

    static func display(_ size: CGFloat, weight: Font.Weight = .bold) -> Font {
        .custom("Plus Jakarta Sans", size: size, relativeTo: size >= 28 ? .largeTitle : .title2).weight(weight)
    }

    static func body(_ size: CGFloat = 16, weight: Font.Weight = .regular) -> Font {
        .custom("Plus Jakarta Sans", size: size, relativeTo: .body).weight(weight)
    }

    static func serif(_ size: CGFloat = 18) -> Font {
        .custom("Lora", size: size, relativeTo: .body)
    }
}

extension Color {
    init(hex: UInt, alpha: Double = 1) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xff) / 255,
            green: Double((hex >> 8) & 0xff) / 255,
            blue: Double(hex & 0xff) / 255,
            opacity: alpha
        )
    }
}

struct GlosifyCardModifier: ViewModifier {
    var padding: CGFloat = 18
    func body(content: Content) -> some View {
        content
            .padding(padding)
            .background(GlosifyTheme.surface)
            .clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 20, style: .continuous).stroke(GlosifyTheme.outline.opacity(0.65)))
            .shadow(color: .black.opacity(0.28), radius: 18, y: 9)
    }
}

extension View {
    func glosifyCard(padding: CGFloat = 18) -> some View { modifier(GlosifyCardModifier(padding: padding)) }
    func glosifyScreen() -> some View {
        background(GlosifyTheme.background.ignoresSafeArea()).foregroundStyle(GlosifyTheme.text)
    }
}

struct PrimaryButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(GlosifyTheme.body(16, weight: .bold))
            .frame(maxWidth: .infinity, minHeight: 48)
            .padding(.horizontal, 16)
            .foregroundStyle(GlosifyTheme.onPrimary)
            .background(GlosifyTheme.primary.opacity(configuration.isPressed ? 0.72 : 1))
            .clipShape(Capsule())
            .shadow(color: GlosifyTheme.primary.opacity(0.28), radius: 14)
            .scaleEffect(configuration.isPressed ? 0.98 : 1)
    }
}

struct SecondaryButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(GlosifyTheme.body(15, weight: .semibold))
            .frame(minHeight: 44)
            .padding(.horizontal, 16)
            .foregroundStyle(configuration.isPressed ? GlosifyTheme.primary : GlosifyTheme.text)
            .background(GlosifyTheme.surfaceHigh)
            .clipShape(Capsule())
            .overlay(Capsule().stroke(GlosifyTheme.outline))
    }
}

struct Eyebrow: View {
    let text: String
    var body: some View {
        Text(text.uppercased())
            .font(GlosifyTheme.body(11, weight: .bold))
            .tracking(1.8)
            .foregroundStyle(GlosifyTheme.primary)
    }
}

struct ScreenHeader: View {
    let eyebrow: String
    let title: String
    let subtitle: String
    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            Eyebrow(text: eyebrow)
            Text(title).font(GlosifyTheme.display(34)).foregroundStyle(GlosifyTheme.text)
            Text(subtitle).font(GlosifyTheme.body()).foregroundStyle(GlosifyTheme.muted)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct EmptyState: View {
    let icon: String
    let title: String
    let message: String
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: icon).font(.system(size: 34)).foregroundStyle(GlosifyTheme.primary)
            Text(title).font(GlosifyTheme.display(20))
            Text(message).font(GlosifyTheme.body(14)).foregroundStyle(GlosifyTheme.muted).multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .glosifyCard()
    }
}

struct ErrorBanner: View {
    let message: String
    var body: some View {
        Label(message, systemImage: "exclamationmark.triangle.fill")
            .font(GlosifyTheme.body(14, weight: .semibold))
            .foregroundStyle(GlosifyTheme.error)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(14)
            .background(GlosifyTheme.error.opacity(0.12))
            .clipShape(RoundedRectangle(cornerRadius: 14))
            .accessibilityIdentifier("error-banner")
    }
}
