// Виджет для встраивания (embed.html в <iframe>): редактор, кнопка «Запустить» и вывод.
// Параметры в адресе после «#»: c — код, i — ввод (см. share.js), theme — light|dark, fit=1 — высота
// по содержимому (её подстраивает <bsl-sandbox> по сообщениям bsl-sandbox:resize).
// Движок (десятки мегабайт) загружается не сразу, а при первом наведении, фокусе или запуске:
// на странице статьи может быть несколько виджетов, и читатель не обязательно их запустит.
import { Engine } from './engine.js';
import { createEditor } from './editor.js';
import { renderOutput, showPlaceholder, resultMeta } from './output.js';
import { encodeState, decodeState } from './share.js';
import { EXAMPLES } from './examples.js';

const $ = (id) => document.getElementById(id);
const els = {
  editor: $('editor'),
  run: $('run-btn'),
  status: $('engine-status'),
  open: $('open-link'),
  inputWrap: $('input-wrap'),
  input: $('stdin'),
  outputWrap: $('output-wrap'),
  output: $('output'),
  meta: $('run-meta'),
};

let state;
try {
  state = await decodeState(location.hash);
} catch (e) {
  state = { code: `// Не удалось открыть код из ссылки: ${e.message}\n`, input: '' };
}
const fromExample = state.example && EXAMPLES.find((e) => e.id === state.example);
const code = state.code ?? (fromExample ? fromExample.code : 'Сообщить("Привет, мир!");\n');
const input = state.code != null ? state.input : (fromExample ? fromExample.input : '');
const fit = state.params && state.params.get('fit') === '1';
if (fit) document.documentElement.classList.add('fit');

// Поле «Ввод» нужно, только если программа что-то читает
els.input.value = input || '';
els.inputWrap.hidden = !(input || /ВвестиСтроку/i.test(code));

function setStatus(st, text) {
  els.status.dataset.state = st;
  els.status.textContent = st === 'ready' ? 'Готово' : text;
  els.status.title = text;
  els.run.disabled = st === 'running';
}

const engine = new Engine({
  workerUrl: new URL('./worker.js', document.baseURI),
  onStatus: setStatus,
  onReady: () => editor.relint(),
});

const editor = createEditor({
  parent: els.editor,
  doc: code,
  engine,
  onChange: () => { updateOpenLink(); els.inputWrap.hidden = !(els.input.value || /ВвестиСтроку/i.test(editor.getCode())); },
});

// «Открыть в 1Candbox»: полная версия с текущим кодом
async function updateOpenLink() {
  const hash = await encodeState({ code: editor.getCode(), input: els.input.value });
  els.open.href = new URL('index.html#' + hash, document.baseURI).href;
}
els.input.addEventListener('input', updateOpenLink);
updateOpenLink();

// Ленивый старт движка
const warmUp = () => { if (!engine.started) engine.start(); };
document.addEventListener('pointerenter', warmUp, { once: true, capture: true });
document.addEventListener('pointerdown', warmUp, { once: true, capture: true });
document.addEventListener('focusin', warmUp, { once: true });

async function runCode() {
  if (engine.busy) return;
  els.outputWrap.hidden = false;
  editor.clearRuntimeError();
  els.meta.textContent = '';
  showPlaceholder(els.output, engine.ready ? 'Выполняется…' : 'Загружаю движок, это несколько секунд…');
  const res = await engine.run(editor.getCode(), els.input.value);
  if (!res) return;
  renderOutput(els.output, res.output, res.exitCode, (n) => editor.gotoLine(n));
  editor.showRuntimeError(res.exitCode === 0 ? [] : res.output);
  els.meta.textContent = resultMeta(res);
  editor.relint();
}
els.run.addEventListener('click', runCode);
window.addEventListener('keydown', (e) => {
  if (e.key !== 'F5' || e.ctrlKey || e.metaKey || e.altKey || e.shiftKey) return;
  e.preventDefault();
  runCode();
}, true);

// Высота содержимого — странице со встроенным виджетом (<bsl-sandbox> подстраивает iframe)
if (window.parent !== window) {
  let last = 0;
  const report = () => {
    const height = Math.ceil(document.querySelector('.widget').getBoundingClientRect().height);
    if (height === last) return;
    last = height;
    window.parent.postMessage({ type: 'bsl-sandbox:resize', height }, '*');
  };
  new ResizeObserver(report).observe(document.querySelector('.widget'));
  report();
}
