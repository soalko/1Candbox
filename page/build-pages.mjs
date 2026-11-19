// Статические страницы для поисковиков: страница на каждый пример (examples/<id>.html),
// список примеров, ссылки на них в подвале главной, sitemap.xml и robots.txt.
// Запуск из build.sh после копирования page/public в site/:
//   SITE_URL=https://example.github.io/bsl-sandbox node page/build-pages.mjs site
// SITE_URL нужен для абсолютных адресов (canonical, og:url, sitemap). Без него эти теги пропускаются.
import fs from 'node:fs';
import path from 'node:path';
import { EXAMPLES } from './src/examples.js';

const SITE = path.resolve(process.argv[2] || 'site');
const SITE_URL = (process.env.SITE_URL || '').replace(/\/+$/, '');
const BRAND = '1Candbox';

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
// Видимый текст с местами для переноса в длинных именах 1С (ОбщегоНазначенияКлиентСервер.СообщитьПользователю):
// <wbr> после точки и, только в именах от 18 символов, на стыке слов внутри имени. Короткие имена
// (СтрШаблон, ОписаниеОшибки) переносятся целиком. Только для текста страницы, не для мета-тегов.
const escWrap = (s) => esc(s)
  .replace(/[\p{L}\p{N}_]{18,}/gu, (w) => w.replace(/(\p{Ll})(?=\p{Lu}\p{Ll})/gu, '$1<wbr>'))
  .replace(/\.(?=\p{L})/gu, '.<wbr>');
const abs = (rel) => SITE_URL + '/' + rel;
const jsonLd = (obj) => `<script type="application/ld+json">${JSON.stringify(obj).replace(/</g, '\\u003c')}</script>`;

// Иконки вкладки и закладок (исходник page/public/favicon.svg, растровые копии — scripts/render_images.sh).
// favicon-120.png — для сниппета в выдаче Яндекса, он просит не меньше 120×120.
// prefix — путь до корня сайта: '' в корне, '../' в examples/, '/' на странице 404 (она открывается по любому адресу).
const headIcons = (prefix) => [
  `<link rel="icon" href="${prefix}favicon.ico" sizes="32x32">`,
  `<link rel="icon" href="${prefix}favicon.svg" type="image/svg+xml">`,
  `<link rel="icon" href="${prefix}favicon-120.png" type="image/png" sizes="120x120">`,
  `<link rel="apple-touch-icon" href="${prefix}apple-touch-icon.png">`,
].join('\n');

// Картинка для превью ссылки в мессенджерах и соцсетях. Telegram и VK принимают только абсолютный адрес,
// поэтому без SITE_URL картинки нет и карточка остаётся маленькой (twitter:card summary).
function ogImage() {
  if (!SITE_URL) return '<meta name="twitter:card" content="summary">';
  return [
    `<meta property="og:image" content="${esc(abs('og-image.png'))}">`,
    '<meta property="og:image:type" content="image/png">',
    '<meta property="og:image:width" content="1200">',
    '<meta property="og:image:height" content="630">',
    `<meta property="og:image:alt" content="${BRAND} — онлайн-компилятор 1С: редактор кода и вывод">`,
    '<meta name="twitter:card" content="summary_large_image">',
  ].join('\n');
}

function seoHead(rel, { title, description, type = 'website' }) {
  const tags = [
    `<meta property="og:type" content="${type}">`,
    `<meta property="og:site_name" content="${BRAND}">`,
    `<meta property="og:title" content="${esc(title)}">`,
    `<meta property="og:description" content="${esc(description)}">`,
    '<meta property="og:locale" content="ru_RU">',
    ogImage(),
  ];
  if (SITE_URL) tags.push(`<link rel="canonical" href="${esc(abs(rel))}">`, `<meta property="og:url" content="${esc(abs(rel))}">`);
  return tags.join('\n');
}

