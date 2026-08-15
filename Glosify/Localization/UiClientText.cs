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
        "Client.TtsUnavailable", "Client.TtsFailed", "Client.TranslationFailed",
        "Client.PreferenceFailed", "Client.On", "Client.Off", "Client.Translating",
        "Client.TranslationError", "Client.Read", "Client.Stop",
        "Quiz.Progress", "Quiz.CorrectCount", "Quiz.CheckAnswer",
        "Settings.Flashcards", "Settings.Typing", "Settings.Choices", "Settings.Quiz", "Settings.WordsLower",
        "Settings.SentencesLower", "Settings.WordLower", "Settings.SentenceLower",
        "Settings.NeedItem", "Common.All", "Settings.Newest", "Settings.Oldest",
        "Settings.AllWordsOrder", "Settings.RangeDynamic", "Settings.SelectedCount",
        "Settings.PickedWords", "Settings.PickedCount", "Custom.AnswerQuestion",
        "Custom.Correct", "Custom.CorrectAnswer", "Custom.CompleteAnswers", "Custom.Score",
        "Custom.GradeFailed", "Classroom.JoinCall", "Classroom.ReadyStart", "Classroom.NoOneCall",
        "Classroom.WaitingTeacher", "Classroom.JoinAfterTeacher", "Classroom.Connecting",
        "Classroom.MuteMic", "Classroom.UnmuteMic", "Classroom.CameraOff", "Classroom.CameraOn",
        "Classroom.Connected", "Classroom.NotConnected", "Classroom.ClassChat", "Classroom.VideoCall",
        "Classroom.MemberOne", "Classroom.MemberMany",
        "Classroom.NoMessages", "Classroom.Reconnecting",
        "Speaking.NextMonth", "Speaking.PaidPaused", "Speaking.StartingScene", "Speaking.SessionReady",
        "Speaking.Unavailable", "Speaking.NewSessionConfirm", "Speaking.UpdatingQuiz", "Speaking.PractisingNamed",
        "Speaking.FreeModeActive", "Speaking.Voice", "Speaking.Typed", "Speaking.StartBeforeSend",
        "Speaking.AvatarThinking", "Speaking.Avatar", "Speaking.SessionEnded", "Speaking.TakingSip",
        "Speaking.TakingSnack", "Speaking.AvatarReacting", "Speaking.MomentPassed", "Speaking.RecordUnavailable",
        "Speaking.Starting", "Speaking.MicConnectingAuto", "Speaking.MicConnectingHold", "Speaking.Listening",
        "Speaking.SpeakNaturally", "Speaking.ReleaseSend", "Speaking.KeepHolding", "Speaking.Transcribing",
        "Speaking.FinishingTranscript", "Speaking.NotCaughtComposer", "Speaking.NotCaughtAvatar",
        "Speaking.RecordingTooLong", "Speaking.TranscriptSending", "Speaking.TranscriptReady",
        "Speaking.Scene.PoursDrink", "Speaking.Scene.TakeSnack", "Speaking.Scene.OffersSnack",
        "Speaking.Scene.ClearsGlass", "Speaking.Scene.PolishesGlass", "Speaking.Scene.WipesCounter",
        "Speaking.Scene.LastCall", "Speaking.Scene.ItemUnavailable", "Speaking.Scene.FinishDrink",
        "Speaking.Scene.TakeSip", "Speaking.Scene.PresentsBill", "Speaking.Scene.PaymentRejected",
        "Speaking.Scene.PaymentAccepted", "Speaking.Scene.ReturnsChange", "Speaking.MicFailed", "Speaking.SpeechFinishFailed",
        "Reader.ReadingSentence", "Reader.ReadingSelectionPart", "Reader.ReadingSelection", "Reader.ReadingPage",
        "Reader.FinishedSelection", "Reader.FinishedPage", "Reader.ReadingStopped", "Reader.Detected",
        "Reader.DetectedCached", "Reader.CachedTranslation", "Reader.NoSelectableTextPage", "Reader.RenderFailed",
        "Reader.PdfLoadFailed", "Reader.TranslateOffTitle", "Reader.ReadAloud", "Books.PaidUnavailable",
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
