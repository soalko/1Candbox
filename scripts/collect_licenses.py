# Собирает уведомления о лицензиях всего, что сайт отдаёт браузеру, и страницу «Лицензии».
# Запускается из build.sh после сборки: python3 scripts/collect_licenses.py
#
# Источники:
#   - OneScript: onescript/LICENSE (MPL-2.0), адрес репозитория и коммит клона, наш патч-скрипт;
#   - .NET runtime для WebAssembly: LICENSE.TXT и THIRD-PARTY-NOTICES.TXT из NuGet-кэша (в т.ч. ICU);
#   - NuGet-библиотеки, чьи сборки попали в site/_framework (Newtonsoft.Json, ProDotNetZip…);
#   - npm-пакеты, вошедшие в бандл app.js (по метафайлу esbuild page/meta.json).
# Результат: site/licenses/*.txt и site/licenses.html (из шаблона page/public/licenses.html).
import html, json, pathlib, re, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SITE = ROOT / 'site'
OUT = SITE / 'licenses'
NUGET = pathlib.Path.home() / '.nuget' / 'packages'
LICENSE_NAMES = ('LICENSE', 'LICENSE.md', 'LICENSE.txt', 'LICENSE.TXT', 'license', 'license.md', 'LICENCE', 'COPYING')


def fail(msg):
    sys.exit('collect_licenses: ' + msg)


def read_license(folder):
    for name in LICENSE_NAMES:
        p = folder / name
        if p.is_file():
            return p.read_text(encoding='utf-8-sig', errors='replace').strip()
    return None


def section(title, text):
    bar = '=' * 78
    return f'{bar}\n{title}\n{bar}\n\n{text.strip()}\n\n'


components = []   # (название, версия, лицензия, что это)
notices = []      # тексты для THIRD-PARTY-NOTICES.txt

OUT.mkdir(parents=True, exist_ok=True)

# ---------- OneScript (MPL-2.0) ----------
onescript = ROOT / 'onescript'
mpl = read_license(onescript)
if not mpl:
    fail('не найден onescript/LICENSE')
# Текстовые файлы пишем с BOM (utf-8-sig): статические серверы часто отдают .txt без charset,
# и без BOM браузер показывает кириллицу кракозябрами
(OUT / 'OneScript-MPL-2.0.txt').write_text(mpl + '\n', encoding='utf-8-sig')
(OUT / 'patch_onescript.py').write_text(
    (ROOT / 'scripts' / 'patch_onescript.py').read_text(encoding='utf-8-sig'), encoding='utf-8-sig')

def git(*args):
    return subprocess.run(['git', '-C', str(onescript), *args], capture_output=True, text=True).stdout

commit = git('rev-parse', 'HEAD').strip() or 'неизвестен'
repo = git('remote', 'get-url', 'origin').strip() or 'https://github.com/EvilBeaver/OneScript.git'
repo_web = re.sub(r'\.git$', '', repo)
# Формат porcelain: «XY путь», первые два символа — статус (могут быть пробелами), поэтому строку не обрезаем
changed = [l[3:] for l in git('status', '--porcelain').splitlines() if l.strip()]
(OUT / 'OneScript-source.txt').write_text(
    'Исходный код OneScript, использованный в этой сборке\n'
    '=====================================================\n\n'
    f'Репозиторий: {repo_web}\n'
    f'Коммит:      {commit}\n'
    f'Архив:       {repo_web}/archive/{commit}.zip\n\n'
    'К исходникам применён скрипт patch_onescript.py (лежит рядом с этим файлом):\n'
    '    python3 patch_onescript.py <папка с исходниками OneScript>\n\n'
    'Изменённые файлы (распространяются под MPL-2.0, как и весь OneScript):\n'
    + ''.join(f'    {f}\n' for f in changed),
    encoding='utf-8-sig')
components.append(('OneScript', commit[:8], 'MPL-2.0', 'движок языка 1С (изменён, исходники выше)'))

# ---------- .NET runtime для WebAssembly ----------
runtimecfg = json.loads((ROOT / 'out' / 'OneScriptWasm.runtimeconfig.json').read_text())
fw = runtimecfg['runtimeOptions']['includedFrameworks'][0]['version']
rt = NUGET / 'microsoft.netcore.app.runtime.mono.browser-wasm' / fw
if not rt.is_dir():
    fail(f'не найден рантайм {rt}')
