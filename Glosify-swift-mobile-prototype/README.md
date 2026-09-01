# Glosify Mobile Prototype

A native SwiftUI iPhone prototype of the Glosify learner experience. It targets
iOS 18, supports portrait orientation, and intentionally uses no network,
database, account, AI, translation, or payment backend.

## Run

1. Install a full version of Xcode with an iOS 18 or newer Simulator runtime.
2. Open `GlosifyMobilePrototype.xcodeproj`.
3. Select the shared `GlosifyMobilePrototype` scheme and an iPhone simulator.
4. Build and run.

The prototype starts as `learner@glosify.se` with Polish selected. Sign out from
Profile to exercise mock sign-in, registration, and password reset. Restarting
the app restores the deterministic seed data.

## Included experience

- Five native tabs: Home, Quizzes, Anki, Explore, and Library.
- Full learning-language catalog plus Freestyle mode.
- Quiz and nested-collection management, JSON import, image-selection mock
  extraction, flashcard and typing practice, speech, visibility, and moves.
- Anki collections, quiz linking, study ratings, and deterministic intervals.
- Community quiz previews and local copies.
- Native PDF import and PDFKit rendering, mock translation, page navigation,
  and on-device read-aloud.
- Seeded transcript management, contextual assistant chats and reviewable
  library changes, credits, legal information, and support.

Admin tools, live Chrome subtitle capture, real FSRS scheduling, production
authentication, remote AI, translation, Stripe checkout, and persistence are
explicitly outside this prototype.

## Architecture

`AppEnvironment` injects domain-specific async protocols. `MockDataStore` is a
single actor-backed implementation and the only mutable source of truth. Device
behavior is isolated behind `SpeechProviding` and `PDFProviding`. Views receive
an observable `AppModel`; there is no `URLSession`, SwiftData, Core Data,
UserDefaults, backend URL, secret, or third-party package.

Google Fonts' Plus Jakarta Sans and Lora are bundled under their OFL licenses in
`GlosifyMobilePrototype/Fonts`.

## Tests

The project includes unit coverage for deterministic reset, validation,
quiz/collection mutations, JSON import, scoring, Anki ratings, Explore copies,
transcripts, assistant changes, credits, and auth. UI tests cover the seeded tab
journey and sign-out/sign-in.

Run from Xcode with **Product → Test**, or from a configured command line:

```sh
xcodebuild test \
  -project GlosifyMobilePrototype.xcodeproj \
  -scheme GlosifyMobilePrototype \
  -destination 'platform=iOS Simulator,name=iPhone 16 Pro'
```
