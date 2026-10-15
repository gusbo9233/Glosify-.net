namespace Glosify.Services.Avatar;

// Only Rain is written as young enough for teenage-sounding voices.
public sealed record AvatarCharacter(string Id, string Name, string VoiceGender, bool TeenVoice = false)
{
    public static AvatarCharacter Rain { get; } = new("rain", "Rain", "female", TeenVoice: true);
    public static AvatarCharacter Rowan { get; } = new("rowan", "Rowan", "male");
    public static AvatarCharacter Kwame { get; } = new("kwame", "Kwame", "male");
    public static AvatarCharacter Mei { get; } = new("mei", "Mei", "female");
    public static IReadOnlyList<AvatarCharacter> All { get; } = [Rain, Rowan, Kwame, Mei];

    public static AvatarCharacter? Find(string? id) => id switch
    {
        null or "rain" => Rain,
        "rowan" => Rowan,
        "kwame" => Kwame,
        "mei" => Mei,
        _ => null
    };
}
