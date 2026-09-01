import PhotosUI
import SwiftUI

struct AssistantView: View {
    @Bindable var model: AppModel
    let context: String
    @Environment(\.dismiss) private var dismiss
    @State private var selectedChatID: UUID?
    @State private var draft = ""
    @State private var selectedPhoto: PhotosPickerItem?
    @State private var showingChats = false
    @State private var isSending = false

    private var chat: AssistantChat? {
        model.chats.first(where: { $0.id == selectedChatID }) ?? model.chats.first
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                if showingChats { chatList } else { conversation }
                composer
            }
            .background(GlosifyTheme.background)
            .navigationTitle(showingChats ? "Chats" : "Study assistant")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button(showingChats ? "Chat" : "Chats") { showingChats.toggle() } }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    Button { Task { await newChat() } } label: { Image(systemName: "plus") }.accessibilityLabel("New chat")
                    Button("Done") { dismiss() }
                }
            }
            .onAppear { selectedChatID = selectedChatID ?? model.chats.first?.id }
            .onChange(of: selectedPhoto) { _, newValue in if newValue != nil { draft = draft.isEmpty ? "Create a quiz from the selected image" : draft } }
        }.preferredColorScheme(.dark)
    }

    private var conversation: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 14) {
                    if let chat, chat.messages.isEmpty { EmptyState(icon: "sparkles", title: "Ask Glosify", message: "Create a quiz, explain a phrase, or plan your next study session.") }
                    ForEach(chat?.messages ?? []) { message in messageBubble(message) }
                    if isSending { ProgressView().tint(GlosifyTheme.primary).padding() }
                }.padding().padding(.bottom, 8)
            }.onChange(of: chat?.messages.count) { _, _ in if let id = chat?.messages.last?.id { withAnimation { proxy.scrollTo(id, anchor: .bottom) } } }
        }
    }

    private var chatList: some View {
        List {
            ForEach(model.chats) { item in
                Button {
                    selectedChatID = item.id; showingChats = false
                } label: {
                    VStack(alignment: .leading, spacing: 4) { Text(item.title).font(.headline); Text(item.contextLabel).font(.caption).foregroundStyle(GlosifyTheme.muted) }
                }.listRowBackground(GlosifyTheme.surface)
            }.onDelete(perform: deleteChats)
        }.scrollContentBackground(.hidden).background(GlosifyTheme.background)
    }

    private var composer: some View {
        VStack(spacing: 9) {
            HStack { Label(context, systemImage: "scope").font(.caption).foregroundStyle(GlosifyTheme.muted); Spacer(); if selectedPhoto != nil { Label("Image attached", systemImage: "photo.fill").font(.caption).foregroundStyle(GlosifyTheme.primary) } }
            HStack(alignment: .bottom, spacing: 10) {
                PhotosPicker(selection: $selectedPhoto, matching: .images) { Image(systemName: "photo.on.rectangle").frame(width: 44, height: 44).background(GlosifyTheme.surfaceHigh).clipShape(Circle()) }.accessibilityLabel("Attach image")
                TextField("Ask the assistant…", text: $draft, axis: .vertical).lineLimit(1...5).padding(12).background(GlosifyTheme.surfaceHigh).clipShape(RoundedRectangle(cornerRadius: 16))
                Button { Task { await send() } } label: { Image(systemName: "arrow.up").font(.headline).frame(width: 44, height: 44).foregroundStyle(GlosifyTheme.onPrimary).background(GlosifyTheme.primary).clipShape(Circle()) }.disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isSending).accessibilityLabel("Send message")
            }
        }.padding().background(GlosifyTheme.surfaceLowest)
    }

    private func messageBubble(_ message: AssistantMessage) -> some View {
        VStack(alignment: message.role == .user ? .trailing : .leading, spacing: 8) {
            Text(message.text).font(GlosifyTheme.body(15)).padding(14).foregroundStyle(message.role == .user ? GlosifyTheme.onPrimary : GlosifyTheme.text).background(message.role == .user ? GlosifyTheme.primaryContainer : GlosifyTheme.surface).clipShape(RoundedRectangle(cornerRadius: 17))
            if let change = message.pendingChange { pendingCard(change) }
            if message.role == .assistant {
                HStack {
                    Button { Task { await feedback(message, 1) } } label: { Image(systemName: message.feedback == 1 ? "hand.thumbsup.fill" : "hand.thumbsup") }
                    Button { Task { await feedback(message, -1) } } label: { Image(systemName: message.feedback == -1 ? "hand.thumbsdown.fill" : "hand.thumbsdown") }
                }.foregroundStyle(GlosifyTheme.muted)
            }
        }.frame(maxWidth: .infinity, alignment: message.role == .user ? .trailing : .leading).id(message.id)
    }

    private func pendingCard(_ change: PendingLibraryChange) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Label("Review library change", systemImage: "list.clipboard").font(.headline)
            Text(change.summary).font(.subheadline).foregroundStyle(GlosifyTheme.muted)
            if change.state == .pending {
                HStack {
                    Button("Apply") { Task { await resolve(change, true) } }.buttonStyle(PrimaryButtonStyle())
                    Button("Reject") { Task { await resolve(change, false) } }.buttonStyle(SecondaryButtonStyle())
                }
            } else { Label(change.state == .applied ? "Applied" : "Rejected", systemImage: change.state == .applied ? "checkmark.circle.fill" : "xmark.circle.fill").foregroundStyle(change.state == .applied ? GlosifyTheme.primary : GlosifyTheme.error) }
        }.glosifyCard(padding: 14)
    }

    private func newChat() async {
        if let created = try? await model.environment.assistant.createChat(context: context) { model.chats.insert(created, at: 0); selectedChatID = created.id; showingChats = false }
    }

    private func send() async {
        if chat == nil { await newChat() }
        guard let id = chat?.id else { return }
        let text = draft; draft = ""; selectedPhoto = nil; isSending = true
        defer { isSending = false }
        _ = await model.perform {
            let updated = try await model.environment.assistant.sendMessage(chatID: id, text: text, context: context)
            replace(updated)
        }
    }

    private func resolve(_ change: PendingLibraryChange, _ apply: Bool) async {
        guard let chatID = chat?.id else { return }
        _ = await model.perform {
            replace(try await model.environment.assistant.resolveChange(chatID: chatID, changeID: change.id, apply: apply))
            if apply { await model.refreshLibrary() }
        }
    }

    private func feedback(_ message: AssistantMessage, _ rating: Int) async {
        guard let chatID = chat?.id else { return }
        _ = await model.perform { replace(try await model.environment.assistant.saveFeedback(chatID: chatID, messageID: message.id, rating: rating)) }
    }

    private func replace(_ chat: AssistantChat) {
        if let index = model.chats.firstIndex(where: { $0.id == chat.id }) { model.chats[index] = chat } else { model.chats.insert(chat, at: 0) }
    }

    private func deleteChats(at offsets: IndexSet) {
        let ids = offsets.compactMap { model.chats.indices.contains($0) ? model.chats[$0].id : nil }
        Task {
            let deleted = await model.perform {
                for id in ids { try await model.environment.assistant.deleteChat(id: id) }
                model.chats = try await model.environment.assistant.chats()
            }
            guard deleted, let selectedChatID, !model.chats.contains(where: { $0.id == selectedChatID }) else { return }
            self.selectedChatID = model.chats.first?.id
        }
    }
}
