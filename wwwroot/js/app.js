/* Bindery — library + editor. Plain JS, no build step. */
(() => {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const RTL = new Set(['ar', 'ur', 'fa', 'he', 'ps', 'sd', 'ug', 'yi']);
  const CLOTHS = ['#263F7A', '#7A2630', '#25605A', '#4B3A73', '#39424F', '#7A5A26'];

  /* ------------------------------------------------------------------ helpers */

  function h(tag, props = {}, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
      else el.setAttribute(k, v === true ? '' : v);
    }
    for (const kid of kids.flat()) if (kid != null) el.append(kid);
    return el;
  }

  const hash = (s) => [...s].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 7);
  const newChapterId = () => crypto.randomUUID().replace(/-/g, '').slice(0, 8);
  const escAttr = (s) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const escHtml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

  let toastTimer;
  function toast(msg) {
    const t = $('#toast');
    t.textContent = msg;
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (t.hidden = true), 4000);
  }

  /* ---------------------------------------------------------------------- API */

  const JSON_HEADERS = { 'Content-Type': 'application/json' };

  async function request(url, opts) {
    const res = await fetch(url, opts);
    if (!res.ok) {
      let msg = res.statusText || 'Request failed';
      try { msg = (await res.json()).error || msg; } catch (_) { /* not json */ }
      throw new Error(msg);
    }
    return res.status === 204 ? null : res.json();
  }

  const api = {
    list: () => request('/api/ebooks'),
    create: (title) => request('/api/ebooks', { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify({ title }) }),
    get: (id) => request(`/api/ebooks/${id}`),
    save: (b) => request(`/api/ebooks/${b.id}`, { method: 'PUT', headers: JSON_HEADERS, body: JSON.stringify(b) }),
    remove: (id) => request(`/api/ebooks/${id}`, { method: 'DELETE' }),
    upload: (id, file) => {
      const fd = new FormData();
      fd.append('file', file);
      return request(`/api/ebooks/${id}/images`, { method: 'POST', body: fd });
    }
  };

  /* ================================================================= LIBRARY */

  function coverNode(b) {
    if (b.coverImage) {
      return h('span', { class: 'cover' }, h('img', { class: 'cover-art', src: `/api/ebooks/${b.id}/images/${b.coverImage}`, alt: '' }));
    }
    const cloth = CLOTHS[hash(b.id) % CLOTHS.length];
    return h('span', { class: 'cover' },
      h('span', { class: 'cover-cloth', style: `background-color:${cloth}` },
        h('span', { class: 'foil-title', text: b.title }),
        b.author ? h('span', { class: 'foil-author', text: b.author }) : null));
  }

  async function showLibrary() {
    showView('library');
    const shelf = $('#shelf');
    let books = [];
    try { books = await api.list(); } catch (e) { toast('Could not load your books: ' + e.message); }

    shelf.replaceChildren(
      ...books.map((b) => h('article', { class: 'book' },
        h('a', { class: 'book-link', href: `#/book/${b.id}`, 'aria-label': `Open ${b.title}` }, coverNode(b)),
        h('div', { class: 'book-meta' },
          h('div', {},
            h('div', { class: 't', text: b.title }),
            h('div', { class: 's', text: `${b.chapterCount} chapter${b.chapterCount === 1 ? '' : 's'} · ${new Date(b.updatedUtc).toLocaleDateString()}` })),
          h('button', {
            type: 'button', text: 'Delete', 'aria-label': `Delete ${b.title}`,
            onclick: async () => {
              if (!confirm(`Delete “${b.title}”? This cannot be undone.`)) return;
              try { await api.remove(b.id); showLibrary(); } catch (e) { toast(e.message); }
            }
          })))),
      h('button', { class: 'new-tile', type: 'button', onclick: openNewDialog }, 'New book'));
  }

  function openNewDialog() {
    $('#new-title').value = '';
    $('#new-dialog').showModal();
    $('#new-title').focus();
  }

  $('#btn-new').addEventListener('click', openNewDialog);
  $('#new-dialog').addEventListener('close', async (e) => {
    if (e.target.returnValue !== 'ok') return;
    try {
      const book = await api.create($('#new-title').value.trim());
      location.hash = `#/book/${book.id}`;
    } catch (err) { toast(err.message); }
  });

  /* =================================================================== EDITOR */

  let book = null;
  let cur = 0;
  let dirty = false;
  let timer = null;
  let saveChain = Promise.resolve();
  let savedRange = null;

  const editor = $('#editor');
  const titleInput = $('#chapter-title');

  function setStatus(text) { $('#save-state').textContent = text; }

  function markDirty() {
    if (!book) return;
    dirty = true;
    setStatus('Unsaved changes…');
    clearTimeout(timer);
    timer = setTimeout(saveNow, 1200);
  }

  /** Copy what's on screen into the book model. */
  function commitEditor() {
    const ch = book?.chapters[cur];
    if (!ch) return;
    ch.html = editor.innerHTML;
    ch.title = titleInput.value.trim() || 'Untitled chapter';
  }

  function saveNow() {
    clearTimeout(timer);
    saveChain = saveChain.then(async () => {
      if (!book || !dirty) return;
      commitEditor();
      dirty = false;
      setStatus('Saving…');
      try {
        await api.save(book);
        setStatus('Saved');
      } catch (e) {
        dirty = true;
        setStatus('Save failed — retrying…');
        timer = setTimeout(saveNow, 5000);
      }
    });
    return saveChain;
  }

  async function openBook(id) {
    try {
      book = await api.get(id);
    } catch (e) {
      toast('Could not open that book.');
      location.hash = '#/';
      return;
    }
    if (!book.chapters.length) book.chapters.push({ id: newChapterId(), title: 'Chapter 1', html: '' });
    cur = 0;
    dirty = false;
    setStatus('Saved');
    showView('editor');
    fillSettings();
    renderChapterList();
    loadChapter();
  }

  function applyDirection() {
    const dir = RTL.has((book.language || 'en').split('-')[0]) ? 'rtl' : 'ltr';
    editor.dir = dir;
    titleInput.dir = dir;
  }

  function loadChapter() {
    const ch = book.chapters[cur];
    titleInput.value = ch.title;
    editor.innerHTML = ch.html && ch.html.trim() ? ch.html : '<p><br></p>';
    applyDirection();
    $('.desk').scrollTop = 0;
  }

  /* ------------------------------------------------------------ chapter list */

  function renderChapterList() {
    const list = $('#chapter-list');
    list.replaceChildren(...book.chapters.map((ch, i) => {
      const li = h('li', { class: i === cur ? 'current' : '', draggable: 'true', 'data-i': i },
        h('button', { class: 'ch-open', type: 'button', text: ch.title, title: ch.title, onclick: () => selectChapter(i) }),
        h('span', { class: 'ch-actions' },
          h('button', { type: 'button', text: '↑', title: 'Move up', 'aria-label': `Move ${ch.title} up`, disabled: i === 0, onclick: () => moveChapter(i, i - 1) }),
          h('button', { type: 'button', text: '↓', title: 'Move down', 'aria-label': `Move ${ch.title} down`, disabled: i === book.chapters.length - 1, onclick: () => moveChapter(i, i + 1) }),
          h('button', { type: 'button', text: '✕', title: 'Delete chapter', 'aria-label': `Delete ${ch.title}`, disabled: book.chapters.length === 1, onclick: () => deleteChapter(i) })));

      li.addEventListener('dragstart', (e) => { e.dataTransfer.setData('text/plain', String(i)); e.dataTransfer.effectAllowed = 'move'; li.classList.add('dragging'); });
      li.addEventListener('dragend', () => li.classList.remove('dragging'));
      li.addEventListener('dragover', (e) => { e.preventDefault(); li.classList.add('drop-before'); });
      li.addEventListener('dragleave', () => li.classList.remove('drop-before'));
      li.addEventListener('drop', (e) => {
        e.preventDefault();
        li.classList.remove('drop-before');
        const from = Number(e.dataTransfer.getData('text/plain'));
        if (Number.isInteger(from) && from !== i) moveChapter(from, i);
      });
      return li;
    }));
  }

  function selectChapter(i) {
    if (i === cur) return;
    commitEditor();
    cur = i;
    loadChapter();
    renderChapterList();
  }

  function moveChapter(from, to) {
    if (to < 0 || to >= book.chapters.length) return;
    commitEditor();
    const open = book.chapters[cur];
    const [moved] = book.chapters.splice(from, 1);
    book.chapters.splice(to, 0, moved);
    cur = book.chapters.indexOf(open);
    renderChapterList();
    markDirty();
  }

  function deleteChapter(i) {
    if (book.chapters.length === 1) return;
    if (!confirm(`Delete “${book.chapters[i].title}”?`)) return;
    commitEditor();
    book.chapters.splice(i, 1);
    cur = Math.min(cur > i ? cur - 1 : cur, book.chapters.length - 1);
    loadChapter();
    renderChapterList();
    markDirty();
  }

  $('#btn-add-chapter').addEventListener('click', () => {
    commitEditor();
    book.chapters.push({ id: newChapterId(), title: `Chapter ${book.chapters.length + 1}`, html: '' });
    cur = book.chapters.length - 1;
    loadChapter();
    renderChapterList();
    markDirty();
    titleInput.select();
  });

  titleInput.addEventListener('input', () => {
    const ch = book.chapters[cur];
    ch.title = titleInput.value || 'Untitled chapter';
    const btn = $$('#chapter-list li')[cur]?.querySelector('.ch-open');
    if (btn) { btn.textContent = ch.title; btn.title = ch.title; }
    markDirty();
  });

  /* ------------------------------------------------------------------ editing */

  try { document.execCommand('defaultParagraphSeparator', false, 'p'); } catch (_) { /* ignore */ }

  editor.addEventListener('input', markDirty);

  // Paste as clean paragraphs: no Word/web junk gets into the book.
  editor.addEventListener('paste', (e) => {
    e.preventDefault();
    const text = (e.clipboardData || window.clipboardData).getData('text/plain');
    if (!text) return;
    const html = text.replace(/\r\n?/g, '\n').split(/\n{2,}/)
      .map((para) => `<p>${escHtml(para).replace(/\n/g, '<br>')}</p>`).join('');
    document.execCommand('insertHTML', false, html);
  });

  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's' && book) { e.preventDefault(); saveNow(); }
  });

  function rememberSelection() {
    const sel = getSelection();
    if (sel.rangeCount && editor.contains(sel.anchorNode)) savedRange = sel.getRangeAt(0).cloneRange();
  }
  function restoreSelection() {
    editor.focus();
    if (savedRange) { const sel = getSelection(); sel.removeAllRanges(); sel.addRange(savedRange); }
  }
  document.addEventListener('selectionchange', rememberSelection);

  function exec(cmd, value) {
    restoreSelection();
    document.execCommand(cmd, false, value);
    markDirty();
  }

  // Keep the text selection while clicking toolbar buttons.
  $('#toolbar').addEventListener('mousedown', (e) => { if (e.target.closest('button')) e.preventDefault(); });
  $('#toolbar').addEventListener('click', (e) => {
    const btn = e.target.closest('button');
    if (!btn) return;
    if (btn.dataset.cmd) return exec(btn.dataset.cmd);
    if (btn.dataset.block) {
      restoreSelection();
      const now = (document.queryCommandValue('formatBlock') || '').toLowerCase();
      const target = btn.dataset.block;
      document.execCommand('formatBlock', false, now === target ? 'p' : target);
      markDirty();
    }
  });

  $('#tb-link').addEventListener('click', () => {
    restoreSelection();
    if (getSelection().isCollapsed) return toast('Select the text you want to link first.');
    const url = prompt('Link address (https://…)');
    if (url && /^(https?:\/\/|mailto:|#)/i.test(url.trim())) exec('createLink', url.trim());
    else if (url) toast('Links must start with http://, https:// or mailto:');
  });

  $('#tb-clear').addEventListener('click', () => { exec('removeFormat'); document.execCommand('formatBlock', false, 'p'); });

  $('#tb-image').addEventListener('click', () => { rememberSelection(); $('#file-image').click(); });
  $('#file-image').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    e.target.value = '';
    if (!file) return;
    try {
      setStatus('Uploading image…');
      const { url } = await api.upload(book.id, file);
      const alt = prompt('Describe this image for screen-reader users (optional)') || '';
      exec('insertHTML', `<p><img src="${escAttr(url)}" alt="${escAttr(alt)}"></p>`);
    } catch (err) {
      toast(err.message);
      setStatus(dirty ? 'Unsaved changes…' : 'Saved');
    }
  });

  /* ----------------------------------------------------------------- settings */

  function fillSettings() {
    $('#s-title').value = book.title;
    $('#s-author').value = book.author;
    $('#s-desc').value = book.description;
    $('#s-lang').value = [...$('#s-lang').options].some((o) => o.value === book.language) ? book.language : 'en';
    $('#s-accent').value = book.theme.accent;
    $(`input[name=mode][value=${book.theme.mode}]`).checked = true;
    $(`input[name=font][value=${book.theme.font}]`).checked = true;
    $('#bar-title').textContent = book.title;
    renderCoverPreview();
  }

  function renderCoverPreview() {
    $('#cover-preview').replaceChildren(coverNode({ id: book.id, title: book.title || 'Untitled', author: '', coverImage: book.coverImage }));
    $('#btn-cover-remove').disabled = !book.coverImage;
  }

  function bind(sel, apply) {
    $(sel).addEventListener('input', (e) => { apply(e.target.value); markDirty(); });
  }
  bind('#s-title', (v) => { book.title = v; $('#bar-title').textContent = v || 'Untitled Book'; if (!book.coverImage) renderCoverPreview(); });
  bind('#s-author', (v) => { book.author = v; });
  bind('#s-desc', (v) => { book.description = v; });
  bind('#s-lang', (v) => { book.language = v; applyDirection(); });
  bind('#s-accent', (v) => { book.theme.accent = v; });
  $$('input[name=mode]').forEach((r) => r.addEventListener('change', () => { book.theme.mode = r.value; markDirty(); }));
  $$('input[name=font]').forEach((r) => r.addEventListener('change', () => { book.theme.font = r.value; markDirty(); }));

  $('#btn-cover').addEventListener('click', () => $('#file-cover').click());
  $('#file-cover').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    e.target.value = '';
    if (!file) return;
    try {
      const { fileName } = await api.upload(book.id, file);
      book.coverImage = fileName;
      renderCoverPreview();
      markDirty();
    } catch (err) { toast(err.message); }
  });
  $('#btn-cover-remove').addEventListener('click', () => { book.coverImage = null; renderCoverPreview(); markDirty(); });

  $('#btn-delete').addEventListener('click', async () => {
    if (!confirm(`Delete “${book.title}” and all of its chapters? This cannot be undone.`)) return;
    const id = book.id;
    book = null;
    clearTimeout(timer);
    dirty = false;
    try { await api.remove(id); } catch (e) { toast(e.message); }
    location.hash = '#/';
  });

  $('#btn-settings').addEventListener('click', () => {
    const open = $('#settings').classList.toggle('open');
    $('#btn-settings').setAttribute('aria-expanded', String(open));
  });

  /* --------------------------------------------------------- preview & export */

  $('#btn-preview').addEventListener('click', async () => {
    await saveNow();
    $('#pv-frame').src = `/api/ebooks/${book.id}/preview?t=${Date.now()}`;
    $('#preview-dialog').showModal();
  });
  $('#pv-close').addEventListener('click', () => $('#preview-dialog').close());
  $('#preview-dialog').addEventListener('close', () => { $('#pv-frame').src = 'about:blank'; });

  $('#export-menu').addEventListener('click', async (e) => {
    const link = e.target.closest('a[data-format]');
    if (!link) return;
    e.preventDefault();
    $('#export-menu').open = false;
    await saveNow();
    location.assign(`/api/ebooks/${book.id}/export?format=${link.dataset.format}`);
  });
  document.addEventListener('click', (e) => {
    const menu = $('#export-menu');
    if (menu.open && !menu.contains(e.target)) menu.open = false;
  });

  window.addEventListener('beforeunload', (e) => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden' && dirty) saveNow(); });

  /* ------------------------------------------------------------------ routing */

  function showView(name) {
    $('#view-library').hidden = name !== 'library';
    $('#view-editor').hidden = name !== 'editor';
    $('#settings').classList.remove('open');
    document.title = name === 'library' ? 'Bindery — write and publish e-books' : `${book?.title ?? 'Book'} — Bindery`;
  }

  async function route() {
    if (book && dirty) await saveNow();
    const m = location.hash.match(/^#\/book\/([a-f0-9]{32})$/);
    if (m) await openBook(m[1]);
    else { book = null; await showLibrary(); }
  }

  window.addEventListener('hashchange', route);
  route();
})();
