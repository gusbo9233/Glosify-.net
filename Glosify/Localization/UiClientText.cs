using Microsoft.Extensions.Localization;

namespace Glosify.Localization;

public static class UiClientText
{
    private static readonly string[] Keys =
    [
        "Client.GenericError", "Client.NetworkError", "Client.TryAgain", "Client.Loading", "Assistant.Empty",
        "Client.Correct", "Client.Incorrect", "Client.NextWord", "Client.CheckFailed",
        "Client.SessionExpired", "Client.NotQuite", "Client.ShowResults",
        "Client.DeleteQuizConfirm", "Client.DeleteQuizFailed", "Client.DeleteChat", "Client.DeleteChatConfirm",
        "Client.ApplyFailed", "Client.ApplyNetwork", "Client.RejectNetwork",
        "Client.AssistantFailed", "Client.AssistantNetwork", "Client.PictureFailed",
        "Client.PictureNetwork", "Client.MessageFailed", "Client.MessageRateLimited", "Client.JoinCallFailed",
        "Client.CallTokenFailed", "Client.PageOf", "Client.TtsNoText",
        "Client.TtsUnavailable", "Client.TtsFailed", "Client.TranslationFailed", "Common.NextMonth",
        "Client.PreferenceFailed", "Client.On", "Client.Off", "Client.Translating",
        "Client.TranslationError", "Client.Read", "Client.Stop",
        "Quiz.Progress", "Quiz.CorrectCount", "Quiz.CheckAnswer",
        "Settings.Flashcards", "Settings.Typing", "Settings.Choices", "Settings.Quiz", "Settings.WordsLower",
        "Settings.SentencesLower", "Settings.WordLower", "Settings.SentenceLower",
        "Settings.NeedItem", "Common.All", "Settings.Newest", "Settings.Oldest",
        "Settings.AllWordsOrder", "Settings.RangeDynamic", "Settings.SelectedCount",
        "Settings.PickedWords", "Settings.PickedCount", "Custom.AnswerQuestion",
        "Custom.Correct", "Custom.CorrectAnswer", "Custom.CompleteAnswers", "Custom.Score",
        "Custom.GradeFailed",
        "Reader.ReadingSentence", "Reader.ReadingSelectionPart", "Reader.ReadingSelection", "Reader.ReadingPage",
        "Reader.FinishedSelection", "Reader.FinishedPage", "Reader.ReadingStopped", "Reader.Detected",
        "Reader.DetectedCached", "Reader.CachedTranslation", "Reader.NoSelectableTextPage", "Reader.RenderFailed",
        "Reader.PdfLoadFailed", "Reader.TranslateOffTitle", "Reader.ReadAloud", "Books.PaidUnavailable", "Books.PaidReason",
        "Editor.Heading", "Editor.Instruction", "Editor.WordLabel", "Editor.TranslationLabel",
        "Editor.TextInput", "Editor.LongAnswer", "Editor.Checkbox", "Editor.RadioChoices", "Editor.CheckboxChoices",
        "Editor.SelectMenu", "Editor.WordBank", "Editor.SubmitButton", "Editor.Feedback", "Editor.EmptyCanvas",
        "Editor.MoveLeft", "Editor.MoveUp", "Editor.MoveDown", "Editor.MoveRight", "Editor.RemoveBlock", "Editor.Resize",
        "Editor.Word", "Editor.DisplayField", "Editor.Words", "Editor.Options", "Editor.CorrectOption",
        "Editor.RemoveOption", "Editor.AddOption", "Editor.Properties", "Editor.SelectBlock", "Editor.TargetInputs",
        "Editor.PreviewOnly", "Editor.BackToEditor", "Editor.Preview", "Editor.PreviewingOne", "Editor.PreviewingMany",
        "Editor.ReadyOne", "Editor.ReadyMany", "Editor.ReplaceConfirm", "Editor.SaveFailed", "Editor.SavedPlayable",
        "Editor.DraftSaved", "Editor.SaveStopped", "Editor.WordLemma", "Editor.Translation", "Editor.Width",
        "Editor.Column", "Editor.ColumnNumber", "Editor.Row", "Editor.Text", "Editor.LiveBinding",
        "Editor.ExerciseRow", "Editor.AccessibleLabel", "Editor.CustomExpected", "Editor.ExpectedBinding",
        "Editor.DisplayedWord", "Editor.ExpectedChecked"
    ];

    public static IReadOnlyDictionary<string, string> Create(IStringLocalizer<UiText> text) =>
        Keys.ToDictionary(key => key, key => text[key].Value, StringComparer.Ordinal);
}
