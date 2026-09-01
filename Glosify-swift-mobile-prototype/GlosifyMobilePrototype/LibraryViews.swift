import SwiftUI
import UniformTypeIdentifiers

struct LibraryView: View {
    @Bindable var model: AppModel
    @State private var section = 0
    var body: some View {
        VStack(spacing: 14) {
            Picker("Library section", selection: $section) { Text("Books").tag(0); Text("Transcripts").tag(1) }.pickerStyle(.segmented).padding(.horizontal).padding(.top, 12)
            if section == 0 { BookLibraryView(model: model) } else { TranscriptLibraryView(model: model) }
        }.glosifyScreen().navigationBarHidden(true).accessibilityIdentifier("library-screen")
    }
}

private struct BookLibraryView: View {
    @Bindable var model: AppModel
    @State private var importing = false
    @State private var isWorking = false

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                ScreenHeader(eyebrow: "Reading library", title: "Study books", subtitle: "Import a local PDF, read it natively, and explore mock translation.")
                Button { importing = true } label: { Label("Import PDF", systemImage: "doc.badge.plus") }.buttonStyle(PrimaryButtonStyle())
                if isWorking { ProgressView("Reading PDF…").tint(GlosifyTheme.primary) }
                if model.books.isEmpty { EmptyState(icon: "books.vertical", title: "Your shelf is empty", message: "Choose a PDF from Files to add it for this session.") }
                ForEach(model.books) { book in
                    NavigationLink { BookReaderView(model: model, bookID: book.id) } label: {
                        HStack(spacing: 14) { Image(systemName: "book.closed.fill").font(.title2).foregroundStyle(GlosifyTheme.primary); VStack(alignment: .leading, spacing: 5) { Text(book.title).font(GlosifyTheme.body(17, weight: .bold)); Text("PDF · \(book.pageCount) pages").font(.caption).foregroundStyle(GlosifyTheme.muted); Text(book.originalFileName).font(.caption2).foregroundStyle(GlosifyTheme.muted) }; Spacer(); Image(systemName: "arrow.right") }.glosifyCard(padding: 14)
                    }.buttonStyle(.plain)
                }
            }.padding().padding(.bottom, 90)
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.pdf]) { result in
            Task { await importFile(result) }
        }
    }

    private func importFile(_ result: Result<URL, Error>) async {
        isWorking = true; defer { isWorking = false }
        do {
            let url = try result.get()
            let didAccess = url.startAccessingSecurityScopedResource()
            defer { if didAccess { url.stopAccessingSecurityScopedResource() } }
            let data = try Data(contentsOf: url)
            let texts = try model.environment.pdf.pageTexts(from: data)
            let title = url.deletingPathExtension().lastPathComponent.replacingOccurrences(of: "-", with: " ")
            let book = try await model.environment.books.importBook(title: title, fileName: url.lastPathComponent, data: data, pageTexts: texts)
            model.books.append(book)
            model.notice = "Imported \(book.title)."
        } catch { model.show(error) }
    }
}

private struct BookReaderView: View {
    @Bindable var model: AppModel
    let bookID: UUID
    @Environment(\.dismiss) private var dismiss
    @State private var page = 0
    @State private var showingTranslation = false
    private var book: BookDocument? { model.books.first { $0.id == bookID } }

    var body: some View {
        VStack(spacing: 12) {
            if let book {
                HStack { Button { page = max(0, page - 1) } label: { Image(systemName: "chevron.left").frame(width: 44, height: 44) }.disabled(page == 0); Spacer(); Text("Page \(page + 1) of \(book.pageCount)").font(.caption.bold()); Spacer(); Button { page = min(book.pageCount - 1, page + 1) } label: { Image(systemName: "chevron.right").frame(width: 44, height: 44) }.disabled(page >= book.pageCount - 1) }
                Picker("Reader mode", selection: $showingTranslation) { Text("Original").tag(false); Text("Translation").tag(true) }.pickerStyle(.segmented)
                Group {
                    if showingTranslation {
                        ScrollView { Text(book.pages.indices.contains(page) ? book.pages[page].mockTranslation : "[Mock translation for this PDF page]").font(GlosifyTheme.serif(18)).lineSpacing(7).frame(maxWidth: .infinity, alignment: .leading).padding(22) }.background(Color.white).foregroundStyle(Color(hex: 0x182235)).clipShape(RoundedRectangle(cornerRadius: 16))
                    } else if let data = book.pdfData {
                        PDFKitView(data: data, pageIndex: page).clipShape(RoundedRectangle(cornerRadius: 16))
                    } else {
                        ScrollView { Text(book.pages.indices.contains(page) ? book.pages[page].sourceText : "").font(GlosifyTheme.serif(18)).lineSpacing(7).frame(maxWidth: .infinity, alignment: .leading).padding(22) }.background(Color.white).foregroundStyle(Color(hex: 0x182235)).clipShape(RoundedRectangle(cornerRadius: 16))
                    }
                }
                HStack {
                    Button { let text = book.pages.indices.contains(page) ? (showingTranslation ? book.pages[page].mockTranslation : book.pages[page].sourceText) : book.title; model.speech.speak(text, locale: showingTranslation ? "en-GB" : model.selectedLanguage.locale) } label: { Label("Read aloud", systemImage: "speaker.wave.2.fill") }.buttonStyle(SecondaryButtonStyle())
                    Button(role: .destructive) { Task { let ok = await model.perform { try await model.environment.books.deleteBook(id: book.id); model.books = try await model.environment.books.books() }; if ok { model.notice = "Book removed."; dismiss() } } } label: { Image(systemName: "trash").frame(width: 44, height: 44) }
                }
            }
        }.padding().glosifyScreen().navigationTitle(book?.title ?? "Reader").navigationBarTitleDisplayMode(.inline)
            .onAppear { model.assistantContext = book?.title ?? "Books" }
            .onDisappear { model.assistantContext = "Library" }
    }
}

