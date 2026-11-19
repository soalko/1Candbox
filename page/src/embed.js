// Веб-компонент <bsl-sandbox>: запускаемый пример кода 1С на любой странице.
//
//   <script src="https://<сайт 1Candbox>/embed.js" defer></script>
//   <bsl-sandbox>
//   <script type="text/bsl">
//   Сообщить("Привет, мир!");
//   </script>
//   </bsl-sandbox>
//
// Код берётся из <script type="text/bsl"> (в нём можно писать < и & без экранирования),
// иначе из текста элемента, например <pre><code>…</code></pre>: без JavaScript читатель увидит обычный код.
// Ввод для ВвестиСтроку(): <script type="text/bsl-input"> или атрибут input.
// Атрибуты: theme="light|dark" (по умолчанию как у читателя), height="320" (по умолчанию — по содержимому).
//
// Компонент заменяет содержимое на <iframe> с embed.html того же сайта, что и этот скрипт.
// Собирается esbuild в отдельный файл без зависимостей (IIFE), его можно подключать обычным <script>.
import { encodeState } from './share.js';

const script = document.currentScript;
const BASE = new URL('.', script && script.src ? script.src : location.href);

// Убирает общий отступ и пустые строки по краям: код внутри разметки обычно сдвинут
function dedent(text) {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  while (lines.length && !lines[0].trim()) lines.shift();
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop();
  const indents = lines.filter((l) => l.trim()).map((l) => l.match(/^[ \t]*/)[0].length);
  const cut = indents.length ? Math.min(...indents) : 0;
  return lines.map((l) => l.slice(cut)).join('\n') + '\n';
}

const unescapeScript = (s) => s.replace(/<\\\/script/gi, '</script');

class BslSandbox extends HTMLElement {
  connectedCallback() {
    if (this.shadowRoot) return;
    // Скрипт без defer до элемента: дети ещё не разобраны, ждём конца разбора страницы
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', () => this.connectedCallback(), { once: true });
      return;
    }
    this.render();
  }

  readSource() {
    const codeEl = this.querySelector('script[type="text/bsl"]');
    const inputEl = this.querySelector('script[type="text/bsl-input"]');
    let code;
    if (codeEl) {
      code = unescapeScript(codeEl.textContent);
    } else {
      const clone = this.cloneNode(true);
      for (const s of clone.querySelectorAll('script')) s.remove();
      code = clone.textContent;
    }
    const input = inputEl ? unescapeScript(inputEl.textContent) : (this.getAttribute('input') || '');
    return { code: dedent(code), input: input ? dedent(input).replace(/\n$/, '') : '' };
  }

  async render() {
    const { code, input } = this.readSource();
    const theme = this.getAttribute('theme');
    const fixedHeight = parseInt(this.getAttribute('height'), 10);
    const hash = await encodeState({ code, input }, {
      theme: theme === 'light' || theme === 'dark' ? theme : null,
      fit: fixedHeight > 0 ? null : 1,
    });

    const root = this.attachShadow({ mode: 'open' });
    const style = document.createElement('style');
    style.textContent = ':host{display:block;margin:1em 0}iframe{display:block;width:100%;border:0;border-radius:8px;color-scheme:normal}';
    const frame = document.createElement('iframe');
    frame.title = '1Candbox: запускаемый пример кода';
    frame.loading = 'lazy';
    frame.src = new URL('embed.html#' + hash, BASE).href;
    // До первого сообщения от виджета — высота по числу строк
    const lines = Math.min(code.split('\n').length, 22);
    frame.style.height = (fixedHeight > 0 ? fixedHeight : Math.round(48 + lines * 19 + 10)) + 'px';
    root.append(style, frame);

    if (!(fixedHeight > 0)) {
      window.addEventListener('message', (e) => {
        if (e.source !== frame.contentWindow || !e.data || e.data.type !== 'bsl-sandbox:resize') return;
        const h = Number(e.data.height);
        if (h > 0 && h < 5000) frame.style.height = h + 'px';
      });
    }
  }
}

if (!customElements.get('bsl-sandbox')) customElements.define('bsl-sandbox', BslSandbox);
