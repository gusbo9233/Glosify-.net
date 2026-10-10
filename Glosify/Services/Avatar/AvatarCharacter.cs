namespace Glosify.Services.Avatar;

public sealed record AvatarCharacter(string Id, string Name, string VoiceGender)
{
    public static AvatarCharacter Rain { get; } = new("rain", "Rain", "female");
    public static AvatarCharacter Rowan { get; } = new("rowan", "Rowan", "male");

    public static AvatarCharacter? Find(string? id) => id switch
    {
        null or "rain" => Rain,
        "rowan" => Rowan,
        _ => null
    };
}
