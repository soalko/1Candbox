// Песочница 1С: редактор CodeMirror + движок OneScript (.NET WebAssembly) в Web Worker.
import { EditorView, basicSetup } from 'codemirror';
import { keymap } from '@codemirror/view';
import { EditorState, Compartment } from '@codemirror/state';
import { indentUnit } from '@codemirror/language';
import { indentWithTab } from '@codemirror/commands';
import { bsl } from '@1c-syntax/codemirror-lang-bsl';
import { EXAMPLES } from './examples.js';

const TIMEOUT_MS = 8000;
const DRAFT_KEY = 'bsl-sandbox-draft-v1';

const $ = (id) => document.getElementById(id);
const els = {
  editor: $('editor'),
  run: $('run-btn'),
  examples: $('example-select'),
  copy: $('copy-btn'),
  input: $('stdin'),
  output: $('output'),
  status: $('engine-status'),
  statusText: $('engine-status-text'),
  meta: $('run-meta'),
  hints: $('hints'),
  clear: $('clear-btn'),
};

// ---------- Редактор ----------
const themeCompartment = new Compartment();
const isDark = () => {
  const t = document.documentElement.getAttribute('data-theme');
  if (t) return t === 'dark';
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
};
const editorTheme = () => EditorView.theme({}, { dark: isDark() });

function loadDraft() {
  try {
    const raw = localStorage.getItem(DRAFT_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch { return null; }
}
function saveDraft() {
  try {
    localStorage.setItem(DRAFT_KEY, JSON.stringify({ code: view.state.doc.toString(), input: els.input.value }));
  } catch { /* хранилище недоступно — черновик просто не сохранится */ }
}

const draft = loadDraft();
const initial = draft && draft.code ? draft : EXAMPLES[0];
els.input.value = initial.input || '';

let saveTimer = null;
const view = new EditorView({
  parent: els.editor,
  state: EditorState.create({
    doc: initial.code,
    extensions: [
      basicSetup,
      bsl(),
      indentUnit.of('    '),
      keymap.of([
        { key: 'Mod-Enter', run: () => { runCode(); return true; } },
        { key: 'F5', run: () => { runCode(); return true; } },
        indentWithTab,
      ]),
      themeCompartment.of(editorTheme()),
      EditorView.updateListener.of((u) => {
        if (u.docChanged) {
          clearTimeout(saveTimer);
          saveTimer = setTimeout(() => { saveDraft(); updateHints(); }, 400);
        }
      }),
    ],
  }),
});
new MutationObserver(() => view.dispatch({ effects: themeCompartment.reconfigure(editorTheme()) }))
  .observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
els.input.addEventListener('input', () => { clearTimeout(saveTimer); saveTimer = setTimeout(saveDraft, 400); });

// ---------- Примеры ----------
for (const ex of EXAMPLES) {
  const opt = document.createElement('option');
  opt.value = ex.id;
  opt.textContent = ex.title;
  els.examples.appendChild(opt);
}
els.examples.addEventListener('change', () => {
  const ex = EXAMPLES.find((e) => e.id === els.examples.value);
  if (!ex) return;
  view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: ex.code } });
  els.input.value = ex.input || '';
  els.examples.value = '';
  saveDraft();
  updateHints();
  view.focus();
});

els.copy.addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(view.state.doc.toString());
    flash(els.copy, 'Скопировано');
  } catch {
    flash(els.copy, 'Не удалось');
  }
});
function flash(btn, text) {
  const old = btn.textContent;
  btn.textContent = text;
  setTimeout(() => { btn.textContent = old; }, 1400);
}

els.clear.addEventListener('click', () => {
  els.output.replaceChildren();
  els.meta.textContent = '';
  showPlaceholder('Вывод очищен.');
});

// ---------- Подсказки о совместимости ----------
const RULES = [
  { re: /Новый\s+Запрос|ВЫБРАТЬ\s/i, text: 'Запросы к базе данных выполняются только в платформе 1С: здесь нет информационной базы.' },
  { re: /\b(Справочники|Документы|РегистрыСведений|РегистрыНакопления|РегистрыБухгалтерии|Перечисления|Константы|ПланыСчетов|ПланыВидовХарактеристик|БизнесПроцессы|ЖурналыДокументов|Обработки|Отчеты)\./, text: 'Объекты конфигурации (справочники, документы, регистры) существуют только внутри конкретной базы 1С.' },
  { re: /&(НаСервере|НаКлиенте|НаСервереБезКонтекста|НаКлиентеНаСервере)/, text: 'Директивы &НаКлиенте/&НаСервере здесь не разделяют код: всё выполняется в одном месте.' },
  { re: /\b(ОбщегоНазначения|ОбщегоНазначенияКлиент|ОбщегоНазначенияКлиентСервер|СтроковыеФункцииКлиентСервер|СтроковыеФункции|ОбщегоНазначенияСервер)\./, text: 'Это модули БСП (Библиотеки стандартных подсистем). В песочнице их нет — используйте функции платформы: СтрРазделить, СтрСоединить, СтрШаблон.' },
  { re: /\b(ЭтаФорма|ЭтотОбъект|Элементы\.|ОткрытьФорму|ПолучитьФорму|ПоказатьПредупреждение|ПоказатьВопрос|ОповеститьОбИзменении)\b/, text: 'Формы и интерфейс клиента в песочнице недоступны. Выводите результат через Сообщить().' },
];
function updateHints() {
  const code = view.state.doc.toString();
  const found = RULES.filter((r) => r.re.test(code));
  els.hints.replaceChildren(...found.map((r) => {
    const li = document.createElement('li');
    li.textContent = r.text;
    return li;
  }));
  els.hints.hidden = found.length === 0;
}
updateHints();

