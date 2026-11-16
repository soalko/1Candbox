# Песочница 1С (OneScript в браузере)

Код на языке 1С выполняется прямо в браузере: движок OneScript скомпилирован в WebAssembly и работает в фоновом потоке (Web Worker). Сервер для запуска кода не нужен, сайт — набор статических файлов.

## Сборка

```bash
./build.sh
python3 -m http.server 8080 --directory site
```

Откройте http://localhost:8080.

Требования: .NET SDK 8+, Node.js 18+, git, python3.

## Устройство

- `browser/Runner.cs` — хост OneScript: принимает код и строки ввода, возвращает вывод `Сообщить()` и ошибки (с номером строки) в JSON. Экспортирован в JavaScript через `[JSExport]`.
- `page/src/app.js` — редактор CodeMirror с подсветкой 1С (`@1c-syntax/codemirror-lang-bsl`), примеры, подсказки о совместимости, таймаут 8 с.
- `page/public/worker.js` — загружает .NET-рантайм и вызывает `Runner.Run(code, input)`.
- `scripts/patch_onescript.py` — убирает из OneScript встроенный веб-сервер, который в браузере не нужен.

## Ограничения

Работает алгоритмическая часть языка: строки, числа, даты, коллекции, ТаблицаЗначений, исключения. Запросов, объектов конфигурации, форм и модулей БСП нет. Это требует платформы 1С с информационной базой.

Лицензии: OneScript — MPL-2.0, CodeMirror и codemirror-lang-bsl — MIT, .NET — MIT.