// Общая часть оформления страниц примеров: те же цвета и шрифты, что на главной
const STYLE = `
  :root {
    --bg: #f4f5f2; --panel: #ffffff; --ink: #1d2320; --muted: #5f6a64; --line: #d9ddd7;
    --accent: #1f6f5c; --accent-ink: #ffffff;
    --font-ui: "IBM Plex Sans", "Segoe UI", Roboto, Arial, sans-serif;
    --font-code: "JetBrains Mono", "Cascadia Code", Menlo, Consolas, monospace;
    color-scheme: light;
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #141816; --panel: #1b201e; --ink: #e4e8e5; --muted: #98a39d; --line: #2d3531;
      --accent: #4fb89b; --accent-ink: #0d1411; color-scheme: dark;
    }
  }
  :root[data-theme="dark"] {
    --bg: #141816; --panel: #1b201e; --ink: #e4e8e5; --muted: #98a39d; --line: #2d3531;
    --accent: #4fb89b; --accent-ink: #0d1411; color-scheme: dark;
  }
  * { box-sizing: border-box; }
  /* overflow-wrap: запасной перенос, если в слове нет места для <wbr> */
  body { margin: 0; background: var(--bg); color: var(--ink); font: 16px/1.6 var(--font-ui); overflow-wrap: break-word; }
  a { color: var(--accent); text-underline-offset: 2px; }
  .top {
    display: flex; flex-wrap: wrap; align-items: center; gap: 10px 20px; padding: 12px 20px;
    background: var(--panel); border-bottom: 1px solid var(--line);
  }
  .top .brand { font-weight: 600; font-size: 18px; color: var(--ink); text-decoration: none; margin-right: auto; }
  .top nav { display: flex; gap: 16px; align-items: center; font-size: 14.5px; }
  .top nav a { color: var(--ink); }
  /* Переключатель темы, как на главной: два значка, выбранный подсвечен */
  .theme-switch { display: inline-flex; padding: 2px; gap: 2px; border: 1px solid var(--line); border-radius: 7px; background: var(--panel); }
  .theme-switch button {
    border: 0; padding: 4px 6px; border-radius: 5px; background: none; color: var(--muted); cursor: pointer;
    display: inline-flex; align-items: center; justify-content: center;
  }
  .theme-switch button:hover { color: var(--ink); }
  .theme-switch button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  .theme-switch button[aria-pressed="true"] { background: color-mix(in srgb, var(--accent) 16%, transparent); color: var(--ink); }
  .theme-switch svg { width: 16px; height: 16px; display: block; }
  .btn {
    display: inline-block; padding: 7px 16px; border-radius: 6px; background: var(--accent); color: var(--accent-ink) !important;
    font-weight: 600; font-size: 14.5px; text-decoration: none;
  }
  .btn:focus-visible, a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  /* Подвал прижат к низу и на коротких страницах */
  body { min-height: 100vh; display: flex; flex-direction: column; }
  main { flex: 1; width: 100%; max-width: 860px; margin: 0 auto; padding: 22px 20px 32px; }
  .crumbs { font-size: 14px; color: var(--muted); }
  .crumbs a { color: var(--muted); }
  h1 { font-size: clamp(24px, 4vw, 32px); line-height: 1.2; margin: 8px 0 12px; letter-spacing: -0.01em; text-wrap: balance; }
  h2 { font-size: 20px; margin: 32px 0 10px; }
  .lead { font-size: 17px; margin: 0 0 18px; max-width: 70ch; }
  .hint { color: var(--muted); font-size: 14.5px; max-width: 70ch; }
  /* Код до загрузки компонента и без JavaScript */
  bsl-sandbox { display: block; margin: 1em 0; }
  bsl-sandbox pre {
    margin: 0; padding: 12px 14px; overflow: auto; background: var(--panel); border: 1px solid var(--line);
    border-radius: 8px; font: 13.5px/1.45 var(--font-code); tab-size: 4;
  }
  .cards { list-style: none; padding: 0; margin: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 250px), 1fr)); gap: 12px; }
  .cards li { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 12px 14px; min-width: 0; }
  .cards a { font-weight: 600; }
  .cards p { margin: 4px 0 0; font-size: 14px; color: var(--muted); }
`;

const THEME_SCRIPT = `<script>
  try { const t = localStorage.getItem('bsl-sandbox-theme'); if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t; } catch {}
</script>`;

// Переключатель темы в шапке. Выбор сохраняется под тем же ключом, что на главной; виджеты <bsl-sandbox>
// на странице (iframe того же сайта) получают событие storage и перекрашиваются сами.
const THEME_SWITCH = `<div class="theme-switch" role="group" aria-label="Тема оформления">
    <button type="button" data-theme-choice="light" aria-pressed="false" aria-label="Светлая тема" title="Светлая тема"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41"/></svg></button>
    <button type="button" data-theme-choice="dark" aria-pressed="false" aria-label="Тёмная тема" title="Тёмная тема"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg></button>
  </div>`;

