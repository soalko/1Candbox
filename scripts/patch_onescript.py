# Адаптирует OneScript для работы в браузере (WebAssembly).
# Скрипт можно запускать повторно: уже применённые правки пропускаются.
import pathlib, sys

root = pathlib.Path(sys.argv[1])

def patch(rel, old, new, label):
    p = root / rel
    s = p.read_text(encoding='utf-8-sig')
    if new in s:
        print('уже применено:', label)
        return
    if old not in s:
        sys.exit(f'Не найден фрагмент для правки «{label}» в {rel}. Возможно, исходники OneScript изменились.')
    p.write_text(s.replace(old, new, 1), encoding='utf-8')
    print('исправлено:', label)

# 1. Встроенный веб-сервер (ASP.NET Core) в браузере не нужен и не собирается под WebAssembly.
p = root / 'src/ScriptEngine.HostedScript/ScriptEngine.HostedScript.csproj'
s = p.read_text(encoding='utf-8-sig')
s2 = '\n'.join(l for l in s.splitlines() if 'OneScript.Web.Server.csproj' not in l) + '\n'
if s2 != s:
    p.write_text(s2, encoding='utf-8')
    print('исправлено: убрана ссылка на OneScript.Web.Server')
else:
    print('уже применено: ссылка на OneScript.Web.Server')

# 2. Консоль подписывается на Ctrl+C (POSIX-сигнал). В браузере сигналов нет: PlatformNotSupportedException.
patch('src/OneScript.StandardLibrary/Text/ConsoleContext.cs',
      '            Console.CancelKeyPress += ConsoleOnCancelKeyPress;',
      '''            try
            {
                Console.CancelKeyPress += ConsoleOnCancelKeyPress;
            }
            catch (PlatformNotSupportedException)
            {
                // В браузере (WebAssembly) сигналов ОС нет, обработчик Ctrl+C не нужен
            }''',
      'обработчик Ctrl+C в Консоли')
