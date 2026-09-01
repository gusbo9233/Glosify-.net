import AVFAudio
import Foundation
import PDFKit
import SwiftUI
import UIKit

@MainActor
final class SpeechService: NSObject, SpeechProviding {
    private let synthesizer = AVSpeechSynthesizer()

    func speak(_ text: String, locale: String) {
        synthesizer.stopSpeaking(at: .immediate)
        let utterance = AVSpeechUtterance(string: text)
        utterance.voice = AVSpeechSynthesisVoice(language: locale)
        utterance.rate = AVSpeechUtteranceDefaultSpeechRate * 0.92
        synthesizer.speak(utterance)
    }

    func stop() { synthesizer.stopSpeaking(at: .immediate) }
}

struct PDFService: PDFProviding {
    func pageTexts(from data: Data) throws -> [String] {
        guard let document = PDFDocument(data: data), document.pageCount > 0 else {
            throw PrototypeError.invalidInput("Choose a readable PDF document.")
        }
        return (0..<document.pageCount).map { document.page(at: $0)?.string ?? "" }
    }
}

struct PDFKitView: UIViewRepresentable {
    let data: Data
    var pageIndex: Int

    func makeUIView(context: Context) -> PDFView {
        let view = PDFView()
        view.backgroundColor = UIColor(GlosifyTheme.surfaceLow)
        view.displayMode = .singlePageContinuous
        view.displayDirection = .vertical
        view.autoScales = true
        view.document = PDFDocument(data: data)
        return view
    }

    func updateUIView(_ view: PDFView, context: Context) {
        if view.document == nil { view.document = PDFDocument(data: data) }
        if let page = view.document?.page(at: pageIndex), view.currentPage != page {
            view.go(to: page)
        }
    }
}
