using System.Text.Json;

namespace Glosify.Services.Game;

public static class GameProfileValidation
{
    public static bool IsValid(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 20000) return false;
        var allowed = new HashSet<string> { "version", "profile", "appearance", "appearances", "gameLanguage", "learningLevel", "quizId" };
        if (value.EnumerateObject().Any(p => !allowed.Contains(p.Name))) return false;
        if (!value.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1) return false;
        if (!value.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object) return false;
        var limits = new Dictionary<string, int> { ["name"]=200,["pronouns"]=200,["age"]=3,["hometown"]=200,["country"]=200,["currentHome"]=200,["occupation"]=200,["languages"]=1000,["interests"]=1000,["about"]=4000 };
        foreach (var p in profile.EnumerateObject())
        {
            if (p.Name == "version") { if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var n) || n != 1) return false; continue; }
            if (!limits.TryGetValue(p.Name, out var limit) || p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > limit) return false;
            if (p.Name == "age" && p.Value.GetString() is { Length: > 0 } age && (!age.All(char.IsAsciiDigit) || !int.TryParse(age, out var years) || years > 120)) return false;
        }
        if (!value.TryGetProperty("appearance", out var appearance) || !Appearance(appearance)) return false;
        if (value.TryGetProperty("appearances", out var presets) && (presets.ValueKind != JsonValueKind.Object
            || presets.EnumerateObject().Any(p => p.Name is not ("straight" or "curved") || !Appearance(p.Value)))) return false;
        if (value.TryGetProperty("learningLevel", out var level)
            && (level.ValueKind != JsonValueKind.String || level.GetString() is not ("A1" or "A2" or "B1" or "B2" or "C1" or "C2"))) return false;
        return value.TryGetProperty("gameLanguage", out var language) && language.ValueKind == JsonValueKind.String
            && language.GetString() is { Length: > 0 and <= 32 }
            && value.TryGetProperty("quizId", out var quiz) && (quiz.ValueKind == JsonValueKind.Null || quiz.ValueKind == JsonValueKind.String && Guid.TryParse(quiz.GetString(), out _));
    }
    private static bool Appearance(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var fields = new HashSet<string> { "body", "build", "face", "skin", "hair", "hairColor", "outfit", "topColor", "bottomColor" };
        return value.EnumerateObject().All(p => p.Name == "version" ? p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v) && v == 1
            : fields.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 and <= 32 });
    }
}
