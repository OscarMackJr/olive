namespace Olive.Application;

// D-07: alarms carry a stable code and component only, never request data.
public interface IOperationalAlarm { void Raise(string code, string component); }

public static class AlarmCodes
{
    public const string TraceResolver401 = "trace_resolver_401";
    public const string TraceResolver403 = "trace_resolver_403";
    public const string TokenIssuer401 = "token_issuer_401";
    public const string TokenIssuer403 = "token_issuer_403";
    public const string TokenIssuerRefusedScope = "token_issuer_refused_scope";
    public const string TokenClaimsMismatch = "token_claims_mismatch";
    public const string TokenOutlivesSessionKey = "token_outlives_session_key";
    public const string TraceResponseMalformed = "trace_response_malformed";
    public const string IntrospectionCallerRejected = "introspection_caller_rejected";
    public const string DirectoryResolver401 = "directory_resolver_401";
    public const string DirectoryResolver403 = "directory_resolver_403";
    public const string DirectoryResponseMalformed = "directory_response_malformed";
    // WR-03: an execution outcome is unknown and must be reconciled against the target by an operator.
    public const string ExecutionInDoubt = "execution_in_doubt";
}
