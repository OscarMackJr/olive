namespace Olive.Catalog;

// First schema error only: instance JSON Pointer and failing keyword. Never the offending value (D-03, T-02-06).
public sealed record SchemaViolation(string Pointer, string Keyword);
