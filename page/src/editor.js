// Редактор CodeMirror для языка 1С: подсветка, проверка компилятором без запуска,
// предупреждения о совместимости, подсветка строки с ошибкой выполнения.
import { EditorView, basicSetup } from 'codemirror';
import { keymap, Decoration, WidgetType } from '@codemirror/view';
import { EditorState, Compartment, StateField, StateEffect } from '@codemirror/state';
import { linter, lintGutter, forceLinting } from '@codemirror/lint';
import { indentUnit, HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags as t } from '@lezer/highlight';
import { indentWithTab } from '@codemirror/commands';
import { bsl } from '@1c-syntax/codemirror-lang-bsl';
import { findRuleMatches } from './rules.js';

export const isDark = () => {
  const theme = document.documentElement.getAttribute('data-theme');
  if (theme) return theme === 'dark';
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
};

// ---------- Подсветка синтаксиса ----------
// Стандартная схема CodeMirror рассчитана на светлый фон: на тёмном директивы (#404740)
// и ключевые слова (#708) почти не видны. Цвета берём из CSS-переменных --syn-*,
// поэтому схема сама переключается вместе с темой страницы.
const bslHighlight = HighlightStyle.define([
  { tag: [t.definitionKeyword, t.controlKeyword, t.keyword, t.modifier, t.operatorKeyword, t.logicOperator],
    color: 'var(--syn-keyword)' },
  { tag: [t.string, t.literal], color: 'var(--syn-string)' },
  { tag: [t.number, t.bool, t.null], color: 'var(--syn-number)' },
  { tag: [t.comment, t.lineComment, t.docComment], color: 'var(--syn-comment)', fontStyle: 'italic' },
  // Директивы компиляции (&НаСервере, &НаКлиенте) — отдельный цвет и полужирный: от них зависит, где выполняется код
  { tag: t.annotation, color: 'var(--syn-annotation)', fontWeight: '600' },
  // Препроцессор (#Область, #Если) и метки
  { tag: [t.macroName, t.namespace, t.processingInstruction, t.labelName], color: 'var(--syn-macro)' },
  { tag: t.function(t.definition(t.variableName)), color: 'var(--syn-definition)', fontWeight: '600' },
  { tag: [t.typeName, t.className], color: 'var(--syn-type)' },
  { tag: [t.arithmeticOperator, t.compareOperator], color: 'var(--syn-operator)' },
  { tag: t.special(t.variableName), color: 'var(--syn-number)' },
  { tag: t.invalid, color: 'var(--err)' },
]);

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

function makeLintSource(engine) {
  return async (v) => {
    const doc = v.state.doc;
    const code = doc.toString();
    const result = [];
    const compiled = engine ? await engine.check(code) : null;
    // Пока проверяли, текст мог измениться: тогда результат устарел, линтер запустится снова сам
    if (v.state.doc.toString() !== code) return [];
    const seenPos = new Set();
    for (const d of compiled || []) {
      if (d.line < 1 || d.line > doc.lines) continue;
      // Несколько ошибок в одной точке — обычно следствие первой, показываем первую
      const key = `${d.line}:${d.column}`;
      if (seenPos.has(key)) continue;
      seenPos.add(key);
      const severity = d.severity === 'warning' ? 'warning' : 'error';
      const line = doc.line(d.line);
      if (d.length > 0 && d.column >= 1) {
        // Точный диапазон (например, из проверки текста запроса): от столбца на length символов,
        // может уходить на следующие строки
        const from = line.from + Math.min(d.column - 1, line.length);
        const to = Math.min(from + d.length, doc.length);
        result.push({ from, to: Math.max(to, from), severity, source: d.source || 'Компилятор', message: d.message });
        continue;
      }
      let { from, to } = rangeAt(doc, d.line, d.column);
      if (!d.source) {
        // Компилятор замечает ошибку на следующей лексеме. Если перед ней в строке ничего нет
        // (столбец 0 или «Ожидается ;» в начале оператора), ошибка относится к концу предыдущей строки.
        const atLineStart = !line.text.slice(0, Math.max(0, from - line.from)).trim();
        if (d.column < 1 || (atLineStart && d.message.includes(';'))) {
          const prev = prevCodeEnd(doc, d.line);
          if (prev) ({ from, to } = prev);
        }
        // Незакрытая кавычка: подчёркиваем от кавычки до конца строки — туда ушёл «литерал»
        if (d.message.startsWith('Незакрытая кавычка') && d.column >= 1) to = Math.max(to, line.to);
      }
      result.push({ from, to, severity, source: d.source || 'Компилятор', message: d.message });
    }
    // «Ожидается ;» на строке, где уже есть другая ошибка, — её следствие
    // (например, «;» попала внутрь незакрытой строки): не показываем
    const isSemicolon = (r) => r.source === 'Компилятор' && r.message.startsWith('Ожидается символ ;');
    const linesWithOther = new Set(result.filter((r) => r.severity === 'error' && !isSemicolon(r)).map((r) => doc.lineAt(r.from).number));
    for (let i = result.length - 1; i >= 0; i--) {
      if (isSemicolon(result[i]) && linesWithOther.has(doc.lineAt(result[i].from).number)) result.splice(i, 1);
    }
    // Подсказки о совместимости — предупреждения на месте
    for (const m of findRuleMatches(code)) {
      result.push({ from: m.from, to: m.to, severity: 'warning', source: 'Совместимость', message: m.text });
    }
    return result;
  };
}

// ---------- Ошибка выполнения: подсветка строки до первого изменения этой строки ----------
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

/**
 * Создаёт редактор.
 * @param {object} opts
 * @param {HTMLElement} opts.parent
 * @param {string} opts.doc
 * @param {import('./engine.js').Engine} [opts.engine]  для проверки кода компилятором
 * @param {() => void} [opts.onChange]  текст изменился (с задержкой 400 мс)
 */
export function createEditor({ parent, doc, engine, onChange }) {
  const themeCompartment = new Compartment();
  const editorTheme = () => EditorView.theme({}, { dark: isDark() });
  let changeTimer = null;

  const view = new EditorView({
    parent,
    state: EditorState.create({
      doc,
      extensions: [
        basicSetup,
        bsl(),
        syntaxHighlighting(bslHighlight),
        indentUnit.of('    '),
        keymap.of([indentWithTab]),
        themeCompartment.of(editorTheme()),
        lintGutter(),
        linter(makeLintSource(engine), { delay: 500 }),
        runtimeErrorField,
        EditorView.updateListener.of((u) => {
          if (u.docChanged && onChange) {
            clearTimeout(changeTimer);
            changeTimer = setTimeout(onChange, 400);
          }
        }),
      ],
    }),
  });
  new MutationObserver(() => view.dispatch({ effects: themeCompartment.reconfigure(editorTheme()) }))
    .observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

  return {
    view,
    getCode: () => view.state.doc.toString(),
    setCode(code) {
      view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: code }, effects: setRuntimeError.of(null) });
    },
    relint: () => forceLinting(view),
    clearRuntimeError: () => view.dispatch({ effects: setRuntimeError.of(null) }),
    /** Подсвечивает строку первой ошибки «Строка N: текст» из вывода программы */
    showRuntimeError(items) {
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
    },
    gotoLine(n) {
      const d = view.state.doc;
      if (n < 1 || n > d.lines) return;
      const line = d.line(n);
      view.dispatch({ selection: { anchor: line.from, head: line.to }, scrollIntoView: true });
      view.focus();
    },
  };
}
