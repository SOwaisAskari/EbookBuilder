/* Reader for exported e-books: chapters, contents, search, themes, progress memory. Zero dependencies. */
(() => {
  'use strict';

  const $ = (sel) => document.querySelector(sel);
  const root = document.documentElement;
  const body = document.body;
  const sections = Array.from(document.querySelectorAll('.chapter'));
  const tocLinks = Array.from(document.querySelectorAll('#toc-list a'));
  const prevBtn = $('#prev');
  const nextBtn = $('#next');
  const posLabel = $('#pos');
  const progress = $('#progress');
  const rtl = root.getAttribute('dir') === 'rtl';
  const THEMES = ['light', 'sepia', 'dark'];
  const KEY = 'ebook:' + (body.dataset.bookId || 'book');

  /* ---------- persisted preferences ---------- */
  const state = { chapter: null, theme: root.dataset.theme, font: root.dataset.font, scale: 1 };
  try { Object.assign(state, JSON.parse(localStorage.getItem(KEY) || '{}')); } catch (_) { /* private mode */ }
  const save = () => { try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (_) { /* ignore */ } };

  function applyPrefs() {
    if (!THEMES.includes(state.theme)) state.theme = 'light';
    root.dataset.theme = state.theme;
    root.dataset.font = state.font === 'sans' ? 'sans' : 'serif';
    root.style.setProperty('--scale', String(state.scale));
  }

  /* ---------- chapter navigation ---------- */
  let current = 0;

  function show(id) {
    let idx = sections.findIndex((s) => s.id === id);
    if (idx < 0) idx = 0;
    current = idx;

    sections.forEach((s, i) => s.classList.toggle('active', i === idx));
    tocLinks.forEach((a, i) => {
      a.classList.toggle('current', i === idx);
      if (i === idx) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    });

    if (prevBtn) prevBtn.disabled = idx === 0;
    if (nextBtn) nextBtn.disabled = idx === sections.length - 1;
    if (posLabel) posLabel.textContent = (idx + 1) + ' / ' + sections.length;

    state.chapter = sections[idx].id;
    save();
    window.scrollTo(0, 0);
    updateProgress();
    if (window.innerWidth < 900) body.classList.remove('toc-open');
  }

  function go(idx) {
    if (idx < 0 || idx >= sections.length) return;
    if (location.hash === '#' + sections[idx].id) show(sections[idx].id);
    else location.hash = sections[idx].id;
  }

  function fromHash() {
    const id = decodeURIComponent(location.hash.slice(1));
    if (sections.some((s) => s.id === id)) show(id);
  }

  function updateProgress() {
    if (!progress) return;
    const max = root.scrollHeight - window.innerHeight;
    progress.style.width = (max > 0 ? Math.min(100, (window.scrollY / max) * 100) : 0) + '%';
  }

  /* ---------- search ---------- */
  const searchInput = $('#search-input');
  const searchResults = $('#search-results');
  const tocList = $('#toc-list');
  const index = sections.map((s) => ({
    id: s.id,
    title: s.dataset.title || '',
    text: s.textContent.replace(/\s+/g, ' ').trim()
  }));

  function runSearch() {
    const q = searchInput.value.trim();
    searchResults.textContent = '';
    if (q.length < 2) { searchResults.hidden = true; tocList.hidden = false; return; }

    const needle = q.toLowerCase();
    let total = 0;
    for (const item of index) {
      const hay = item.text.toLowerCase();
      let from = 0, shown = 0;
      while (shown < 3 && total < 40) {
        const at = hay.indexOf(needle, from);
        if (at < 0) break;
        const li = document.createElement('li');
        const a = document.createElement('a');
        a.href = '#' + item.id;
        const t = document.createElement('span');
        t.className = 'hit-title';
        t.textContent = item.title;
        a.appendChild(t);
        a.appendChild(document.createTextNode('…' + item.text.slice(Math.max(0, at - 40), at)));
        const mark = document.createElement('mark');
        mark.textContent = item.text.slice(at, at + needle.length);
        a.appendChild(mark);
        a.appendChild(document.createTextNode(item.text.slice(at + needle.length, at + needle.length + 60) + '…'));
        li.appendChild(a);
        searchResults.appendChild(li);
        from = at + needle.length;
        shown++; total++;
      }
    }
    if (!total) {
      const li = document.createElement('li');
      li.className = 'none';
      li.textContent = 'No matches.';
      searchResults.appendChild(li);
    }
    searchResults.hidden = false;
    tocList.hidden = true;
  }

  /* ---------- wire up ---------- */
  applyPrefs();
  body.classList.toggle('toc-open', window.innerWidth >= 900);

  $('#toc-toggle')?.addEventListener('click', () => {
    const open = body.classList.toggle('toc-open');
    $('#toc-toggle').setAttribute('aria-expanded', String(open));
  });
  $('#size-up')?.addEventListener('click', () => { state.scale = Math.min(1.6, +(state.scale + 0.1).toFixed(2)); applyPrefs(); save(); updateProgress(); });
  $('#size-down')?.addEventListener('click', () => { state.scale = Math.max(0.8, +(state.scale - 0.1).toFixed(2)); applyPrefs(); save(); updateProgress(); });
  $('#theme-btn')?.addEventListener('click', () => { state.theme = THEMES[(THEMES.indexOf(state.theme) + 1) % THEMES.length]; applyPrefs(); save(); });
  $('#font-btn')?.addEventListener('click', () => { state.font = state.font === 'sans' ? 'serif' : 'sans'; applyPrefs(); save(); });
  prevBtn?.addEventListener('click', () => go(current - 1));
  nextBtn?.addEventListener('click', () => go(current + 1));
  searchInput?.addEventListener('input', runSearch);

  searchResults?.addEventListener('click', (e) => {
    if (e.target.closest('a') && window.innerWidth < 900) body.classList.remove('toc-open');
  });

  window.addEventListener('hashchange', fromHash);
  window.addEventListener('scroll', updateProgress, { passive: true });
  window.addEventListener('resize', updateProgress);

  document.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.target.closest?.('input, textarea, select')) return;
    if (e.key === 'ArrowRight') go(current + (rtl ? -1 : 1));
    else if (e.key === 'ArrowLeft') go(current + (rtl ? 1 : -1));
    else if (e.key === 'Escape' && window.innerWidth < 900) body.classList.remove('toc-open');
  });

  const hashId = decodeURIComponent(location.hash.slice(1));
  show(sections.some((s) => s.id === hashId) ? hashId : (state.chapter || sections[0]?.id));
})();