const THEME_SWITCH_SCRIPT = `<script>
(function () {
  var KEY = 'bsl-sandbox-theme', root = document.documentElement;
  var btns = document.querySelectorAll('[data-theme-choice]');
  var mq = window.matchMedia ? matchMedia('(prefers-color-scheme: dark)') : null;
  function current() { var t = root.dataset.theme; return t === 'light' || t === 'dark' ? t : (mq && mq.matches ? 'dark' : 'light'); }
  function sync() { btns.forEach(function (b) { b.setAttribute('aria-pressed', String(b.dataset.themeChoice === current())); }); }
  btns.forEach(function (b) {
    b.addEventListener('click', function () {
      root.dataset.theme = b.dataset.themeChoice;
      try { localStorage.setItem(KEY, b.dataset.themeChoice); } catch (e) {}
      sync();
    });
  });
  if (mq) mq.addEventListener('change', sync);
  sync();
})();
</script>`;

const FONTS = `<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600&family=JetBrains+Mono:wght@400;600&display=swap" rel="stylesheet">`;

// ---------- Общий подвал всех страниц ----------
// Один источник для главной (index.html), списка и страниц примеров и «Лицензий»:
// метки <!--SITE_FOOTER--> и /*SITE_FOOTER_CSS*/ в шаблонах заменяются здесь.
const FOOTER_CSS = `
  /* Подвал на всю ширину, как шапка: сверху примеры, ниже описание движка и правовая строка */
  .site-footer {
    border-top: 1px solid var(--line); background: var(--panel);
    padding: 12px 20px 14px; color: var(--muted); font-size: 12.5px; line-height: 1.5;
    display: flex; flex-direction: column; gap: 8px;
  }
  .site-footer p { margin: 0; }
  .site-footer a { color: inherit; text-underline-offset: 2px; }
  .site-footer a:hover { color: var(--ink); }
  .examples-nav { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px 10px; }
  .examples-nav > a { font-weight: 600; color: var(--ink); text-decoration: none; margin-right: 2px; }
  .examples-nav > a:hover { text-decoration: underline; }
  .ex-links { display: contents; }
  /* Примеры — компактные «чипы»: кликабельная область больше, строка читается легче, чем текст через точки */
  .ex-links a {
    font-size: 12.5px; color: var(--ink); text-decoration: none; white-space: nowrap;
    padding: 2px 9px; border: 1px solid var(--line); border-radius: 999px; background: var(--bg);
  }
  .ex-links a:hover { border-color: var(--accent); color: var(--accent); }
  .ex-links a[aria-current="page"] { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 14%, transparent); font-weight: 600; }
  .site-footer a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  .footer-meta { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 4px 24px; padding-top: 8px; border-top: 1px dashed var(--line); }
  .footer-meta p:first-child { max-width: 100ch; }
  .footer-meta p:last-child { white-space: nowrap; }
  @media (max-width: 820px) { .footer-meta p:last-child { white-space: normal; } }
`;

const exampleHref = (ex) => `${ex.id}.html`;

// prefix — путь до корня сайта ('' для страниц в корне, '../' для examples/); currentId — текущий пример.
// data-ex: на главной клик загружает пример прямо в редактор; href — для поисковиков и новой вкладки.
function siteFooter(prefix, currentId = null) {
  const links = EXAMPLES.map((e) =>
    `<a href="${prefix}examples/${exampleHref(e)}" data-ex="${esc(e.id)}"${e.id === currentId ? ' aria-current="page"' : ''}>${esc(e.title)}</a>`).join('');
  return `<footer class="site-footer">
  <nav class="examples-nav" aria-label="Примеры кода"><a href="${prefix}examples/">Примеры кода на 1С</a><span class="ex-links">${links}</span></nav>
  <div class="footer-meta">
    <p>Код выполняет OneScript — открытая реализация языка 1С (MPL-2.0). Работает алгоритмическая часть: строки, числа, даты, Массив, Структура, Соответствие, ТаблицаЗначений, исключения, а также запросы к таблицам значений и временным таблицам. Справочников, документов и форм нет: для них нужна платформа «1С:Предприятие» с информационной базой.</p>
    <p>Сайт не связан с фирмой «1С»; «1С» — её товарный знак. <a href="${prefix}licenses.html">Лицензии и исходный код</a></p>
  </div>
</footer>`;
}

