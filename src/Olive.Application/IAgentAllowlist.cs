namespace Olive.Application;

public interface IAgentAllowlist { bool IsAllowed(string agentId); IReadOnlySet<string> Entitlements(string agentId); }
