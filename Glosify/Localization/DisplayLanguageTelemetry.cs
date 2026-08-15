using System.Diagnostics.Metrics;

namespace Glosify.Localization;

public static class DisplayLanguageTelemetry
{
    public const string MeterName = "Glosify.DisplayLanguage";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Selections = Meter.CreateCounter<long>("glosify.display_language.selections");

    public static void Record(string culture, string source) =>
        Selections.Add(1, new KeyValuePair<string, object?>("display.culture", culture),
            new KeyValuePair<string, object?>("display.source", source));
}
