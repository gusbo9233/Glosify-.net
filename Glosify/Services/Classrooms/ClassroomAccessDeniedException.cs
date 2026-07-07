namespace Glosify.Services.Classrooms;

public sealed class ClassroomAccessDeniedException : Exception
{
    public ClassroomAccessDeniedException()
        : base("Classroom not found or you do not have access to it.") { }

    public ClassroomAccessDeniedException(string message)
        : base(message) { }
}
