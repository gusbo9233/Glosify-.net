using Glosify.Services.Ai;
using Xunit;

namespace Glosify.Tests;

public sealed class PageTranslationOptionsTests
{
    [Fact]
    public void Translation_output_reserve_must_be_positive_even_when_budget_is_disabled()
    {
        var options = new AiUsageOptions
        {
            PageTranslationOutputTokenReserve = 0,
            MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
        };

        var result = new AiUsageOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("PageTranslationOutputTokenReserve", StringComparison.Ordinal));
    }
}
