namespace Glosify.Models;

/// <summary>
/// TempData keys for the one-shot flash messages the views render. Using the
/// constants keeps controller and view in sync; a typo becomes a compile error
/// instead of a silently missing message.
/// </summary>
public static class NotificationKeys
{
    public const string Quiz = "QuizMessage";
    public const string Admin = "AdminMessage";
    public const string Book = "BookMessage";
    public const string Explore = "ExploreMessage";
}
