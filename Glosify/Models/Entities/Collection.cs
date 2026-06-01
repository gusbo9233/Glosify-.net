namespace Glosify.Models.Entities
{
    public class Collection
    {
        public Guid Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Language {  get; set; } = string.Empty;
        public Guid? ParentCollectionId { get; set; }
        public Collection? ParentCollection { get; set; }

        public ICollection<Collection> ChildCollections { get; set; } = [];
        public ICollection<Quiz> Quizzes { get; set; } = [];

        public DateTimeOffset CreatedAt { get; set; }
    }
}
