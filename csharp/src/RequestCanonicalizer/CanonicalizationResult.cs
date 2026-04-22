namespace RequestCanonicalizer;

public sealed record CanonicalizationResult(
    string Canonical,
    string BodySha256,
    IReadOnlyList<string> SignedHeaders);
