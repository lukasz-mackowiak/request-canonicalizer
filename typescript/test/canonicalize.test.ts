import test from 'node:test';
import assert from 'node:assert/strict';

import { canonicalizeRequest } from '../src/index.ts';

test('canonicalizes JSON requests with sorted query params and normalized headers', async () => {
  const result = await canonicalizeRequest(
    {
      method: 'post',
      url: '/orders?z=9&a=2&a=1',
      headers: {
        'Content-Type': 'application/json; charset=utf-8',
        'X-Custom': [' beta ', 'alpha']
      },
      body: '{"z":1,"a":{"d":4,"c":3}}'
    },
    {
      signedHeaders: ['x-custom', 'content-type'],
      baseUrl: 'https://example.test'
    }
  );

  assert.deepEqual(result.signedHeaders, ['content-type', 'x-custom']);
  assert.equal(
    result.canonical,
    [
      'REQSIG-V1',
      'METHOD:POST',
      'PATH:/orders',
      'QUERY:a=1&a=2&z=9',
      'HEADER:content-type:application/json; charset=utf-8',
      'HEADER:x-custom:alpha,beta',
      'SIGNED-HEADERS:content-type;x-custom',
      'BODY-SHA256:lgk5inmP_V0lvyrVO7BdMSCUI3x8gY3ZmVHmADlvzGQ'
    ].join('\n')
  );
});

test('canonicalizes form-url-encoded bodies from search params', async () => {
  const result = await canonicalizeRequest(
    {
      method: 'PUT',
      url: 'https://example.test/token',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded'
      },
      body: new URLSearchParams([
        ['scope', 'write'],
        ['scope', 'read'],
        ['grant_type', 'client_credentials']
      ])
    },
    {
      signedHeaders: ['content-type']
    }
  );

  assert.equal(
    result.canonical,
    [
      'REQSIG-V1',
      'METHOD:PUT',
      'PATH:/token',
      'QUERY:',
      'HEADER:content-type:application/x-www-form-urlencoded',
      'SIGNED-HEADERS:content-type',
      'BODY-SHA256:ktA-ZuMtnVixjTQLEHxA0pNJhQEjczsh63Os0xIWKzc'
    ].join('\n')
  );
});

test('omits missing signed headers and hashes empty bodies', async () => {
  const result = await canonicalizeRequest(
    {
      method: 'GET',
      url: 'https://example.test/ping'
    },
    {
      signedHeaders: ['x-missing']
    }
  );

  assert.equal(
    result.canonical,
    [
      'REQSIG-V1',
      'METHOD:GET',
      'PATH:/ping',
      'QUERY:',
      'SIGNED-HEADERS:',
      'BODY-SHA256:47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU'
    ].join('\n')
  );
});
