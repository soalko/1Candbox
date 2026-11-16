// Фоновый поток: загружает .NET-рантайм и движок OneScript, выполняет код по запросу.
// Код выполняется вне основного потока, поэтому страницу можно остановить по таймауту (worker.terminate()).
import { dotnet } from './_framework/dotnet.js';

let runner = null;

const ready = (async () => {
  const runtime = await dotnet
    .withApplicationCulture('ru-RU')
    .create();
  const exports = await runtime.getAssemblyExports('OneScriptWasm.dll');
  runner = exports.OneScriptWasm.Runner;
  postMessage({ type: 'ready', version: runner.Version() });
})().catch((e) => {
  postMessage({ type: 'fatal', message: String((e && e.message) || e) });
});

self.onmessage = async (ev) => {
  const msg = ev.data;
  if (msg.type !== 'run') return;
  await ready;
  if (!runner) return;
  let result;
  try {
    result = JSON.parse(runner.Run(msg.code, msg.input || ''));
  } catch (e) {
    result = { exitCode: 2, ms: 0, output: [{ kind: 'err', text: 'Сбой движка: ' + String((e && e.message) || e) }] };
  }
  postMessage({ type: 'result', id: msg.id, result });
};
