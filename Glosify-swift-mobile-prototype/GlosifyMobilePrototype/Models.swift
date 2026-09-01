import Foundation

struct Account: Identifiable, Codable, Sendable, Equatable {
    let id: UUID
    var email: String
    var displayName: String
    var credits: Int
    var selectedLanguageCode: String
}

struct LanguageOption: Identifiable, Codable, Sendable, Hashable {
    let code: String
    let name: String
    let nativeName: String
    let flag: String
    let locale: String
    let isLanguageLearning: Bool

    var id: String { code }
}

struct VocabularyWord: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var source: String
    var translation: String
    var createdAt: Date
}

struct QuizSentence: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var source: String
    var translation: String
}

struct Quiz: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var name: String
    var sourceLanguage: String
    var targetLanguage: String
    var collectionID: UUID?
    var isPublic: Bool
    var createdAt: Date
    var words: [VocabularyWord]
    var sentences: [QuizSentence]
}

struct QuizCollection: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var name: String
    var languageCode: String
    var parentID: UUID?
    var isPublic: Bool
    var createdAt: Date
}

enum PracticeMode: String, Codable, Sendable, CaseIterable, Identifiable {
    case flashcards
    case typing
    var id: String { rawValue }
}

enum PracticeDirection: String, Codable, Sendable, CaseIterable, Identifiable {
    case sourceToTarget
    case targetToSource
    var id: String { rawValue }
    var title: String { self == .sourceToTarget ? "Source → learning" : "Learning → source" }
}

struct PracticeConfiguration: Codable, Sendable, Equatable {
    var mode: PracticeMode = .flashcards
    var direction: PracticeDirection = .sourceToTarget
    var itemCount = 10
    var includesSentences = false

    func availableItemCount(wordCount: Int, sentenceCount: Int) -> Int {
        wordCount + (includesSentences ? sentenceCount : 0)
    }

    mutating func normalizeItemCount(wordCount: Int, sentenceCount: Int) {
        let available = availableItemCount(wordCount: wordCount, sentenceCount: sentenceCount)
        itemCount = min(max(itemCount, 1), max(available, 1))
    }
}

enum PracticeScorer {
    static func matches(_ answer: String, expected: String) -> Bool {
        answer.trimmingCharacters(in: .whitespacesAndNewlines)
            .localizedCaseInsensitiveCompare(expected.trimmingCharacters(in: .whitespacesAndNewlines)) == .orderedSame
    }
}

struct AnkiCard: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var prompt: String
    var answer: String
    var promptLanguage: String
    var answerLanguage: String
    var dueAt: Date
    var intervalDays: Int
    var reviewCount: Int
}

struct AnkiCollection: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var name: String
    var sourceLanguage: String
    var targetLanguage: String
    var cards: [AnkiCard]
}

struct SharedQuiz: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var author: String
    var quiz: Quiz
    var copyCount: Int
}

struct BookPage: Identifiable, Codable, Sendable, Hashable {
    let number: Int
    var sourceText: String
    var mockTranslation: String
    var id: Int { number }
}

struct BookDocument: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var title: String
    var originalFileName: String
    var createdAt: Date
    var pageCount: Int
    var pages: [BookPage]
    var pdfData: Data?
}

struct TranscriptSegment: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var capturedAt: Date
    var sourceText: String
    var translatedText: String
}

struct Transcript: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var title: String
    var sourceLanguage: String
    var targetLanguage: String
    var stream: String
    var createdAt: Date
    var updatedAt: Date
    var segments: [TranscriptSegment]
}

enum AssistantRole: String, Codable, Sendable, Hashable {
    case user
    case assistant
}

struct PendingLibraryChange: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var summary: String
    var quizName: String
    var words: [VocabularyWord]
    var state: State

    enum State: String, Codable, Sendable, Hashable {
        case pending
        case applied
        case rejected
    }
}

struct AssistantMessage: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var role: AssistantRole
    var text: String
    var createdAt: Date
    var pendingChange: PendingLibraryChange?
    var feedback: Int?
}

