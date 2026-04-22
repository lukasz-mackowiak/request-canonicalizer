namespace RequestCanonicalizer;

public sealed record CanonicalRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Headers = null,
    object? Body = null);
