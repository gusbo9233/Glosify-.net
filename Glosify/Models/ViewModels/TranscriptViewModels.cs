using Glosify.Services.RealtimeTranslation;

namespace Glosify.Models.ViewModels;

public sealed class TranscriptLibraryViewModel
{
    public required TranscriptLibraryPage Library { get; init; }
}

public sealed class TranscriptDetailViewModel
{
    public required TranscriptDetailPage Transcript { get; init; }

    /// <summary>
    /// When each page happened, so a jump target can be named by time rather than by
    /// number alone. Empty for a session too long to index cheaply; the pager then falls
    /// back to bare page numbers.
    /// </summary>
    public IReadOnlyList<TranscriptPageSpan> PageSpans { get; init; } = [];
}
