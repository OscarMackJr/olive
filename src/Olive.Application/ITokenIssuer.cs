using Olive.Domain;

namespace Olive.Application;

public abstract record TokenIssueResult
{
    public sealed record Issued(ServiceTokenGrant Grant) : TokenIssueResult;
    public sealed record Unavailable : TokenIssueResult;
    public sealed record Refused : TokenIssueResult;
    public sealed record CallerRejected(int Status) : TokenIssueResult;
}

// [ASSUMED] Olive-proposed shape: popeye has not specified a token-issue contract yet (D-10).
[OutboundPort(OutboundDestination.Popeye)]
public interface ITokenIssuer { Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct); }
