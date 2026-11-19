// 1Candbox, главная страница: редактор CodeMirror + движок OneScript (.NET WebAssembly) в Web Worker.
import { Engine } from './engine.js';
import { createEditor, isDark } from './editor.js';
import { findHints } from './rules.js';
import { renderOutput, showPlaceholder, resultMeta } from './output.js';
import { encodeState, decodeState } from './share.js';
import { EXAMPLES } from './examples.js';

const DRAFT_KEY = 'bsl-sandbox-draft-v1';
const THEME_KEY = 'bsl-sandbox-theme';

const $ = (id) => document.getElementById(id);
const els = {
  editor: $('editor'),
  run: $('run-btn'),
  share: $('share-btn'),
  themeButtons: document.querySelectorAll('[data-theme-choice]'),
  input: $('stdin'),
  output: $('output'),
  status: $('engine-status'),
  statusText: $('engine-status-text'),
  meta: $('run-meta'),
  hints: $('hints'),
  clear: $('clear-btn'),
  dialog: $('share-dialog'),
  shareLink: $('share-link'),
  shareEmbed: $('share-embed'),
  shareComponent: $('share-component'),
  shareNote: $('share-note'),
};

// ---------- Тема ----------
// Пока пользователь не выбрал тему, действует системная. Выбор хранится в localStorage
// (тот же ключ читает скрипт в <head>, чтобы тема применялась до отрисовки).
const systemDark = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

function syncThemeButtons() {
  const current = isDark() ? 'dark' : 'light';
  for (const btn of els.themeButtons) btn.setAttribute('aria-pressed', String(btn.dataset.themeChoice === current));
}
function setTheme(theme) {
  document.documentElement.dataset.theme = theme;
  try { localStorage.setItem(THEME_KEY, theme); } catch { /* без хранилища тема просто не запомнится */ }
  syncThemeButtons();
}
for (const btn of els.themeButtons) btn.addEventListener('click', () => setTheme(btn.dataset.themeChoice));
if (systemDark) systemDark.addEventListener('change', syncThemeButtons);
syncThemeButtons();

