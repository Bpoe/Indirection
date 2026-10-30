const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { runInNewContext } = require('node:vm');
const { resolve } = require('node:path');

// Exercise the shipped script with a small DOM adapter, without frontend packages.
function createApp(fetch) {
  const elements = new Map();
  const element = () => ({
    value: '', hidden: false, open: false, children: [], listeners: {}, style: {},
    classList: { add() {} },
    addEventListener(name, callback) { this.listeners[name] = callback; },
    setAttribute() {}, focus() {}, reset() {},
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
    showModal() { this.open = true; }, close() { this.open = false; }
  });
  const get = id => {
    if (!elements.has(id)) elements.set(id, element());
    return elements.get(id);
  };
  let copied;
  const context = {
    document: { getElementById: get, createElement: element, createElementNS: element, addEventListener() {} },
    location: { origin: 'https://short.example', hostname: 'short.example' },
    fetch, AbortController, DOMException, URL,
    navigator: { clipboard: { async writeText(value) { copied = value; } } },
    confirm: () => true, setTimeout: () => 1, clearTimeout() {}
  };
  context.window = { location: context.location };
  runInNewContext(readFileSync(resolve(__dirname, '../../prod/Indirection/wwwroot/admin/app.js'), 'utf8'), context);
  return {
    get, copied: () => copied,
    async event(id, name = 'click') { await get(id).listeners[name]({ preventDefault() {} }); }
  };
}
const json = body => ({ ok: true, status: 200, headers: new Headers({ 'content-type': 'application/json' }), json: async () => body });
const noContent = { ok: true, status: 204 };
function deferred() {
  let resolve;
  const promise = new Promise(r => { resolve = r; });
  return { promise, resolve };
}

test('connect, search, copy, create, edit and delete use the existing management API', async () => {
  const links = [{ code: 'docs/start', destination: 'https://example.com/guide', createdUtc: '2026-10-29T00:00:00Z' }];
  const calls = [];
  const app = createApp(async (path, options) => {
    calls.push({ path, ...options });
    assert.equal(options.headers['X-Api-Key'], 'test-key');
    if (!options.method) return json([...links]);
    if (options.method === 'PUT') {
      const code = decodeURIComponent(path.slice('/api/v1/links/'.length));
      const destination = JSON.parse(options.body).destination;
      const existing = links.find(link => link.code === code);
      if (existing) existing.destination = destination;
      else links.push({ code, destination });
    } else if (options.method === 'DELETE') links.splice(links.findIndex(link => path.endsWith(link.code)), 1);
    return noContent;
  });
  app.get('apiKey').value = 'test-key';
  await app.event('loginForm', 'submit');
  assert.equal(app.get('app').hidden, false);
  assert.equal(app.get('apiKey').value, '');
  assert.equal(app.get('linkRows').children.length, 1);

  app.get('search').value = 'missing';
  await app.event('search', 'input');
  assert.equal(app.get('linkRows').children.length, 0);
  app.get('search').value = 'guide';
  await app.event('search', 'input');
  assert.equal(app.get('linkRows').children.length, 1);
  const row = app.get('linkRows').children[0];
  await row.children[3].children[0].children[0].listeners.click();
  assert.equal(app.copied(), 'https://short.example/docs/start');

  await app.event('createBtn');
  app.get('code').value = 'team/hello world';
  app.get('destination').value = 'https://example.com/new';
  await app.event('editorForm', 'submit');
  assert.equal(calls.find(call => call.method === 'PUT').path, '/api/v1/links/team/hello%20world');
  assert.equal(links.length, 2);
  await row.children[3].children[0].children[1].listeners.click();
  assert.equal(app.get('code').disabled, true);
  app.get('code').value = 'docs/start'; // The DOM adapter does not implement form inputs.
  app.get('destination').value = 'https://example.com/updated';
  await app.event('editorForm', 'submit');
  assert.equal(links[0].destination, 'https://example.com/updated');
  await app.event('deleteBtn');
  assert.equal(links.length, 1);
  assert.equal(calls.find(call => call.method === 'DELETE').path, '/api/v1/links/docs/start');
});

test('disconnect aborts a refresh and ignores a late response body', async () => {
  const body = deferred();
  let calls = 0;
  let refreshSignal;
  const app = createApp(async (_, options) => {
    if (++calls === 1) return json([]);
    refreshSignal = options.signal;
    return { ...json([]), json: () => body.promise };
  });
  app.get('apiKey').value = 'test-key';
  await app.event('loginForm', 'submit');
  const refresh = app.event('refreshBtn');
  await Promise.resolve();
  await app.event('disconnectBtn');
  assert.equal(refreshSignal.aborted, true);
  body.resolve([{ code: 'private', destination: 'https://example.com' }]);
  await refresh;
  assert.equal(app.get('app').hidden, true);
  assert.equal(app.get('login').hidden, false);
  assert.equal(app.get('linkRows').children.length, 0);
  assert.equal(app.get('toast').hidden, true);
});

test('an expired key disconnects and clears rendered data', async () => {
  let calls = 0;
  const app = createApp(async () => ++calls === 1
    ? json([{ code: 'private', destination: 'https://example.com' }])
    : { ok: false, status: 401 });
  app.get('apiKey').value = 'expired-key';
  await app.event('loginForm', 'submit');
  await app.event('refreshBtn');
  assert.equal(app.get('login').hidden, false);
  assert.equal(app.get('linkRows').children.length, 0);
  assert.match(app.get('loginError').textContent, /no longer valid/);
});

