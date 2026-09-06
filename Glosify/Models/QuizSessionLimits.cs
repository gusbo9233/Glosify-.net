namespace Glosify.Models;

public static class QuizSessionLimits
{
    public const int MaxItems = 1000;

    public static int NormalizeCount(int count) => Math.Clamp(count, 1, MaxItems);
}
