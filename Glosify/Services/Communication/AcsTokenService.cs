using Azure.Communication;
using Azure.Communication.Identity;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Communication;

public sealed class AcsTokenService : IAcsTokenService
{
    private readonly GlosifyContext _context;
    private readonly AcsOptions _options;
    private readonly Lazy<CommunicationIdentityClient> _client;

    public AcsTokenService(GlosifyContext context, IOptions<AcsOptions> options)
    {
        _context = context;
        _options = options.Value;
        _client = new Lazy<CommunicationIdentityClient>(
            () => new CommunicationIdentityClient(_options.ConnectionString));
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<AcsCallToken> GetCallTokenAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Video calling is not configured on this server.");
        }

        var identity = await _context.AcsUserIdentities
            .FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);

        if (identity == null)
        {
            var created = await _client.Value.CreateUserAsync(cancellationToken);
            identity = new AcsUserIdentity
            {
                UserId = userId,
                AcsUserId = created.Value.Id,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.AcsUserIdentities.Add(identity);
            await _context.SaveChangesAsync(cancellationToken);
        }

        var token = await _client.Value.GetTokenAsync(
            new CommunicationUserIdentifier(identity.AcsUserId),
            [CommunicationTokenScope.VoIP],
            cancellationToken);

        return new AcsCallToken(token.Value.Token, token.Value.ExpiresOn, identity.AcsUserId);
    }
}
