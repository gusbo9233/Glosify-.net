using Microsoft.AspNetCore.Identity;

namespace Glosify.Models.Entities;

public class ApplicationUser : IdentityUser
{
    public string? SelectedQuizLanguageCode { get; set; }

    /// <summary>
    /// Opaque identifier used to correlate and delete this user's assistant telemetry
    /// without exporting the Identity user id, email, or another login identifier.
    /// </summary>
    public Guid AssistantTelemetrySubjectId { get; set; } = Guid.NewGuid();
}
