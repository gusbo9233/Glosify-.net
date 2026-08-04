using Glosify.Services.RealtimeTranslation;

namespace Glosify.Models.ViewModels;

public sealed class TranscriptLibraryViewModel
{
    public required TranscriptLibraryPage Library { get; init; }
}

public sealed class TranscriptDetailViewModel
{
    public required TranscriptDetailPage Transcript { get; init; }
}