// rel — адрес страницы от корня сайта; prefix — путь до корня для иконок.
// seo: false — служебная страница (404): noindex, без Open Graph и canonical.
function page({ rel, prefix, title, description, type, head = '', body, seo = true }) {
  return `<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)}</title>
<meta name="description" content="${esc(description)}">
${seo ? seoHead(rel, { title, description, type }) : '<meta name="robots" content="noindex">'}
${headIcons(prefix)}
${THEME_SCRIPT}
${FONTS}
<style>${STYLE}${FOOTER_CSS}</style>
${head}
</head>
<body>
${body}
${THEME_SWITCH_SCRIPT}
</body>
</html>
`;
}

// ---------- Страница примера ----------
function examplePage(ex) {
  const rel = `examples/${exampleHref(ex)}`;
  const title = `${ex.seoTitle} — запустить онлайн | ${BRAND}`;
  const inputBlock = ex.input
    ? `\n<script type="text/bsl-input">\n${ex.input.replace(/<\/script/gi, '<\\/script')}\n</script>`
    : '';
  const ld = jsonLd({
    '@context': 'https://schema.org',
    '@type': 'SoftwareSourceCode',
    name: ex.seoTitle,
    description: ex.description,
    programmingLanguage: 'Встроенный язык 1С:Предприятие (BSL)',
    codeSampleType: 'full (compile ready) solution',
    inLanguage: 'ru',
    isAccessibleForFree: true,
    ...(SITE_URL ? { url: abs(rel) } : {}),
  });
  return page({
    rel, prefix: '../', title, description: ex.description, type: 'article', head: ld,
    body: `<header class="top">
  <a class="brand" href="../">${BRAND}</a>
  <nav>${THEME_SWITCH}</nav>
</header>
<main>
  <nav class="crumbs" aria-label="Навигация"><a href="../">${BRAND}</a> / <a href="./">Примеры кода</a></nav>
  <h1>${escWrap(ex.seoTitle)}</h1>
  <p class="lead">${escWrap(ex.intro)}</p>
  <bsl-sandbox>
<pre><code>${esc(ex.code)}</code></pre>${inputBlock}
  </bsl-sandbox>
  <p class="hint">Нажмите «Запустить» или F5: код выполнится прямо в браузере, без установки платформы 1С. Код можно поменять прямо здесь. Чтобы проверить его на ошибки до запуска и передать данные в ВвестиСтроку(), нажмите «Открыть в 1Candbox ↗» над редактором: пример откроется в полной версии с этим кодом.</p>
</main>
${siteFooter('../', ex.id)}
<script src="../embed.js" defer></script>`,
  });
}

// ---------- Список примеров ----------
function indexPage() {
  const title = `Примеры кода на 1С с запуском онлайн | ${BRAND}`;
  const description = 'Примеры кода на встроенном языке 1С, которые можно запустить и изменить в браузере: запросы к таблицам значений, строки, даты, ТаблицаЗначений, Структура, Соответствие, JSON, рекурсия, обработка ошибок.';
  const cards = EXAMPLES.map((e) => `    <li><a href="${exampleHref(e)}">${escWrap(e.seoTitle)}</a><p>${escWrap(e.description)}</p></li>`).join('\n');
  return page({
    rel: 'examples/', prefix: '../', title, description,
    body: `<header class="top">
  <a class="brand" href="../">${BRAND}</a>
  <nav>${THEME_SWITCH}</nav>
</header>
<main>
  <nav class="crumbs" aria-label="Навигация"><a href="../">${BRAND}</a> / Примеры кода</nav>
  <h1>Примеры кода на 1С с запуском онлайн</h1>
  <p class="lead">Каждый пример можно запустить прямо на странице и поменять под свою задачу. Код выполняет OneScript — открытая реализация встроенного языка 1С, поэтому работает алгоритмическая часть языка без информационной базы. Язык запросов тоже есть: СГРУППИРОВАТЬ ПО, соединения, ОБЪЕДИНИТЬ и пакеты запросов выполняются над таблицами значений и временными таблицами.</p>
  <ul class="cards">
${cards}
  </ul>
</main>
${siteFooter('../')}`,
  });
}

