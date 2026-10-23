namespace Olive.Application;

public sealed record QueueKindDefinition(string Kind, IReadOnlyList<string> Subtypes, IReadOnlyList<string> Decisions, string RequiredRole, IReadOnlyList<string> RefSchemes, bool Expires);

// QUE-02: source-controlled and read-only. There is deliberately no register, add or set member.
public interface IQueueKindRegistry
{
    IReadOnlyList<string> Kinds { get; }

    bool TryGet(string kind, out QueueKindDefinition definition);
}