private struct TranscriptLibraryView: View {
    @Bindable var model: AppModel
    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                ScreenHeader(eyebrow: "Live subtitle history", title: "Saved transcripts", subtitle: "Review seeded caption streams. Live Chrome capture is outside this prototype.")
                if model.transcripts.isEmpty { EmptyState(icon: "captions.bubble", title: "No saved transcripts", message: "Seeded transcripts return when the app restarts.") }
                ForEach(model.transcripts) { transcript in
                    NavigationLink { TranscriptDetailView(model: model, transcriptID: transcript.id) } label: {
                        HStack(spacing: 14) { Image(systemName: "captions.bubble.fill").font(.title2).foregroundStyle(GlosifyTheme.primary); VStack(alignment: .leading, spacing: 5) { Text(transcript.title).font(GlosifyTheme.body(17, weight: .bold)); Text("\(transcript.segments.count) captions · \(transcript.stream)").font(.caption).foregroundStyle(GlosifyTheme.muted) }; Spacer(); Image(systemName: "arrow.right") }.glosifyCard(padding: 14)
                    }.buttonStyle(.plain)
                }
            }.padding().padding(.bottom, 90)
        }
    }
}

private struct TranscriptDetailView: View {
    @Bindable var model: AppModel
    let transcriptID: UUID
    @Environment(\.dismiss) private var dismiss
    @State private var title = ""
    @State private var page = 0
    private let pageSize = 20
    private var transcript: Transcript? { model.transcripts.first { $0.id == transcriptID } }
    private var segments: [TranscriptSegment] { guard let transcript else { return [] }; return Array(transcript.segments.dropFirst(page * pageSize).prefix(pageSize)) }

    var body: some View {
        ScrollView {
            if let transcript {
                VStack(spacing: 15) {
                    ScreenHeader(eyebrow: "\(LanguageCatalog.find(transcript.sourceLanguage).name) → \(LanguageCatalog.find(transcript.targetLanguage).name)", title: transcript.title, subtitle: "\(transcript.segments.count) captured captions · \(transcript.stream)")
                    HStack { TextField("Transcript title", text: $title).textFieldStyle(.roundedBorder); Button("Rename") { Task { _ = await model.perform { let updated = try await model.environment.transcripts.renameTranscript(id: transcript.id, title: title); if let index = model.transcripts.firstIndex(where: { $0.id == updated.id }) { model.transcripts[index] = updated } } } }.buttonStyle(SecondaryButtonStyle()) }
                    ForEach(segments) { segment in
                        VStack(alignment: .leading, spacing: 7) { Text(segment.capturedAt, style: .time).font(.caption).foregroundStyle(GlosifyTheme.primary); Text(segment.sourceText).font(GlosifyTheme.body(16, weight: .bold)); Text(segment.translatedText).foregroundStyle(GlosifyTheme.muted) }.frame(maxWidth: .infinity, alignment: .leading).glosifyCard(padding: 14)
                    }
                    if transcript.segments.count > pageSize { HStack { Button("Previous") { page = max(0, page - 1) }.disabled(page == 0); Spacer(); Text("Page \(page + 1)"); Spacer(); Button("Next") { page += 1 }.disabled((page + 1) * pageSize >= transcript.segments.count) } }
                    Button(role: .destructive) { Task { let ok = await model.perform { try await model.environment.transcripts.deleteTranscript(id: transcript.id); model.transcripts = try await model.environment.transcripts.transcripts() }; if ok { dismiss() } } } label: { Label("Delete transcript", systemImage: "trash") }
                }.padding()
            }
        }.glosifyScreen().navigationTitle("Transcript").navigationBarTitleDisplayMode(.inline)
            .onAppear { title = transcript?.title ?? ""; model.assistantContext = transcript?.title ?? "Transcripts" }
            .onDisappear { model.assistantContext = "Library" }
    }
}
