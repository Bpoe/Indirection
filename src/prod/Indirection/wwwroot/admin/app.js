/* Indirection admin: plain JavaScript, no build step or dependencies. */
'use strict';

const $ = id => document.getElementById(id);
const state = {
  apiKey: null, links: [], editing: null, toastTimer: null, session: new AbortController(),
  pendingMutation: false, editorGeneration: 0, loadGeneration: 0
};
const endpoint = '/api/v1/links';
const origin = window.location.origin;

function apiPath(code) {
  // Keep nested paths intact while encoding individual segments.
  return endpoint + '/' + code.split('/').map(encodeURIComponent).join('/');
}

async function request(path, options = {}) {
  const { signal } = state.session;
  if (!state.apiKey) throw new DOMException('Disconnected.', 'AbortError');
  const headers = { 'X-Api-Key': state.apiKey, ...options.headers };
  const response = await fetch(path, { ...options, headers, cache: 'no-store', signal });
  signal.throwIfAborted();
  if (!response.ok) {
    const error = new Error(response.status === 401
      ? 'Invalid API key.'
      : 'Request failed (' + response.status + ').');
    error.status = response.status;
    throw error;
  }
  if (response.status === 204) return null;
  const result = response.headers.get('content-type')?.includes('json')
    ? await response.json() : null;
  signal.throwIfAborted();
  return result;
}

function textNode(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  element.textContent = text;
  return element;
}

function icon(name) {
  const paths = {
    link: '<path d="M10 13 14 9M8 16l-2 2a4 4 0 0 1-6-6l5-5a4 4 0 0 1 6 0M13 8l2-2a4 4 0 0 1 6 6l-5 5a4 4 0 0 1-6 0" transform="translate(2 1) scale(.84)"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/>',
    edit: '<path d="m14 6 4 4M5 19l4-.8L19 8a2 2 0 0 0-4-4L5 14z"/>'
  };
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.8');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.classList.add('icon');
  svg.innerHTML = paths[name]; // Fixed, trusted icon markup only.
  return svg;
}

function publicUrl(code) {
  return origin + '/' + code.split('/').map(encodeURIComponent).join('/');
}

function shortDestination(destination) {
  try {
    const url = new URL(destination);
    if (url.protocol === 'http:' || url.protocol === 'https:') {
      return { top: url.hostname, bottom: url.pathname === '/'
        ? (url.search || '—') : url.pathname + url.search };
    }
  } catch { /* Custom or relative destinations are supported by the API. */ }
  return { top: destination, bottom: 'Custom destination' };
}

function showToast(message) {
  clearTimeout(state.toastTimer);
  $('toast').textContent = message;
  $('toast').hidden = false;
  state.toastTimer = setTimeout(() => { $('toast').hidden = true; }, 2900);
}

function showError(id, message) {
  $(id).textContent = message;
  $(id).hidden = !message;
}

function showApp() {
  $('login').hidden = true;
  $('app').hidden = false;
  $('headerRight').hidden = false;
  $('hostname').textContent = location.hostname;
}

function showLogin() {
  state.session.abort();
  state.session = new AbortController();
  state.loadGeneration++;
  state.apiKey = null;
  state.links = [];
  state.editing = null;
  $('app').hidden = true;
  $('headerRight').hidden = true;
  $('login').hidden = false;
  $('apiKey').value = '';
  $('apiKey').type = 'password';
  $('showKey').textContent = 'Show';
  $('showKey').setAttribute('aria-label', 'Show API key');
  $('linkRows').replaceChildren();
  $('search').value = '';
  $('editorForm').reset();
  clearTimeout(state.toastTimer);
  $('toast').hidden = true;
  if ($('editor').open) $('editor').close();
  $('apiKey').focus();
}

async function loadLinks() {
  const generation = ++state.loadGeneration;
  let response;
  try { response = await request(endpoint); }
  catch (e) {
    if (generation !== state.loadGeneration) throw new DOMException('Superseded request.', 'AbortError');
    throw e;
  }
  if (generation !== state.loadGeneration) throw new DOMException('Superseded request.', 'AbortError');
  if (!Array.isArray(response)) throw new Error('The server returned an unexpected links response.');
  state.links = response.sort((a, b) => a.code.localeCompare(b.code));
  render();
}