// ---------- Страница 404 ----------
// Сервер отдаёт её с кодом 404 по любому неизвестному адресу (deploy/Caddyfile), в том числе
// /examples/старое-имя.html, поэтому все ссылки от корня сайта ('/'), а не относительные.
// Код из #c=… сюда не теряется: после # адрес на сервер не уходит, ссылка «Поделиться» ведёт на главную.
function notFoundPage() {
  return page({
    rel: '404.html', prefix: '/', seo: false,
    title: `Страница не найдена | ${BRAND}`,
    description: 'Такой страницы на сайте нет.',
    body: `<header class="top">
  <a class="brand" href="/">${BRAND}</a>
  <nav>${THEME_SWITCH}</nav>
</header>
<main>
  <h1>Страница не найдена</h1>
  <p class="lead">Такой страницы нет: возможно, ссылка устарела или в адресе опечатка.</p>
  <p><a class="btn" href="/">Открыть онлайн-компилятор</a></p>
  <p class="hint">Или выберите <a href="/examples/">пример кода на 1С</a>: каждый можно запустить прямо на странице.</p>
</main>
${siteFooter('/')}`,
  });
}

fs.writeFileSync(path.join(SITE, '404.html'), notFoundPage());

fs.mkdirSync(path.join(SITE, 'examples'), { recursive: true });
for (const ex of EXAMPLES) {
  if (!ex.seoTitle || !ex.description || !ex.intro) throw new Error(`У примера ${ex.id} нет seoTitle, description или intro`);
  fs.writeFileSync(path.join(SITE, 'examples', exampleHref(ex)), examplePage(ex));
}
fs.writeFileSync(path.join(SITE, 'examples', 'index.html'), indexPage());

// ---------- Главная и «Лицензии»: общий подвал и абсолютные адреса ----------
const indexPath = path.join(SITE, 'index.html');
let index = fs.readFileSync(indexPath, 'utf8');
index = index.replace('<!--SITE_FOOTER-->', siteFooter(''))
  .replace('/*SITE_FOOTER_CSS*/', FOOTER_CSS)
  .replace('<!--HEAD_ICONS-->', headIcons(''))
  .replace('<!--SEO_HEAD-->', [ogImage(), ...(SITE_URL ? [`<link rel="canonical" href="${esc(abs(''))}">`, `<meta property="og:url" content="${esc(abs(''))}">`] : [])].join('\n'));
fs.writeFileSync(indexPath, index);

// «Лицензии» (site/licenses.html создаёт scripts/collect_licenses.py, он запускается раньше)
const licPath = path.join(SITE, 'licenses.html');
if (fs.existsSync(licPath)) {
  const lic = fs.readFileSync(licPath, 'utf8')
    .replace('<!--SITE_FOOTER-->', siteFooter(''))
    .replace('/*SITE_FOOTER_CSS*/', FOOTER_CSS)
    .replace('<!--HEAD_ICONS-->', headIcons(''));
  fs.writeFileSync(licPath, lic);
}
// Метки, которые остались незаменёнными, — ошибка в шаблоне: лучше остановить сборку, чем выложить без иконок
for (const f of [indexPath, licPath]) {
  if (fs.existsSync(f) && /<!--(SITE_FOOTER|HEAD_ICONS|SEO_HEAD)-->/.test(fs.readFileSync(f, 'utf8'))) {
    throw new Error(`В ${path.basename(f)} осталась незаменённая метка шаблона`);
  }
}

// ---------- sitemap.xml и robots.txt ----------
let robots = 'User-agent: *\nAllow: /\n';
if (SITE_URL) {
  const today = new Date().toISOString().slice(0, 10);
  const urls = ['', 'examples/', ...EXAMPLES.map((e) => `examples/${exampleHref(e)}`)];
  const xml = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls.map((u) => `  <url><loc>${esc(abs(u))}</loc><lastmod>${today}</lastmod></url>`).join('\n')}
</urlset>
`;
  fs.writeFileSync(path.join(SITE, 'sitemap.xml'), xml);
  robots += `Sitemap: ${abs('sitemap.xml')}\n`;
} else {
  console.warn('SITE_URL не задан: canonical, og:url и sitemap.xml не созданы. Перед публикацией: SITE_URL=https://… ./build.sh');
}
fs.writeFileSync(path.join(SITE, 'robots.txt'), robots);
console.log(`Страницы примеров: ${EXAMPLES.length} → site/examples/`);
