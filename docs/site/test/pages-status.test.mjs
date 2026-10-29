import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
import { pagesEnabled } from '../scripts/pages-status.mjs';

async function response(status, body, action) {
  const requests = [];
  const server = createServer((req, res) => {
    requests.push({ method: req.method, url: req.url });
    res.writeHead(status, { 'content-type': 'application/json' });
    res.end(body);
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  try {
    await action(`http://127.0.0.1:${server.address().port}/`);
    assert.deepEqual(requests, [{ method: 'GET', url: '/repos/owner/repo/pages' }]);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

for (const [status, body, enabled] of [
  [200, '{"build_type":"workflow"}', true],
  [200, '{"build_type":"legacy"}', false],
  [404, '{"message":"Not Found"}', false],
]) {
  test(`Pages inspection ${status} ${body} yields ${enabled} without writes`, async () => {
    await response(status, body, async (apiUrl) => {
      assert.equal(await pagesEnabled({ apiUrl, repository: 'owner/repo', token: 'test' }), enabled);
    });
  });
}

for (const [status, body] of [
  [401, '{}'], [403, '{}'], [429, '{}'], [500, '{}'],
  [200, 'not JSON'], [200, '{}'], [200, '{"build_type":"unknown"}'],
  [302, '{}'],
]) {
  test(`Pages inspection fails closed for ${status} ${body}`, async () => {
    await response(status, body, async (apiUrl) => {
      await assert.rejects(pagesEnabled({ apiUrl, repository: 'owner/repo', token: 'test' }));
    });
  });
}

test('Pages inspection requires credentials instead of guessing enabled', async () => {
  await assert.rejects(pagesEnabled({ apiUrl: 'https://api.github.com/', repository: 'owner/repo' }), /token/i);
});