notices.append(section(f'.NET runtime {fw} (Microsoft.NETCore.App, browser-wasm) — MIT',
                       (rt / 'LICENSE.TXT').read_text(encoding='utf-8-sig')))
notices.append(section(f'.NET runtime {fw} — уведомления о сторонних компонентах (в т.ч. ICU)',
                       (rt / 'THIRD-PARTY-NOTICES.TXT').read_text(encoding='utf-8-sig')))
components.append(('.NET runtime (WebAssembly)', fw, 'MIT', '.NET в браузере, включая ICU'))

# ---------- NuGet-библиотеки в _framework ----------
assets = json.loads((ROOT / 'browser' / 'obj' / 'project.assets.json').read_text())
shipped = {p.stem.lower() for p in (SITE / '_framework').glob('*.wasm')}
for lib, info in assets['libraries'].items():
    if info.get('type') != 'package':
        continue
    name, version = lib.split('/')
    if name.lower() not in shipped:
        continue
    folder = NUGET / name.lower() / version.lower()
    nuspec = next(folder.glob('*.nuspec'), None)
    expr = None
    if nuspec:
        m = re.search(r'<license type="expression">([^<]+)</license>', nuspec.read_text(encoding='utf-8-sig'))
        expr = m.group(1) if m else None
    text = read_license(folder)
    if not text and not expr:
        fail(f'нет лицензии для {lib}')
    label = expr or 'см. текст'
    if name.lower() == 'prodotnetzip':
        label = 'Ms-PL, BSD, Apache-2.0, zlib'
    notices.append(section(f'{name} {version} — {label}', text or f'License: {expr}'))
    components.append((name, version, label, 'нужна OneScript'))

# ---------- npm-пакеты из бандла app.js ----------
meta = json.loads((ROOT / 'page' / 'meta.json').read_text())
packages = set()
for path in meta['inputs']:
    m = re.match(r'(?:.*/)?node_modules/((?:@[^/]+/)?[^/]+)/', path)
    if m:
        packages.add(m.group(1))
for name in sorted(packages):
    folder = ROOT / 'page' / 'node_modules' / name
    pkg = json.loads((folder / 'package.json').read_text())
    lic = pkg.get('license') or 'см. текст'
    text = read_license(folder)
    if not text:
        fail(f'нет файла лицензии у npm-пакета {name}')
    notices.append(section(f'{name} {pkg["version"]} — {lic}', text))
    what = 'подсветка синтаксиса языка 1С' if name.startswith('@1c-syntax/') else 'редактор кода CodeMirror'
    components.append((name, pkg['version'], lic, what))

(OUT / 'THIRD-PARTY-NOTICES.txt').write_text(
    'Уведомления о лицензиях сторонних компонентов, входящих в сайт\n'
    'OneScript (MPL-2.0) — отдельно: OneScript-MPL-2.0.txt и OneScript-source.txt\n\n'
    + ''.join(notices), encoding='utf-8-sig')

# ---------- Страница licenses.html ----------
rows = '\n'.join(
    f'        <tr><td>{html.escape(n)}</td><td class="num">{html.escape(v)}</td>'
    f'<td>{html.escape(l)}</td><td>{html.escape(w)}</td></tr>'
    for n, v, l, w in components)
template = (ROOT / 'page' / 'public' / 'licenses.html').read_text(encoding='utf-8')
page = (template
        .replace('<!--COMPONENTS-->', rows)
        .replace('{{ONESCRIPT_REPO}}', html.escape(repo_web))
        .replace('{{ONESCRIPT_COMMIT}}', html.escape(commit))
        .replace('{{ONESCRIPT_COMMIT_SHORT}}', html.escape(commit[:8])))
if '{{' in page or '<!--COMPONENTS-->' in page:
    fail('в шаблоне licenses.html остались незаменённые метки')
(SITE / 'licenses.html').write_text(page, encoding='utf-8')
print(f'Лицензии: {len(components)} компонентов → site/licenses/, site/licenses.html')
