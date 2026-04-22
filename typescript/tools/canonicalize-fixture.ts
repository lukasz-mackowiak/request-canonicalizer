import { readFile } from 'node:fs/promises';

import { canonicalizeRequest, type CanonicalRequest, type CanonicalizationOptions } from '../src/index.ts';

interface FixtureFile {
  request: CanonicalRequest;
  options: CanonicalizationOptions;
}

async function main(): Promise<void> {
  const fixturePath = process.argv[2];
  if (!fixturePath) {
    throw new Error('Usage: node --experimental-strip-types canonicalize-fixture.ts <fixture.json>');
  }

  const fixture = JSON.parse(await readFile(fixturePath, 'utf8')) as FixtureFile;
  const result = await canonicalizeRequest(fixture.request, fixture.options);
  process.stdout.write(`${JSON.stringify(result)}\n`);
}

await main();