// ---------- Черновик ----------
function loadDraft() {
  try {
    const raw = localStorage.getItem(DRAFT_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch { return null; }
}
function saveDraft() {
  try {
    localStorage.setItem(DRAFT_KEY, JSON.stringify({ code: editor.getCode(), input: els.input.value }));
  } catch { /* хранилище недоступно — черновик просто не сохранится */ }
}

// Адрес с #c=… описывает код до первой правки. После правки убираем его из строки адреса,
// чтобы скопированный из неё адрес не открывал старую версию; при обновлении страницы
// код восстановится из черновика. linked — программа, которую сейчас описывает адрес.
let linked = null;
function dropHashIfChanged() {
  if (linked && editor.getCode() === linked.code && els.input.value === (linked.input || '')) return;
  linked = null;
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);
}

// Код из ссылки: #c=… (поделиться) или #ex=<id> (пример со страницы примеров).
// Возвращает {code, input, note} или null.
async function programFromHash() {
  if (!location.hash) return null;
  let state;
  try {
    state = await decodeState(location.hash);
  } catch (e) {
    return { error: 'Не удалось открыть код из ссылки: ' + e.message + '. Возможно, ссылка обрезана при копировании.' };
  }
  if (state.code != null) {
    return { code: state.code, input: state.input, note: 'Код открыт по ссылке. Нажмите «Запустить» или F5.' };
  }
  const ex = state.example && EXAMPLES.find((e) => e.id === state.example);
  if (ex) return { code: ex.code, input: ex.input, note: `Пример «${ex.title}». Нажмите «Запустить» или F5.` };
  return null;
}

// ---------- Движок ----------
function setStatus(state, text) {
  els.status.dataset.state = state;
  els.statusText.textContent = text;
  els.run.disabled = state === 'loading' || state === 'running' || state === 'fatal';
}

const engine = new Engine({
  workerUrl: new URL('./worker.js', document.baseURI),
  onStatus: setStatus,
  onReady: () => editor.relint(),
});

// ---------- Редактор ----------
// Ссылку разбираем до создания редактора, чтобы не мелькал черновик
const fromLink = await programFromHash();
const draft = loadDraft();
const initial = fromLink && fromLink.code != null ? fromLink : (draft && draft.code ? draft : EXAMPLES[0]);
if (fromLink && fromLink.code != null) linked = { code: initial.code, input: initial.input || '' };
els.input.value = initial.input || '';

const editor = createEditor({
  parent: els.editor,
  doc: initial.code,
  engine,
  onChange: () => { saveDraft(); updateHints(); dropHashIfChanged(); },
});
els.input.addEventListener('input', () => {
  clearTimeout(els.input.saveTimer);
  els.input.saveTimer = setTimeout(() => { saveDraft(); dropHashIfChanged(); }, 400);
});

function loadProgram({ code, input }) {
  editor.setCode(code);
  els.input.value = input || '';
  updateHints();
  saveDraft();
}

// Новый адрес на уже открытой странице (вставили ссылку в адресную строку)
window.addEventListener('hashchange', async () => {
  const p = await programFromHash();
  if (!p) return;
  if (p.error) { showPlaceholder(els.output, p.error); return; }
  linked = { code: p.code, input: p.input || '' };
  loadProgram(p);
  els.meta.textContent = '';
  showPlaceholder(els.output, p.note);
});

// ---------- Примеры ----------
// Ссылки на примеры в подвале ведут на их страницы (для поисковиков и новой вкладки),
// а обычный клик на главной загружает пример прямо в редактор.
document.addEventListener('click', (e) => {
  const link = e.target.closest('a[data-ex]');
  if (!link || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
  const ex = EXAMPLES.find((x) => x.id === link.dataset.ex);
  if (!ex) return;
  e.preventDefault();
  loadProgram(ex);
  dropHashIfChanged();
  els.meta.textContent = '';
  showPlaceholder(els.output, `Загружен пример «${ex.title}». Нажмите «Запустить» или F5.`);
  window.scrollTo({ top: 0, behavior: 'smooth' });
  editor.view.contentDOM.focus({ preventScroll: true });
});

// ---------- Подсказки о совместимости ----------
function updateHints() {
  const found = findHints(editor.getCode());
  els.hints.replaceChildren(...found.map((text) => {
    const li = document.createElement('li');
    li.textContent = text;
    return li;
  }));
  els.hints.hidden = found.length === 0;
}

// ---------- Запуск ----------
async function runCode() {
  if (!engine.ready || engine.busy) return;
  saveDraft();
  editor.clearRuntimeError();
  els.meta.textContent = '';
  showPlaceholder(els.output, 'Выполняется…');
  const res = await engine.run(editor.getCode(), els.input.value);
  if (!res) return;
  renderOutput(els.output, res.output, res.exitCode, (n) => editor.gotoLine(n));
  editor.showRuntimeError(res.exitCode === 0 ? [] : res.output);
  els.meta.textContent = resultMeta(res);
  editor.relint();
}
els.run.addEventListener('click', runCode);

// F5 запускает код в любом месте страницы (вместо обновления страницы, которое стёрло бы вывод)
window.addEventListener('keydown', (e) => {
  if (e.key !== 'F5' || e.ctrlKey || e.metaKey || e.altKey || e.shiftKey) return;
  e.preventDefault();
  runCode();
}, true);

els.clear.addEventListener('click', () => {
  els.meta.textContent = '';
  showPlaceholder(els.output, 'Вывод очищен.');
});

// ---------- Поделиться ----------
// Длинные ссылки обрезают некоторые мессенджеры и форумы
const LONG_LINK = 6000;

const escapeHtml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

async function openShare() {
  const code = editor.getCode();
  const input = els.input.value;
  const hash = await encodeState({ code, input });
  const base = new URL('.', location.href);
  const link = new URL('#' + hash, location.href.split('#')[0]).href;
  const embedUrl = new URL('embed.html#' + hash, base).href;
  // Высота виджета по числу строк: панель, редактор, вывод
  const lines = Math.min(Math.max(code.split('\n').length, 6), 22);
  const height = Math.round(48 + lines * 19.6 + 16 + 170);
  history.replaceState(null, '', '#' + hash);
  linked = { code, input };

  els.shareLink.value = link;
  els.shareEmbed.value =
    `<iframe src="${escapeHtml(embedUrl)}" width="100%" height="${height}" style="border:0;border-radius:8px"` +
    ` title="1Candbox: пример кода" loading="lazy"></iframe>`;
  const inputPart = input ? `\n<script type="text/bsl-input">\n${input.replace(/<\/script/gi, '<\\/script')}\n</script>` : '';
  els.shareComponent.value =
    `<script src="${escapeHtml(new URL('embed.js', base).href)}" defer></script>\n` +
    `<bsl-sandbox>\n<script type="text/bsl">\n${code.replace(/<\/script/gi, '<\\/script')}\n</script>${inputPart}\n</bsl-sandbox>`;
  els.shareNote.textContent = link.length > LONG_LINK
    ? `Ссылка длинная (${link.length} символов): некоторые форумы и мессенджеры её обрезают. Для большого кода лучше встроить виджет.`
    : 'Код и ввод хранятся в самой ссылке: на сервер они не отправляются.';

  if (!els.dialog.open) els.dialog.showModal();
  els.shareLink.focus();
  els.shareLink.select();
  copy(els.shareLink.value, els.dialog.querySelector('[data-copy="share-link"]'));
}

async function copy(text, button) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    return; // нет доступа к буферу (например, не https): текст выделен, его можно скопировать вручную
  }
  if (!button) return;
  const label = button.dataset.label || button.textContent;
  button.dataset.label = label;
  button.textContent = 'Скопировано';
  clearTimeout(button.resetTimer);
  button.resetTimer = setTimeout(() => { button.textContent = label; }, 1600);
}

els.share.addEventListener('click', openShare);
for (const btn of els.dialog.querySelectorAll('[data-copy]')) {
  btn.addEventListener('click', () => {
    const field = $(btn.dataset.copy);
    field.select();
    copy(field.value, btn);
  });
}
// Клик по затемнению вокруг окна закрывает его
els.dialog.addEventListener('click', (e) => { if (e.target === els.dialog) els.dialog.close(); });

// ---------- Старт ----------
updateHints();
showPlaceholder(els.output, fromLink ? (fromLink.error || fromLink.note) : 'Нажмите «Запустить» или F5.');
if (fromLink && fromLink.code != null) saveDraft();
engine.start();
