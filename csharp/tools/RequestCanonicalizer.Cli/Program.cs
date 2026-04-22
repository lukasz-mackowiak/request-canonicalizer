using System.Text.Json;
using RequestCanonicalizer;
using Canonicalizer = RequestCanonicalizer.RequestCanonicalizer;

var fixturePath = args.SingleOrDefault();
if (string.IsNullOrWhiteSpace(fixturePath))
{
    Console.Error.WriteLine("Usage: dotnet run --project ... -- <fixture.json>");
    return 1;
}

var fixture = JsonSerializer.Deserialize<FixtureFile>(
    await File.ReadAllTextAsync(fixturePath),
    new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? throw new InvalidOperationException("Failed to parse fixture file.");

var canonicalizer = new Canonicalizer();
var request = new CanonicalRequest(
    fixture.Request.Method,
    fixture.Request.Url,
    fixture.Request.Headers is null ? null : ConvertHeaders(fixture.Request.Headers),
    ConvertBody(fixture.Request.Body));

var result = await canonicalizer.CanonicalizeAsync(request, fixture.Options.SignedHeaders);

Console.WriteLine(JsonSerializer.Serialize(new
{
    canonical = result.Canonical,
    bodySha256 = result.BodySha256,
    signedHeaders = result.SignedHeaders
}));

return 0;

static object? ConvertBody(JsonElement? body)
{
    if (body is null)
    {
        return null;
    }

    var value = body.Value;
    return value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Object => value,
        JsonValueKind.Array => value,
        JsonValueKind.Number => value,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException($"Unsupported body value kind: {value.ValueKind}")
    };
}

static IReadOnlyDictionary<string, IReadOnlyList<string>> ConvertHeaders(
    IReadOnlyDictionary<string, JsonElement> headers)
{
    return headers.ToDictionary(
        static pair => pair.Key,
        static pair => (IReadOnlyList<string>)ConvertHeaderValues(pair.Value),
        StringComparer.OrdinalIgnoreCase);
}

static string[] ConvertHeaderValues(JsonElement value)
{
    return value.ValueKind switch
    {
        JsonValueKind.String => new[] { value.GetString() ?? string.Empty },
        JsonValueKind.Array => value.EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .ToArray(),
        _ => throw new InvalidOperationException($"Unsupported header value kind: {value.ValueKind}")
    };
}

internal sealed class FixtureFile
{
    public required FixtureRequest Request { get; init; }

    public required FixtureOptions Options { get; init; }
}

internal sealed class FixtureRequest
{
    public required string Method { get; init; }

    public required string Url { get; init; }

    public Dictionary<string, JsonElement>? Headers { get; init; }

    public JsonElement? Body { get; init; }
}

internal sealed class FixtureOptions
{
    public required string[] SignedHeaders { get; init; }
}