struct AssistantChat: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    var title: String
    var contextLabel: String
    var messages: [AssistantMessage]
    var updatedAt: Date
}

struct CreditPackage: Identifiable, Codable, Sendable, Hashable {
    let id: String
    var name: String
    var credits: Int
    var priceSEK: Int
}

enum SeedID {
    static func uuid(_ suffix: Int) -> UUID {
        UUID(uuidString: String(format: "00000000-0000-4000-8000-%012d", suffix))!
    }
}

enum LanguageCatalog {
    static let all: [LanguageOption] = [
        .init(code: "free", name: "Freestyle", nativeName: "General subjects", flag: "✦", locale: "en-GB", isLanguageLearning: false),
        .init(code: "af", name: "Afrikaans", nativeName: "Afrikaans", flag: "🇿🇦", locale: "af-ZA", isLanguageLearning: true),
        .init(code: "ar", name: "Arabic", nativeName: "العربية", flag: "🇸🇦", locale: "ar-SA", isLanguageLearning: true),
        .init(code: "hy", name: "Armenian", nativeName: "Հայերեն", flag: "🇦🇲", locale: "hy-AM", isLanguageLearning: true),
        .init(code: "as", name: "Assamese", nativeName: "অসমীয়া", flag: "🇮🇳", locale: "as-IN", isLanguageLearning: true),
        .init(code: "az", name: "Azerbaijani", nativeName: "Azərbaycanca", flag: "🇦🇿", locale: "az-AZ", isLanguageLearning: true),
        .init(code: "bn", name: "Bangla", nativeName: "বাংলা", flag: "🇧🇩", locale: "bn-BD", isLanguageLearning: true),
        .init(code: "bs", name: "Bosnian", nativeName: "Bosanski", flag: "🇧🇦", locale: "bs-BA", isLanguageLearning: true),
        .init(code: "bg", name: "Bulgarian", nativeName: "Български", flag: "🇧🇬", locale: "bg-BG", isLanguageLearning: true),
        .init(code: "my", name: "Burmese", nativeName: "မြန်မာဘာသာ", flag: "🇲🇲", locale: "my-MM", isLanguageLearning: true),
        .init(code: "yue", name: "Cantonese", nativeName: "粵語", flag: "🇭🇰", locale: "yue-HK", isLanguageLearning: true),
        .init(code: "ca", name: "Catalan", nativeName: "Català", flag: "🇪🇸", locale: "ca-ES", isLanguageLearning: true),
        .init(code: "zh-Hans", name: "Chinese (Simplified)", nativeName: "简体中文", flag: "🇨🇳", locale: "zh-CN", isLanguageLearning: true),
        .init(code: "hr", name: "Croatian", nativeName: "Hrvatski", flag: "🇭🇷", locale: "hr-HR", isLanguageLearning: true),
        .init(code: "cs", name: "Czech", nativeName: "Čeština", flag: "🇨🇿", locale: "cs-CZ", isLanguageLearning: true),
        .init(code: "da", name: "Danish", nativeName: "Dansk", flag: "🇩🇰", locale: "da-DK", isLanguageLearning: true),
        .init(code: "nl", name: "Dutch", nativeName: "Nederlands", flag: "🇳🇱", locale: "nl-NL", isLanguageLearning: true),
        .init(code: "en", name: "English", nativeName: "English", flag: "🇬🇧", locale: "en-GB", isLanguageLearning: true),
        .init(code: "et", name: "Estonian", nativeName: "Eesti", flag: "🇪🇪", locale: "et-EE", isLanguageLearning: true),
        .init(code: "fil", name: "Filipino", nativeName: "Filipino", flag: "🇵🇭", locale: "fil-PH", isLanguageLearning: true),
        .init(code: "fi", name: "Finnish", nativeName: "Suomi", flag: "🇫🇮", locale: "fi-FI", isLanguageLearning: true),
        .init(code: "fr", name: "French", nativeName: "Français", flag: "🇫🇷", locale: "fr-FR", isLanguageLearning: true),
        .init(code: "gl", name: "Galician", nativeName: "Galego", flag: "🇪🇸", locale: "gl-ES", isLanguageLearning: true),
        .init(code: "ka", name: "Georgian", nativeName: "ქართული", flag: "🇬🇪", locale: "ka-GE", isLanguageLearning: true),
        .init(code: "de", name: "German", nativeName: "Deutsch", flag: "🇩🇪", locale: "de-DE", isLanguageLearning: true),
        .init(code: "el", name: "Greek", nativeName: "Ελληνικά", flag: "🇬🇷", locale: "el-GR", isLanguageLearning: true),
        .init(code: "gu", name: "Gujarati", nativeName: "ગુજરાતી", flag: "🇮🇳", locale: "gu-IN", isLanguageLearning: true),
        .init(code: "ha", name: "Hausa", nativeName: "Hausa", flag: "🇳🇬", locale: "ha-NG", isLanguageLearning: true),
        .init(code: "he", name: "Hebrew", nativeName: "עברית", flag: "🇮🇱", locale: "he-IL", isLanguageLearning: true),
        .init(code: "hi", name: "Hindi", nativeName: "हिन्दी", flag: "🇮🇳", locale: "hi-IN", isLanguageLearning: true),
        .init(code: "hu", name: "Hungarian", nativeName: "Magyar", flag: "🇭🇺", locale: "hu-HU", isLanguageLearning: true),
        .init(code: "is", name: "Icelandic", nativeName: "Íslenska", flag: "🇮🇸", locale: "is-IS", isLanguageLearning: true),
        .init(code: "id", name: "Indonesian", nativeName: "Bahasa Indonesia", flag: "🇮🇩", locale: "id-ID", isLanguageLearning: true),
        .init(code: "it", name: "Italian", nativeName: "Italiano", flag: "🇮🇹", locale: "it-IT", isLanguageLearning: true),
        .init(code: "ja", name: "Japanese", nativeName: "日本語", flag: "🇯🇵", locale: "ja-JP", isLanguageLearning: true),
        .init(code: "kn", name: "Kannada", nativeName: "ಕನ್ನಡ", flag: "🇮🇳", locale: "kn-IN", isLanguageLearning: true),
        .init(code: "kk", name: "Kazakh", nativeName: "Қазақша", flag: "🇰🇿", locale: "kk-KZ", isLanguageLearning: true),
        .init(code: "ko", name: "Korean", nativeName: "한국어", flag: "🇰🇷", locale: "ko-KR", isLanguageLearning: true),
        .init(code: "ky", name: "Kyrgyz", nativeName: "Кыргызча", flag: "🇰🇬", locale: "ky-KG", isLanguageLearning: true),
        .init(code: "lv", name: "Latvian", nativeName: "Latviešu", flag: "🇱🇻", locale: "lv-LV", isLanguageLearning: true),
        .init(code: "lt", name: "Lithuanian", nativeName: "Lietuvių", flag: "🇱🇹", locale: "lt-LT", isLanguageLearning: true),
        .init(code: "mk", name: "Macedonian", nativeName: "Македонски", flag: "🇲🇰", locale: "mk-MK", isLanguageLearning: true),
        .init(code: "ms", name: "Malay", nativeName: "Bahasa Melayu", flag: "🇲🇾", locale: "ms-MY", isLanguageLearning: true),
        .init(code: "ml", name: "Malayalam", nativeName: "മലയാളം", flag: "🇮🇳", locale: "ml-IN", isLanguageLearning: true),
        .init(code: "mt", name: "Maltese", nativeName: "Malti", flag: "🇲🇹", locale: "mt-MT", isLanguageLearning: true),
        .init(code: "mi", name: "Māori", nativeName: "Māori", flag: "🇳🇿", locale: "mi-NZ", isLanguageLearning: true),
        .init(code: "mr", name: "Marathi", nativeName: "मराठी", flag: "🇮🇳", locale: "mr-IN", isLanguageLearning: true),
        .init(code: "ne", name: "Nepali", nativeName: "नेपाली", flag: "🇳🇵", locale: "ne-NP", isLanguageLearning: true),
        .init(code: "nb", name: "Norwegian", nativeName: "Norsk bokmål", flag: "🇳🇴", locale: "nb-NO", isLanguageLearning: true),
        .init(code: "or", name: "Odia", nativeName: "ଓଡ଼ିଆ", flag: "🇮🇳", locale: "or-IN", isLanguageLearning: true),
        .init(code: "fa", name: "Persian", nativeName: "فارسی", flag: "🇮🇷", locale: "fa-IR", isLanguageLearning: true),
        .init(code: "pl", name: "Polish", nativeName: "Polski", flag: "🇵🇱", locale: "pl-PL", isLanguageLearning: true),
        .init(code: "pt", name: "Portuguese (Brazil)", nativeName: "Português (Brasil)", flag: "🇧🇷", locale: "pt-BR", isLanguageLearning: true),
        .init(code: "pa", name: "Punjabi", nativeName: "ਪੰਜਾਬੀ", flag: "🇮🇳", locale: "pa-IN", isLanguageLearning: true),
        .init(code: "ro", name: "Romanian", nativeName: "Română", flag: "🇷🇴", locale: "ro-RO", isLanguageLearning: true),
        .init(code: "ru", name: "Russian", nativeName: "Русский", flag: "🇷🇺", locale: "ru-RU", isLanguageLearning: true),
        .init(code: "sr-Latn", name: "Serbian (Latin)", nativeName: "Srpski (latinica)", flag: "🇷🇸", locale: "sr-Latn-RS", isLanguageLearning: true),
        .init(code: "sk", name: "Slovak", nativeName: "Slovenčina", flag: "🇸🇰", locale: "sk-SK", isLanguageLearning: true),
        .init(code: "sl", name: "Slovenian", nativeName: "Slovenščina", flag: "🇸🇮", locale: "sl-SI", isLanguageLearning: true),
        .init(code: "es", name: "Spanish", nativeName: "Español", flag: "🇪🇸", locale: "es-ES", isLanguageLearning: true),
        .init(code: "sw", name: "Swahili", nativeName: "Kiswahili", flag: "🇹🇿", locale: "sw-TZ", isLanguageLearning: true),
        .init(code: "sv", name: "Swedish", nativeName: "Svenska", flag: "🇸🇪", locale: "sv-SE", isLanguageLearning: true),
        .init(code: "ta", name: "Tamil", nativeName: "தமிழ்", flag: "🇮🇳", locale: "ta-IN", isLanguageLearning: true),
        .init(code: "te", name: "Telugu", nativeName: "తెలుగు", flag: "🇮🇳", locale: "te-IN", isLanguageLearning: true),
        .init(code: "th", name: "Thai", nativeName: "ไทย", flag: "🇹🇭", locale: "th-TH", isLanguageLearning: true),
        .init(code: "tr", name: "Turkish", nativeName: "Türkçe", flag: "🇹🇷", locale: "tr-TR", isLanguageLearning: true),
        .init(code: "uk", name: "Ukrainian", nativeName: "Українська", flag: "🇺🇦", locale: "uk-UA", isLanguageLearning: true),
        .init(code: "uz", name: "Uzbek", nativeName: "Oʻzbekcha", flag: "🇺🇿", locale: "uz-UZ", isLanguageLearning: true),
        .init(code: "vi", name: "Vietnamese", nativeName: "Tiếng Việt", flag: "🇻🇳", locale: "vi-VN", isLanguageLearning: true),
        .init(code: "cy", name: "Welsh", nativeName: "Cymraeg", flag: "🇬🇧", locale: "cy-GB", isLanguageLearning: true)
    ]

    static func find(_ code: String) -> LanguageOption {
        all.first(where: { $0.code == code }) ?? all.first(where: { $0.code == "pl" })!
    }
}
