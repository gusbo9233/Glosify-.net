namespace Glosify.Models.Entities;

public class Classroom
{
    public Guid Id { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// The learning language the classroom teaches, so the list only shows
    /// classrooms for the language the member has selected.
    /// </summary>
    public string? Language { get; set; }

    public string JoinCode { get; set; } = string.Empty;
    public bool JoinCodeEnabled { get; set; } = true;
    public Guid GroupCallId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsArchived { get; set; }
}
