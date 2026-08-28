using Microsoft.Extensions.Localization;

namespace Glosify.Localization;

public static class UiClientText
{
    private static readonly string[] Keys =
    [
        "Client.GenericError", "Client.NetworkError", "Client.TryAgain", "Client.Loading", "Assistant.Empty",
        "Client.Correct", "Client.Incorrect", "Client.NextWord", "Client.CheckFailed",
        "Client.SessionExpired", "Client.NotQuite", "Client.ShowResults",
        "Client.DeleteChat", "Client.DeleteChatConfirm",
        "Client.ApplyFailed", "Client.ApplyNetwork", "Client.RejectNetwork",
        "Client.AssistantFailed", "Client.AssistantNetwork", "Client.PictureFailed",
        "Client.PictureNetwork", "Client.MessageFailed", "Client.MessageRateLimited", "Client.PageOf", "Client.TtsNoText",
        "Client.TtsUnavailable", "Client.TtsFailed", "Client.TranslationFailed", "Common.NextMonth",
        "Client.PreferenceFailed", "Client.On", "Client.Off", "Client.Translating",
        "Client.TranslationError", "Client.Read", "Client.Stop",
        "Quiz.Progress", "Quiz.CorrectCount", "Quiz.CheckAnswer",
        "Settings.Flashcards", "Settings.Typing", "Settings.Choices", "Settings.Quiz", "Settings.WordsLower",
        "Settings.SentencesLower", "Settings.WordLower", "Settings.SentenceLower",
        "Settings.NeedItem", "Common.All", "Settings.Newest", "Settings.Oldest",
        "Settings.AllWordsOrder", "Settings.RangeDynamic", "Settings.SelectedCount",
        "Settings.PickedWords", "Settings.PickedCount",
        "Reader.ReadingSentence", "Reader.ReadingSelectionPart", "Reader.ReadingSelection", "Reader.ReadingPage",
        "Reader.FinishedSelection", "Reader.FinishedPage", "Reader.ReadingStopped", "Reader.Detected",
        "Reader.DetectedCached", "Reader.CachedTranslation", "Reader.NoSelectableTextPage", "Reader.RenderFailed",
        "Reader.PdfLoadFailed", "Reader.TranslateOffTitle", "Reader.ReadAloud", "Books.PaidUnavailable", "Books.PaidReason"
    ];

    public static IReadOnlyDictionary<string, string> Create(IStringLocalizer<UiText> text) =>
        Keys.ToDictionary(key => key, key => text[key].Value, StringComparer.Ordinal);
}