function render() {
  const query = $('search').value.trim().toLowerCase();
  const filtered = state.links.filter(link => link.code.toLowerCase().includes(query)
    || link.destination.toLowerCase().includes(query));
  $('count').textContent = state.links.length;
  $('resultCount').textContent = filtered.length === state.links.length
    ? state.links.length + (state.links.length === 1 ? ' link' : ' links')
    : filtered.length + ' of ' + state.links.length + ' links';
  $('linkRows').replaceChildren();

  for (const link of filtered) {
    const tr = document.createElement('tr');
    const codeCell = document.createElement('td');
    const codeWrap = textNode('div', 'code-cell', '');
    const avatar = textNode('span', 'link-avatar', '');
    avatar.append(icon('link'));
    codeWrap.append(avatar);
    const codeButton = textNode('button', 'code-name', link.code);
    codeButton.type = 'button';
    codeButton.style.cssText = 'border:0;background:none;padding:0;text-align:left';
    codeButton.title = 'Edit ' + link.code;
    codeButton.addEventListener('click', () => openEditor(link));
    codeWrap.append(codeButton);
    codeCell.append(codeWrap);

    const destinationCell = document.createElement('td');
    const parts = shortDestination(link.destination);
    const top = textNode('span', 'domain-text', parts.top);
    top.title = link.destination;
    const bottom = textNode('span', 'path-text', parts.bottom);
    bottom.title = link.destination;
    destinationCell.append(top, bottom);
    const created = textNode('td', '', link.createdUtc
      ? new Date(link.createdUtc).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
      : '—');

    const actionsCell = document.createElement('td');
    const actions = textNode('div', 'row-actions', '');
    const copy = textNode('button', 'icon-button', '');
    copy.type = 'button';
    copy.title = 'Copy short link';
    copy.setAttribute('aria-label', 'Copy ' + link.code);
    copy.append(icon('copy'));
    copy.addEventListener('click', () => copyUrl(link.code));
    const edit = textNode('button', 'icon-button', '');
    edit.type = 'button';
    edit.title = 'Edit link';
    edit.setAttribute('aria-label', 'Edit ' + link.code);
    edit.append(icon('edit'));
    edit.addEventListener('click', () => openEditor(link));
    actions.append(copy, edit);
    actionsCell.append(actions);
    tr.append(codeCell, destinationCell, created, actionsCell);
    $('linkRows').append(tr);
  }

  $('emptyState').hidden = filtered.length > 0;
  $('linkTable').hidden = filtered.length === 0;
  $('emptyTitle').textContent = state.links.length ? 'No matching links' : 'No links yet';
  $('emptySubtitle').textContent = state.links.length
    ? 'Try a different search term.' : 'Create your first link to get started.';
  $('emptyCreateBtn').hidden = state.links.length > 0;
}

async function copyUrl(code) {
  try {
    await navigator.clipboard.writeText(publicUrl(code));
    showToast('Short link copied to clipboard');
  } catch { showToast('Could not copy. Your browser may require HTTPS.'); }
}

function openEditor(link = null) {
  state.editorGeneration++;
  state.editing = link;
  $('editorForm').reset();
  showError('editorError', '');
  $('editorEyebrow').textContent = link ? 'EDIT REDIRECT' : 'CREATE REDIRECT';
  $('editorTitle').textContent = link ? 'Edit link' : 'New link';
  $('saveBtn').textContent = link ? 'Save changes' : 'Create link';
  $('saveBtn').disabled = state.pendingMutation;
  $('deleteBtn').hidden = !link;
  $('deleteBtn').disabled = state.pendingMutation;
  $('code').disabled = Boolean(link);
  $('code').value = link?.code ?? '';
  $('destination').value = link?.destination ?? '';
  $('codeHelp').textContent = link
    ? 'A link code cannot be changed after creation.'
    : 'Codes are case-insensitive and may contain nested paths.';
  updatePreview();
  $('editor').showModal();
  (link ? $('destination') : $('code')).focus();
}

function updatePreview() {
  const code = getCode();
  $('previewUrl').textContent = code ? publicUrl(code) : origin + '/your-code';
}

function getCode() {
  return state.editing?.code ?? $('code').value.trim().replace(/^\/+|\/+$/g, '');
}

