using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace RequestCanonicalizer;

public sealed class RequestCanonicalizer
{
    public async Task<CanonicalizationResult> CanonicalizeAsync(
        CanonicalRequest request,
        IEnumerable<string> configuredSignedHeaders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(configuredSignedHeaders);

        var uri = new Uri(request.Url, UriKind.RelativeOrAbsolute);
        if (!uri.IsAbsoluteUri)
        {
            uri = new Uri(new Uri("https://canonical.invalid"), request.Url);
        }

        var method = request.Method.ToUpperInvariant();
        var path = NormalizePath(uri.AbsolutePath);
        var query = NormalizeQuery(QueryHelpers.ParseQuery(uri.Query));
        var normalizedHeaders = NormalizeHeaders(request.Headers, configuredSignedHeaders);

        var contentType = FirstHeaderValue(request.Headers, "content-type");
        var bodyBytes = await CanonicalizeBodyAsync(request.Body, contentType, cancellationToken);
        var bodySha256 = Base64UrlEncode(SHA256.HashData(bodyBytes));

        var lines = new List<string>
        {
            "REQSIG-V1",
            $"METHOD:{method}",
            $"PATH:{path}",
            $"QUERY:{query}"
        };

        lines.AddRange(normalizedHeaders.HeaderLines.Select(static header => $"HEADER:{header}"));
        lines.Add($"SIGNED-HEADERS:{string.Join(';', normalizedHeaders.SignedHeaders)}");
        lines.Add($"BODY-SHA256:{bodySha256}");

        return new CanonicalizationResult(
            string.Join('\n', lines),
            bodySha256,
            normalizedHeaders.SignedHeaders);
    }

    public async Task<CanonicalizationResult> CanonicalizeAspNetCoreAsync(
        HttpRequest request,
        IEnumerable<string> configuredSignedHeaders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bodyBytes = await ReadBodyAsync(request, cancellationToken);
        var headers = request.Headers.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value.Select(static value => value ?? string.Empty).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        object? body = bodyBytes.Length == 0 ? null : bodyBytes;
        var contentType = request.ContentType;

        if (body is not null && IsJsonContentType(contentType))
        {
            body = Encoding.UTF8.GetString(bodyBytes);
        }
        else if (body is not null && IsFormUrlEncodedContentType(contentType))
        {
            body = Encoding.UTF8.GetString(bodyBytes);
        }

        var canonicalRequest = new CanonicalRequest(
            request.Method,
            $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}",
            headers,
            body);

        return await CanonicalizeAsync(canonicalRequest, configuredSignedHeaders, cancellationToken);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
    }

    private static string NormalizeQuery(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> query)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        foreach (var entry in query)
        {
            foreach (var value in entry.Value)
            {
                pairs.Add(new KeyValuePair<string, string>(entry.Key, value ?? string.Empty));
            }
        }

        pairs.Sort(static (left, right) =>
        {
            var keyCompare = string.CompareOrdinal(left.Key, right.Key);
            return keyCompare != 0
                ? keyCompare
                : string.CompareOrdinal(left.Value, right.Value);
        });

        return string.Join("&", pairs.Select(static pair =>
            $"{Rfc3986Encode(pair.Key)}={Rfc3986Encode(pair.Value)}"));
    }

    private static (IReadOnlyList<string> SignedHeaders, IReadOnlyList<string> HeaderLines) NormalizeHeaders(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers,
        IEnumerable<string> configuredSignedHeaders)
    {
        var headerLookup = headers is null
            ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyList<string>>(headers, StringComparer.OrdinalIgnoreCase);

        var requested = configuredSignedHeaders
            .Select(static header => header.Trim().ToLowerInvariant())
            .Where(static header => !string.IsNullOrWhiteSpace(header))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static header => header, StringComparer.Ordinal)
            .ToArray();

        var signedHeaders = new List<string>();
        var headerLines = new List<string>();

        foreach (var name in requested)
        {
            if (!headerLookup.TryGetValue(name, out var values))
            {
                continue;
            }

            var normalizedValues = values
                .Select(static value => NormalizeHeaderValue(value))
                .Where(static value => value.Length > 0)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

            if (normalizedValues.Length == 0)
            {
                continue;
            }

            signedHeaders.Add(name);
            headerLines.Add($"{name}:{string.Join(",", normalizedValues)}");
        }

        return (signedHeaders, headerLines);
    }

    private static string NormalizeHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var seenWhitespace = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                seenWhitespace = true;
                continue;
            }

            if (seenWhitespace && sb.Length > 0)
            {
                sb.Append(' ');
                seenWhitespace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string? FirstHeaderValue(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers,
        string targetName)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, targetName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.FirstOrDefault();
            }
        }

        return null;
    }

    private static async Task<byte[]> CanonicalizeBodyAsync(
        object? body,
        string? contentType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (body is null)
        {
            return Array.Empty<byte>();
        }

        switch (body)
        {
            case byte[] bytes:
                return bytes;
            case ReadOnlyMemory<byte> memory:
                return memory.ToArray();
            case Memory<byte> memory:
                return memory.ToArray();
            case Stream stream:
                using (var ms = new MemoryStream())
                {
                    await stream.CopyToAsync(ms, cancellationToken);
                    return ms.ToArray();
                }
            case IEnumerable<KeyValuePair<string, string>> formPairs:
                return Encoding.UTF8.GetBytes(NormalizeFormPairs(formPairs));
            case string text when IsJsonContentType(contentType):
                return CanonicalizeJsonDocument(JsonDocument.Parse(text));
            case string text when IsFormUrlEncodedContentType(contentType):
                return Encoding.UTF8.GetBytes(NormalizeFormPairs(ParseFormPairs(text)));
            case string text:
                return Encoding.UTF8.GetBytes(text);
            case JsonDocument document:
                return CanonicalizeJsonDocument(document);
            case JsonElement element:
                return CanonicalizeJsonElement(element);
            default:
                return CanonicalizeJsonElement(JsonSerializer.SerializeToElement(body));
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, cancellationToken);
        request.Body.Position = 0;
        return ms.ToArray();
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var lowered = contentType.ToLowerInvariant();
        return lowered.Contains("application/json", StringComparison.Ordinal) ||
               lowered.Contains("+json", StringComparison.Ordinal);
    }

    private static bool IsFormUrlEncodedContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        return contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseFormPairs(string rawForm)
    {
        var parsed = QueryHelpers.ParseQuery(rawForm);

        foreach (var entry in parsed)
        {
            foreach (var value in entry.Value)
            {
                yield return new KeyValuePair<string, string>(entry.Key, value ?? string.Empty);
            }
        }
    }

    private static byte[] CanonicalizeJsonDocument(JsonDocument doc)
    {
        return CanonicalizeJsonElement(doc.RootElement);
    }

    private static byte[] CanonicalizeJsonElement(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            WriteCanonicalJsonElement(writer, element);
        }

        return ms.ToArray();
    }

    private static void WriteCanonicalJsonElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJsonElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJsonElement(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new InvalidOperationException($"Unsupported JSON value kind: {element.ValueKind}");
        }
    }

    private static string NormalizeFormPairs(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        return string.Join("&", pairs
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Value, StringComparer.Ordinal)
            .Select(static pair => $"{Rfc3986Encode(pair.Key)}={Rfc3986Encode(pair.Value)}"));
    }

    private static string Rfc3986Encode(string value)
    {
        return Uri.EscapeDataString(value)
            .Replace("!", "%21", StringComparison.Ordinal)
            .Replace("'", "%27", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("*", "%2A", StringComparison.Ordinal);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }
}