test('a pending login cannot reconnect after disconnect', async () => {
  const response = deferred();
  const app = createApp(() => response.promise);
  app.get('apiKey').value = 'test-key';
  const login = app.event('loginForm', 'submit');
  await app.event('disconnectBtn');
  response.resolve(json([]));
  await login;
  assert.equal(app.get('app').hidden, true);
  assert.equal(app.get('login').hidden, false);
});

test('invalid keys and reserved paths produce errors without writing a link', async () => {
  const calls = [];
  const app = createApp(async (path, options) => {
    calls.push({ path, ...options });
    return options.headers['X-Api-Key'] === 'wrong' ? { ok: false, status: 401 } : json([]);
  });
  app.get('apiKey').value = 'wrong';
  await app.event('loginForm', 'submit');
  assert.match(app.get('loginError').textContent, /Invalid API key/);
  assert.equal(app.get('app').hidden, true);
  app.get('apiKey').value = 'test-key';
  await app.event('loginForm', 'submit');
  await app.event('createBtn');
  for (const code of ['admin', 'ADMIN/nested', 'api/v1/links', 'health', 'a/../b', 'a//b', 'a?b']) {
    app.get('code').value = code;
    app.get('destination').value = 'https://example.com';
    await app.event('editorForm', 'submit');
    assert.equal(app.get('editorError').hidden, false, code);
  }
  assert.equal(calls.filter(call => call.method).length, 0);
});

async function connect(app) {
  app.get('apiKey').value = 'test-key';
  await app.event('loginForm', 'submit');
}
async function editRow(app, index = 0) {
  await app.get('linkRows').children[index].children[3].children[0].children[1].listeners.click();
}
const existingLink = { code: 'docs/start', destination: 'https://example.com/guide' };

for (const staleResponse of ['snapshot', 'unauthorized']) {
  test(`a superseded refresh ${staleResponse} cannot undo a completed creation`, async () => {
    const oldRefresh = deferred();
    let reads = 0;
    const app = createApp(async (_, options) => {
      if (options.method === 'PUT') return noContent;
      if (++reads === 2) return oldRefresh.promise;
      return json(reads === 1 ? [existingLink] : [existingLink, { code: 'new', destination: 'https://example.com/new' }]);
    });
    await connect(app);
    const refresh = app.event('refreshBtn');
    await app.event('createBtn');
    app.get('code').value = 'new';
    app.get('destination').value = 'https://example.com/new';
    await app.event('editorForm', 'submit');
    assert.equal(app.get('linkRows').children.length, 2);
    oldRefresh.resolve(staleResponse === 'snapshot' ? json([existingLink]) : { ok: false, status: 401 });
    await refresh;
    assert.equal(app.get('linkRows').children.length, 2);
    assert.equal(app.get('app').hidden, false);
    assert.equal(app.get('toast').textContent, 'Link created');
  });
}

for (const operation of ['save', 'delete']) {
  test(`a pending ${operation} prevents competing writes`, async () => {
    const mutation = deferred();
    const writes = [];
    const app = createApp(async (_, options) => {
      if (!options.method) return json([existingLink]);
      writes.push(options.method);
      return mutation.promise;
    });
    await connect(app);
    await editRow(app);
    const pending = operation === 'save' ? app.event('editorForm', 'submit') : app.event('deleteBtn');
    assert.equal(app.get('saveBtn').disabled, true);
    assert.equal(app.get('deleteBtn').disabled, true);
    // Invoke handlers directly too: disabled controls alone are insufficient.
    await app.event('editorForm', 'submit');
    await app.event('deleteBtn');
    assert.deepEqual(writes, [operation === 'save' ? 'PUT' : 'DELETE']);
    mutation.resolve(noContent);
    await pending;
    assert.equal(app.get('saveBtn').disabled, false);
    assert.equal(app.get('deleteBtn').disabled, false);
  });
}

for (const outcome of ['success', 'failure']) {
  test(`a previous save's ${outcome} cannot affect a reopened editor`, async () => {
    const mutation = deferred();
    const app = createApp(async (_, options) => options.method ? mutation.promise : json([existingLink]));
    await connect(app);
    await editRow(app);
    const save = app.event('editorForm', 'submit');
    await app.event('cancelEditor');
    await app.event('createBtn');
    app.get('code').value = 'another';
    app.get('destination').value = 'https://example.com/another';
    assert.equal(app.get('saveBtn').disabled, true);
    mutation.resolve(outcome === 'success' ? noContent : { ok: false, status: 500 });
    await save;
    assert.equal(app.get('editor').open, true);
    assert.equal(app.get('code').value, 'another');
    assert.equal(app.get('destination').value, 'https://example.com/another');
    assert.equal(app.get('editorError').hidden, true);
    assert.equal(app.get('saveBtn').disabled, false);
  });
}

test('editing preserves an existing alias exactly in previews and write paths', async () => {
  const link = { code: 'docs ', destination: 'https://example.com/original' };
  const writes = [];
  const app = createApp(async (path, options) => {
    if (!options.method) return json([link]);
    writes.push({ path, ...options });
    return noContent;
  });
  await connect(app);
  await editRow(app);
  assert.equal(app.get('previewUrl').textContent, 'https://short.example/docs%20');
  app.get('code').value = 'docs'; // Even a changed disabled input must not rename the alias.
  app.get('destination').value = 'https://example.com/updated';
  await app.event('editorForm', 'submit');
  assert.equal(writes[0].path, '/api/v1/links/docs%20');
  assert.equal(JSON.parse(writes[0].body).destination, 'https://example.com/updated');
  await editRow(app);
  await app.event('deleteBtn');
  assert.equal(writes[1].path, '/api/v1/links/docs%20');
});