// ---------- Движок ----------
let worker = null;
let engineReady = false;
let pending = null; // { id, timer, started }
let runSeq = 0;
let readyText = 'Движок готов';

function setStatus(state, text) {
  els.status.dataset.state = state;
  els.statusText.textContent = text;
  els.run.disabled = state === 'loading' || state === 'running' || state === 'fatal';
}

function startWorker() {
  engineReady = false;
  setStatus('loading', 'Загружаю движок…');
  const started = performance.now();
  worker = new Worker(new URL('./worker.js', document.baseURI), { type: 'module' });
  worker.onmessage = (ev) => {
    const msg = ev.data;
    if (msg.type === 'ready') {
      engineReady = true;
      const sec = ((performance.now() - started) / 1000).toFixed(1);
      readyText = `Движок готов · OneScript ${msg.version} · загрузка ${sec} с`;
      setStatus('ready', readyText);
    } else if (msg.type === 'fatal') {
      setStatus('fatal', 'Движок не загрузился');
      renderOutput([{ kind: 'err', text: 'Не удалось запустить движок: ' + msg.message }]);
    } else if (msg.type === 'result' && pending && msg.id === pending.id) {
      clearTimeout(pending.timer);
      const res = msg.result;
      renderOutput(res.output, res.exitCode);
      const total = Math.round(performance.now() - pending.started);
      els.meta.textContent = res.exitCode === 0
        ? `Выполнено за ${total} мс`
        : `Завершено с ошибкой · ${total} мс`;
      pending = null;
      setStatus('ready', readyText);
    }
  };
  worker.onerror = (e) => {
    setStatus('fatal', 'Движок не загрузился');
    renderOutput([{ kind: 'err', text: 'Ошибка воркера: ' + (e.message || 'неизвестная ошибка') }]);
  };
}

function runCode() {
  if (!engineReady || pending) return;
  const id = ++runSeq;
  const code = view.state.doc.toString();
  saveDraft();
  setStatus('running', 'Выполняю…');
  els.meta.textContent = '';
  showPlaceholder('Выполняется…');
  pending = {
    id,
    started: performance.now(),
    timer: setTimeout(() => {
      // Бесконечный цикл: останавливаем поток целиком и поднимаем движок заново
      worker.terminate();
      pending = null;
      renderOutput([{ kind: 'err', text: `Программа работала дольше ${TIMEOUT_MS / 1000} с и была остановлена. Проверьте условия выхода из циклов и рекурсии.` }], 1);
      els.meta.textContent = 'Остановлено по таймауту';
      startWorker();
    }, TIMEOUT_MS),
  };
  worker.postMessage({ type: 'run', id, code, input: els.input.value });
}
els.run.addEventListener('click', runCode);

// ---------- Вывод ----------
function showPlaceholder(text) {
  const p = document.createElement('p');
  p.className = 'placeholder';
  p.textContent = text;
  els.output.replaceChildren(p);
}

function renderOutput(items, exitCode) {
  els.output.replaceChildren();
  if (!items || items.length === 0) {
    showPlaceholder(exitCode === 0 ? 'Программа выполнена, вывода нет. Используйте Сообщить().' : 'Нет вывода.');
    return;
  }
  for (const it of items) {
    for (const line of String(it.text).split(/\r?\n/)) {
      const div = document.createElement('div');
      div.className = 'line ' + (it.kind === 'err' ? 'line-err' : 'line-out');
      const m = it.kind === 'err' && line.match(/^Строка (\d+):/);
      if (m) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'goto';
        btn.textContent = `Строка ${m[1]}`;
        btn.title = 'Перейти к строке в редакторе';
        btn.addEventListener('click', () => gotoLine(Number(m[1])));
        div.append(btn, document.createTextNode(line.slice(m[0].length)));
      } else {
        div.textContent = line;
      }
      els.output.appendChild(div);
    }
  }
}

function gotoLine(n) {
  const doc = view.state.doc;
  if (n < 1 || n > doc.lines) return;
  const line = doc.line(n);
  view.dispatch({ selection: { anchor: line.from, head: line.to }, scrollIntoView: true });
  view.focus();
}

showPlaceholder('Нажмите «Запустить» или Ctrl+Enter.');
startWorker();
