using Microsoft.AspNetCore.Identity;

namespace Glosify.Models.Entities;

public class ApplicationUser : IdentityUser
{
    public string? SelectedQuizLanguageCode { get; set; }

    /// <summary>
    /// The language this user translates into, as a display name such as "English".
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SelectedQuizLanguageCode"/>, which is the language being
    /// learned and is restricted to the versioned supported catalog. What a learner already
    /// knows is stored the way quizzes store it, as a name, so the value can be used as a quiz
    /// source language without translation.
    /// </remarks>
    public string? PreferredSourceLanguage { get; set; }

    /// <summary>
    /// The language the assistant should reply in, as a display name.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same field as <see cref="PreferredSourceLanguage"/>: someone can
    /// study Polish through English translations while asking their questions in Swedish.
    /// Conflating the two is what made the assistant re-ask about English every turn.
    /// </remarks>
    public string? PreferredAssistantLanguage { get; set; }

    /// <summary>
    /// Opaque identifier used to correlate and delete this user's assistant telemetry
    /// without exporting the Identity user id, email, or another login identifier.
    /// </summary>
    public Guid AssistantTelemetrySubjectId { get; set; } = Guid.NewGuid();
}
