# Request Canonicalizer

This repo is a small, focused library collection for one job: turning an HTTP request into a deterministic string representation and a deterministic body hash.

There are two implementations of the same behavior:

- `typescript/` for TypeScript callers
- `csharp/` for .NET callers

The important part is that they agree with each other.

## What this is for

If you need request signing, signature verification, replay protection, or any other workflow where both sides need to hash the "same" request in the same way, you need a canonical form first.

That is what these libraries produce.

The output looks like this:

```text
REQSIG-V1
METHOD:<UPPERCASE_METHOD>
PATH:<NORMALIZED_PATH>
QUERY:<SORTED_RFC3986_QUERY>
HEADER:<name:value>
SIGNED-HEADERS:<semicolon-separated-header-names>
BODY-SHA256:<base64url_sha256>
```

In practice that means:

- methods are uppercased
- paths always start with `/`
- query params are sorted by key and value
- signed headers are lowercased, deduplicated, sorted, and skipped if missing
- header values are trimmed and whitespace-normalized
- JSON bodies are rewritten with object keys sorted recursively
- form bodies are normalized like query strings

## Repo layout

- [typescript/src/index.ts](/Users/ccell/Projects/normalisation-library/typescript/src/index.ts)
- [typescript/test/canonicalize.test.ts](/Users/ccell/Projects/normalisation-library/typescript/test/canonicalize.test.ts)
- [csharp/src/RequestCanonicalizer/RequestCanonicalizer.cs](/Users/ccell/Projects/normalisation-library/csharp/src/RequestCanonicalizer/RequestCanonicalizer.cs)
- [csharp/tests/RequestCanonicalizer.Tests/Program.cs](/Users/ccell/Projects/normalisation-library/csharp/tests/RequestCanonicalizer.Tests/Program.cs)
- [tests/fixtures](/Users/ccell/Projects/normalisation-library/tests/fixtures)
- [parity/test_cross_language.py](/Users/ccell/Projects/normalisation-library/parity/test_cross_language.py)

## TypeScript

The TypeScript package is named `request-canonicalizer`.

It takes a plain request shape rather than depending on a framework-specific type, which makes it easier to use from your own HTTP layer.

```ts
import { canonicalizeRequest } from 'request-canonicalizer';

const result = await canonicalizeRequest(
  {
    method: 'POST',
    url: '/orders?a=2&a=1',
    headers: {
      'Content-Type': 'application/json',
      'X-Custom': ['beta', 'alpha']
    },
    body: { z: 1, a: { c: 3, d: 4 } }
  },
  {
    signedHeaders: ['content-type', 'x-custom'],
    baseUrl: 'https://api.example.com'
  }
);
```

Run the TypeScript tests with:

```bash
cd typescript
npm test
```

## C#

The .NET implementation lives under the `RequestCanonicalizer` namespace and targets `net8.0`.

Use `CanonicalizeAsync(...)` if you want to pass a plain request model. If you already have an ASP.NET Core `HttpRequest`, use `CanonicalizeAspNetCoreAsync(...)`.

```csharp
using RequestCanonicalizer;
using Canonicalizer = RequestCanonicalizer.RequestCanonicalizer;

var canonicalizer = new Canonicalizer();

var result = await canonicalizer.CanonicalizeAsync(
    new CanonicalRequest(
        "POST",
        "https://api.example.com/orders?a=2&a=1",
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["Content-Type"] = new[] { "application/json" },
            ["X-Custom"] = new[] { "beta", "alpha" }
        },
        new { z = 1, a = new { c = 3, d = 4 } }),
    new[] { "content-type", "x-custom" });
```

Run the C# tests with:

```bash
dotnet run --project csharp/tests/RequestCanonicalizer.Tests/RequestCanonicalizer.Tests.csproj
```

## Parity tests

The `parity/` folder is a thin test harness that runs the TypeScript and C# implementations against the same JSON fixtures and checks that the outputs match exactly.

That is useful because unit tests inside each language can still pass while the implementations quietly drift apart over time.

Run the parity suite with:

```bash
python3 -m unittest parity/test_cross_language.py
```

## Notes

- The TypeScript implementation rejects `FormData` / multipart bodies.
- The C# example uses a type alias because the namespace and the main class are both named `RequestCanonicalizer`.
- In this sandbox, `dotnet` may print `NU1900` warnings because NuGet vulnerability metadata cannot be fetched. The build still completes.

## Verified locally

```bash
cd typescript && npm test
dotnet run --project csharp/tests/RequestCanonicalizer.Tests/RequestCanonicalizer.Tests.csproj
python3 -m unittest parity/test_cross_language.py
```