function validateCode(code) {
  if (!code || code.split('/').some(segment => !segment || segment === '.' || segment === '..')) {
    return 'Enter a valid code without empty or dot-only path segments.';
  }
  if (/[?#\\]/.test(code)) return 'Codes cannot contain ?, #, or backslashes.';
  const first = code.split('/')[0].toLowerCase();
  if (first === 'api' || first === 'admin' || code.toLowerCase() === 'health') {
    return 'This path is reserved for the application.';
  }
  if (!state.editing && state.links.some(link => link.code.toLowerCase() === code.toLowerCase())) {
    return 'A link with this code already exists. Edit it instead.';
  }
  return '';
}

function setMutationPending(pending) {
  state.pendingMutation = pending;
  $('saveBtn').disabled = pending;
  $('deleteBtn').disabled = pending;
}

async function saveLink(event) {
  event.preventDefault();
  if (state.pendingMutation) return;
  const editorGeneration = state.editorGeneration;
  const code = getCode();
  const destination = $('destination').value.trim();
  const error = validateCode(code);
  if (error) { showError('editorError', error); return; }
  if (!destination) { showError('editorError', 'Enter a destination.'); return; }
  setMutationPending(true);
  showError('editorError', '');
  try {
    await request(apiPath(code), {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ destination })
    });
    await loadLinks();
    if (editorGeneration === state.editorGeneration && $('editor').open) {
      $('editor').close();
      showToast(state.editing ? 'Link updated' : 'Link created');
    }
  } catch (e) {
    if (e.name === 'AbortError') return;
    if (e.status === 401) {
      showLogin();
      showError('loginError', 'Your API key is no longer valid.');
    } else if (editorGeneration === state.editorGeneration && $('editor').open) {
      showError('editorError', e.message);
    }
  } finally { setMutationPending(false); }
}

async function deleteLink() {
  if (state.pendingMutation) return;
  const link = state.editing;
  if (!link || !confirm('Delete /' + link.code + '?\n\nThis will immediately stop the short link from redirecting.')) return;
  const editorGeneration = state.editorGeneration;
  setMutationPending(true);
  try {
    await request(apiPath(link.code), { method: 'DELETE' });
    await loadLinks();
    if (editorGeneration === state.editorGeneration && $('editor').open) {
      $('editor').close();
      showToast('Link deleted');
    }
  } catch (e) {
    if (e.name === 'AbortError') return;
    if (e.status === 401) {
      showLogin();
      showError('loginError', 'Your API key is no longer valid.');
    } else if (editorGeneration === state.editorGeneration && $('editor').open) {
      showError('editorError', e.message);
    }
  } finally { setMutationPending(false); }
}

$('loginForm').addEventListener('submit', async event => {
  event.preventDefault();
  const apiKey = $('apiKey').value;
  showLogin();
  state.apiKey = apiKey;
  $('connectBtn').disabled = true;
  $('connectBtn').textContent = 'Connecting…';
  showError('loginError', '');
  try { await loadLinks(); showApp(); }
  catch (e) {
    if (e.name === 'AbortError') return;
    state.apiKey = null;
    showError('loginError', e.message || 'Unable to connect.');
  } finally {
    $('connectBtn').disabled = false;
    $('connectBtn').textContent = 'Connect →';
  }
});
$('showKey').addEventListener('click', () => {
  const showing = $('apiKey').type === 'text';
  $('apiKey').type = showing ? 'password' : 'text';
  $('showKey').textContent = showing ? 'Show' : 'Hide';
  $('showKey').setAttribute('aria-label', showing ? 'Show API key' : 'Hide API key');
});
$('disconnectBtn').addEventListener('click', showLogin);
$('search').addEventListener('input', render);
$('refreshBtn').addEventListener('click', async () => {
  $('refreshBtn').disabled = true;
  try { await loadLinks(); showToast('Links refreshed'); }
  catch (e) {
    if (e.name === 'AbortError') return;
    if (e.status === 401) {
      showLogin();
      showError('loginError', 'Your API key is no longer valid.');
    } else showToast(e.message);
  } finally { $('refreshBtn').disabled = false; }
});
$('createBtn').addEventListener('click', () => openEditor());
$('emptyCreateBtn').addEventListener('click', () => openEditor());
$('closeEditor').addEventListener('click', () => $('editor').close());
$('cancelEditor').addEventListener('click', () => $('editor').close());
$('editorForm').addEventListener('submit', saveLink);
$('deleteBtn').addEventListener('click', deleteLink);
$('code').addEventListener('input', updatePreview);
document.addEventListener('keydown', event => {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k' && !$('app').hidden) {
    event.preventDefault();
    $('search').focus();
  }
});
$('apiKey').focus();
