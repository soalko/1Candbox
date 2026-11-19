#!/usr/bin/env python3
# Регрессионные тесты языка запросов и примеров. Нужен нативный хост (консольный net8.0 с browser/**/*.cs,
# см. CLAUDE.md): python3 tests/run_query_tests.py <путь к native.dll> [--update]
#   tests/query/NN-имя.os         → вывод программы сравнивается с NN-имя.expected
#   tests/query/NN-имя-check.os   → JSON проверки (--check) сравнивается с NN-имя-check.expected
#   примеры из page/src/examples.js выполняются без ошибок движка
# --update перезаписывает .expected текущим выводом (после ручной проверки!).
import json, pathlib, re, subprocess, sys, tempfile
from concurrent.futures import ThreadPoolExecutor

ROOT = pathlib.Path(__file__).resolve().parent.parent
if len(sys.argv) < 2:
    sys.exit(__doc__ or 'usage: run_query_tests.py <native.dll> [--update]')
DLL = sys.argv[1]
UPDATE = '--update' in sys.argv

def run(path, check=False, stdin=None):
    args = ['dotnet', DLL] + (['--check'] if check else []) + [str(path)] + ([stdin] if stdin else [])
    out = subprocess.run(args, capture_output=True, text=True, encoding='utf-8').stdout
    data = json.loads(out)
    if check:
        return [f"{d['line']}:{d['column']}+{d.get('length', 0)} {d.get('severity', 'error')} {d.get('source', 'Компилятор')}: {d['message']}" for d in data], 0
    lines = []
    for o in data['output']:
        for line in o['text'].split('\n'):
            lines.append(('! ' if o['kind'] == 'err' else '') + line)
    return lines, data['exitCode']

def case(path):
    check = path.stem.endswith('-check')
    actual, _ = run(path, check)
    exp = path.with_suffix('.expected')
    if UPDATE:
        exp.write_text('\n'.join(actual) + '\n', encoding='utf-8')
        return path.name, None
    expected = exp.read_text(encoding='utf-8').rstrip('\n').split('\n') if exp.exists() else []
    if actual == expected:
        return path.name, None
    diff = []
    for i in range(max(len(actual), len(expected))):
        a = actual[i] if i < len(actual) else '<нет строки>'
        e = expected[i] if i < len(expected) else '<нет строки>'
        if a != e:
            diff.append(f'  строка {i + 1}:\n    ожидалось: {e}\n    получено:  {a}')
    return path.name, '\n'.join(diff[:10])

def examples():
    src = (ROOT / 'page/src/examples.js').read_text(encoding='utf-8')
    for m in re.finditer(r"id: '([^']+)'.*?input: '([^']*)'.*?code: `(.*?)`,", src, re.S):
        yield m.group(1), m.group(2).replace('\\n', '\n'), m.group(3)

def example_case(item):
    ex_id, stdin, code = item
    with tempfile.NamedTemporaryFile('w', suffix='.os', delete=False, encoding='utf-8') as f:
        f.write(code)
    lines, code_ = run(f.name, stdin=stdin)
    bad = [l for l in lines if 'Внешнее исключение' in l or 'Внутренняя ошибка' in l]
    # Пример «Обработка ошибок» специально завершается ошибкой выполнения
    if bad or (code_ != 0 and ex_id != 'errors'):
        return 'пример ' + ex_id, '\n'.join('  ' + l for l in lines[-5:])
    return 'пример ' + ex_id, None

tests = sorted((ROOT / 'tests/query').glob('*.os'))
with ThreadPoolExecutor(6) as pool:
    results = list(pool.map(case, tests))
    if not UPDATE:
        results += list(pool.map(example_case, list(examples())))
failed = [(n, d) for n, d in results if d]
for n, d in failed:
    print(f'FAIL {n}\n{d}')
print(f'{len(results) - len(failed)} passed, {len(failed)} failed')
sys.exit(1 if failed else 0)
