// Песочница 1С: редактор CodeMirror + движок OneScript (.NET WebAssembly) в Web Worker.
import { EditorView, basicSetup } from 'codemirror';
import { keymap, Decoration, WidgetType } from '@codemirror/view';
import { EditorState, Compartment, StateField, StateEffect } from '@codemirror/state';
import { linter, lintGutter, forceLinting } from '@codemirror/lint';
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

// ---------- Диагностика ----------
// Слово (идентификатор 1С) под позицией ошибки: его и подчёркиваем.
const WORD_RE = /[\p{L}\p{N}_]+/uy;

function rangeAt(doc, lineNo, column) {
  const line = doc.line(Math.min(Math.max(lineNo, 1), doc.lines));
  if (!column || column < 1) {
    // Столбец неизвестен — подчёркиваем строку без отступа
    const indent = line.text.length - line.text.trimStart().length;
    return { from: line.from + indent, to: Math.max(line.from + indent, line.to) };
  }
  const offset = Math.min(column - 1, line.length);
  WORD_RE.lastIndex = offset;
  const m = WORD_RE.exec(line.text);
  if (m) return { from: line.from + offset, to: line.from + offset + m[0].length };
  // Не слово (скобка, точка с запятой, конец строки) — один символ, а в конце строки — последний символ
  if (offset < line.length) return { from: line.from + offset, to: line.from + offset + 1 };
  return { from: Math.max(line.from, line.to - 1), to: line.to };
}

// Последний символ кода (без комментария) в строках выше lineNo
function prevCodeEnd(doc, lineNo) {
  for (let n = lineNo - 1; n >= 1; n--) {
    const prev = doc.line(n);
    const text = prev.text.replace(/\/\/.*$/, '').trimEnd();
    if (text) return { from: prev.from + text.length - 1, to: prev.from + text.length };
  }
  return null;
}

// Ошибки компиляции: проверка в воркере без запуска кода (Runner.Check)
let checkSeq = 0;
const checkWaiters = new Map(); // id → resolve

const CHECK_TIMEOUT_MS = 3000;

function checkInWorker(code) {
  // Во время запуска воркер занят; после запуска проверка повторится (forceLinting)
  if (!worker || !engineReady || pending) return Promise.resolve(null);
  const id = ++checkSeq;
  return new Promise((resolve) => {
    // Защита от зависания компилятора: перезапускаем воркер, как при таймауте программы
    const timer = setTimeout(() => {
      if (!checkWaiters.has(id) || pending) return;
      worker.terminate();
      dropCheckWaiters();
      startWorker();
    }, CHECK_TIMEOUT_MS);
    checkWaiters.set(id, (diagnostics) => { clearTimeout(timer); resolve(diagnostics); });
    worker.postMessage({ type: 'check', id, code });
  });
}
function dropCheckWaiters() {
  for (const resolve of checkWaiters.values()) resolve(null);
  checkWaiters.clear();
}

async function bslLintSource(v) {
  const doc = v.state.doc;
  const code = doc.toString();
  const result = [];
  const compiled = await checkInWorker(code);
  // Пока проверяли, текст мог измениться: тогда результат устарел, линтер запустится снова сам
  if (v.state.doc.toString() !== code) return [];
  const seenPos = new Set();
  for (const d of compiled || []) {
    if (d.line < 1 || d.line > doc.lines) continue;
    // Несколько ошибок в одной точке — обычно следствие первой, показываем первую
    const key = `${d.line}:${d.column}`;
    if (seenPos.has(key)) continue;
    seenPos.add(key);
    let { from, to } = rangeAt(doc, d.line, d.column);
    // Компилятор замечает ошибку на следующей лексеме. Если перед ней в строке ничего нет
    // (столбец 0 или «Ожидается ;» в начале оператора), ошибка относится к концу предыдущей строки.
    const line = doc.line(d.line);
    const atLineStart = !line.text.slice(0, Math.max(0, from - line.from)).trim();
    if (d.column < 1 || (atLineStart && d.message.includes(';'))) {
      const prev = prevCodeEnd(doc, d.line);
      if (prev) ({ from, to } = prev);
    }
    // Незакрытая кавычка: подчёркиваем от кавычки до конца строки — туда ушёл «литерал»
    if (d.message.startsWith('Незакрытая кавычка') && d.column >= 1) to = Math.max(to, line.to);
    result.push({ from, to, severity: 'error', source: 'Компилятор', message: d.message });
  }
  // «Ожидается ;» на строке, где уже есть другая ошибка, — её следствие
  // (например, «;» попала внутрь незакрытой строки): не показываем
  const isSemicolon = (r) => r.message.startsWith('Ожидается символ ;');
  const linesWithOther = new Set(result.filter((r) => !isSemicolon(r)).map((r) => doc.lineAt(r.from).number));
  for (let i = result.length - 1; i >= 0; i--) {
    if (isSemicolon(result[i]) && linesWithOther.has(doc.lineAt(result[i].from).number)) result.splice(i, 1);
  }
  // Подсказки о совместимости — предупреждения на месте
  for (const rule of RULES) {
    const re = new RegExp(rule.re.source, rule.re.flags.includes('g') ? rule.re.flags : rule.re.flags + 'g');
    for (const m of code.matchAll(re)) {
      result.push({ from: m.index, to: m.index + m[0].trimEnd().length, severity: 'warning', source: 'Совместимость', message: rule.text });
    }
  }
  return result;
}

