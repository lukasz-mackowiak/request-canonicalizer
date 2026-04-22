using RequestCanonicalizer;
using Canonicalizer = RequestCanonicalizer.RequestCanonicalizer;

return await TestRunner.RunAsync();

internal static class TestRunner
{
    public static async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Execute)[]
        {
            ("Canonicalizes JSON requests with sorted query params and normalized headers", CanonicalizesJsonRequestsAsync),
            ("Canonicalizes form-url-encoded request bodies", CanonicalizesFormBodiesAsync),
            ("Omits missing signed headers and hashes empty bodies", OmitsMissingHeadersAsync)
        };

        foreach (var (name, execute) in tests)
        {
            try
            {
                await execute();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        return 0;
    }

    private static async Task CanonicalizesJsonRequestsAsync()
    {
        var canonicalizer = new Canonicalizer();
        var result = await canonicalizer.CanonicalizeAsync(
            new CanonicalRequest(
                "post",
                "/orders?z=9&a=2&a=1",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["Content-Type"] = new[] { "application/json; charset=utf-8" },
                    ["X-Custom"] = new[] { " beta ", "alpha" }
                },
                "{\"z\":1,\"a\":{\"d\":4,\"c\":3}}"),
            new[] { "x-custom", "content-type" });

        AssertEqual("content-type,x-custom", string.Join(',', result.SignedHeaders));
        AssertEqual(string.Join('\n', new[]
        {
            "REQSIG-V1",
            "METHOD:POST",
            "PATH:/orders",
            "QUERY:a=1&a=2&z=9",
            "HEADER:content-type:application/json; charset=utf-8",
            "HEADER:x-custom:alpha,beta",
            "SIGNED-HEADERS:content-type;x-custom",
            "BODY-SHA256:lgk5inmP_V0lvyrVO7BdMSCUI3x8gY3ZmVHmADlvzGQ"
        }), result.Canonical);
    }

    private static async Task CanonicalizesFormBodiesAsync()
    {
        var canonicalizer = new Canonicalizer();
        var result = await canonicalizer.CanonicalizeAsync(
            new CanonicalRequest(
                "PUT",
                "https://example.test/token",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["Content-Type"] = new[] { "application/x-www-form-urlencoded" }
                },
                "scope=write&scope=read&grant_type=client_credentials"),
            new[] { "content-type" });

        AssertEqual(string.Join('\n', new[]
        {
            "REQSIG-V1",
            "METHOD:PUT",
            "PATH:/token",
            "QUERY:",
            "HEADER:content-type:application/x-www-form-urlencoded",
            "SIGNED-HEADERS:content-type",
            "BODY-SHA256:ktA-ZuMtnVixjTQLEHxA0pNJhQEjczsh63Os0xIWKzc"
        }), result.Canonical);
    }

    private static async Task OmitsMissingHeadersAsync()
    {
        var canonicalizer = new Canonicalizer();
        var result = await canonicalizer.CanonicalizeAsync(
            new CanonicalRequest("GET", "https://example.test/ping"),
            new[] { "x-missing" });

        AssertEqual(string.Join('\n', new[]
        {
            "REQSIG-V1",
            "METHOD:GET",
            "PATH:/ping",
            "QUERY:",
            "SIGNED-HEADERS:",
            "BODY-SHA256:47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU"
        }), result.Canonical);
    }

    private static void AssertEqual(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected:\n{expected}\n\nActual:\n{actual}");
        }
    }
}
