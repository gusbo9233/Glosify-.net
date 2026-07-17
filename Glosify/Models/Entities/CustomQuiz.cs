using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Entities;

public sealed class CustomQuiz
{
    public Guid Id { get; set; }
    public Guid QuizId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public bool IsPlayable { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public Quiz Quiz { get; set; } = null!;
}