// Ошибка выполнения: подсветка строки до первого изменения этой строки
const setRuntimeError = StateEffect.define();

class RuntimeErrorWidget extends WidgetType {
  constructor(message) { super(); this.message = message; }
  eq(other) { return other.message === this.message; }
  toDOM() {
    const span = document.createElement('span');
    span.className = 'cm-runtime-error-msg';
    span.textContent = this.message;
    span.title = this.message;
    return span;
  }
  ignoreEvent() { return true; }
}

const runtimeErrorField = StateField.define({
  create: () => null, // { from, to, message } — границы строки с ошибкой
  update(val, tr) {
    for (const e of tr.effects) if (e.is(setRuntimeError)) val = e.value;
    if (val && tr.docChanged) {
      let touched = false;
      tr.changes.iterChangedRanges((fromA, toA) => {
        if (fromA <= val.to && toA >= val.from) touched = true;
      });
      if (touched) return null;
      val = { ...val, from: tr.changes.mapPos(val.from, -1), to: tr.changes.mapPos(val.to, 1) };
    }
    return val;
  },
  provide: (f) => EditorView.decorations.from(f, (val) => {
    if (!val) return Decoration.none;
    return Decoration.set([
      Decoration.line({ class: 'cm-runtime-error-line' }).range(val.from),
      Decoration.widget({ widget: new RuntimeErrorWidget(val.message), side: 1 }).range(val.to),
    ]);
  }),
});

function showRuntimeError(items) {
  let effect = null;
  for (const it of items || []) {
    if (it.kind !== 'err') continue;
    const m = String(it.text).match(/^Строка (\d+):\s*(.*)/s);
    if (!m) continue;
    const n = Number(m[1]);
    if (n < 1 || n > view.state.doc.lines) break;
    const line = view.state.doc.line(n);
    effect = setRuntimeError.of({ from: line.from, to: line.to, message: m[2].split('\n')[0] });
    break;
  }
  view.dispatch({ effects: effect || setRuntimeError.of(null) });
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
      lintGutter(),
      linter(bslLintSource, { delay: 500 }),
      runtimeErrorField,
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
  { re: /(?<![\p{L}\p{N}_])(Справочники|Документы|РегистрыСведений|РегистрыНакопления|РегистрыБухгалтерии|Перечисления|Константы|ПланыСчетов|ПланыВидовХарактеристик|БизнесПроцессы|ЖурналыДокументов|Обработки|Отчеты)\./u, text: 'Объекты конфигурации (справочники, документы, регистры) существуют только внутри конкретной базы 1С.' },
  { re: /&(НаСервере|НаКлиенте|НаСервереБезКонтекста|НаКлиентеНаСервере)/, text: 'Директивы &НаКлиенте/&НаСервере здесь не разделяют код: всё выполняется в одном месте.' },
  { re: /(?<![\p{L}\p{N}_])(ОбщегоНазначения|ОбщегоНазначенияКлиент|ОбщегоНазначенияКлиентСервер|СтроковыеФункцииКлиентСервер|СтроковыеФункции|ОбщегоНазначенияСервер)\./u, text: 'Это модули БСП (Библиотеки стандартных подсистем). В песочнице их нет — используйте функции платформы: СтрРазделить, СтрСоединить, СтрШаблон.' },
  { re: /(?<![\p{L}\p{N}_])(?:(?:ЭтаФорма|ЭтотОбъект|ОткрытьФорму|ПолучитьФорму|ПоказатьПредупреждение|ПоказатьВопрос|ОповеститьОбИзменении)(?![\p{L}\p{N}_])|Элементы\.)/u, text: 'Формы и интерфейс клиента в песочнице недоступны. Выводите результат через Сообщить().' },
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
      forceLinting(view);
    } else if (msg.type === 'check') {
      const resolve = checkWaiters.get(msg.id);
      checkWaiters.delete(msg.id);
      if (resolve) resolve(msg.diagnostics);
    } else if (msg.type === 'fatal') {
      setStatus('fatal', 'Движок не загрузился');
      renderOutput([{ kind: 'err', text: 'Не удалось запустить движок: ' + msg.message }]);
    } else if (msg.type === 'result' && pending && msg.id === pending.id) {
      clearTimeout(pending.timer);
      const res = msg.result;
      renderOutput(res.output, res.exitCode);
      showRuntimeError(res.exitCode === 0 ? [] : res.output);
      const total = Math.round(performance.now() - pending.started);
      els.meta.textContent = res.exitCode === 0
        ? `Выполнено за ${total} мс`
        : `Завершено с ошибкой · ${total} мс`;
      pending = null;
      setStatus('ready', readyText);
      forceLinting(view);
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
  view.dispatch({ effects: setRuntimeError.of(null) });
  setStatus('running', 'Выполняю…');
  els.meta.textContent = '';
  showPlaceholder('Выполняется…');
  pending = {
    id,
    started: performance.now(),
    timer: setTimeout(() => {
      // Бесконечный цикл: останавливаем поток целиком и поднимаем движок заново
      worker.terminate();
      dropCheckWaiters();
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
