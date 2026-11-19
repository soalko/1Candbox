// Движок OneScript (.NET WebAssembly) в Web Worker: загрузка, запуск кода, проверка без запуска.
// Код выполняется вне основного потока, поэтому зависшую программу можно остановить: worker.terminate()
// и новый воркер.

export const RUN_TIMEOUT_MS = 8000;
const CHECK_TIMEOUT_MS = 3000;

export class Engine {
  /**
   * @param {object} opts
   * @param {string} opts.workerUrl  адрес worker.js
   * @param {(state: 'idle'|'loading'|'ready'|'running'|'fatal', text: string) => void} [opts.onStatus]
   * @param {() => void} [opts.onReady]  движок загрузился (в том числе после перезапуска)
   */
  constructor({ workerUrl, onStatus, onReady }) {
    this.workerUrl = workerUrl;
    this.onStatus = onStatus || (() => {});
    this.onReady = onReady || (() => {});
    this.worker = null;
    this.ready = false;
    this.readyText = 'Движок готов';
    this.pending = null;      // { id, resolve, timer, started }
    this.checkWaiters = new Map();
    this.seq = 0;
    this.readyWaiters = [];
  }

  get started() { return this.worker !== null; }
  get busy() { return this.pending !== null; }

  setStatus(state, text) { this.onStatus(state, text); }

  start() {
    if (this.worker) return;
    this.ready = false;
    this.setStatus('loading', 'Загружаю движок…');
    const started = performance.now();
    const worker = new Worker(this.workerUrl, { type: 'module' });
    this.worker = worker;
    worker.onmessage = (ev) => {
      const msg = ev.data;
      if (msg.type === 'ready') {
        this.ready = true;
        const sec = ((performance.now() - started) / 1000).toFixed(1);
        this.readyText = `Движок готов · OneScript ${msg.version} · загрузка ${sec} с`;
        this.setStatus('ready', this.readyText);
        for (const resolve of this.readyWaiters.splice(0)) resolve(true);
        this.onReady();
      } else if (msg.type === 'check') {
        const resolve = this.checkWaiters.get(msg.id);
        this.checkWaiters.delete(msg.id);
        if (resolve) resolve(msg.diagnostics);
      } else if (msg.type === 'fatal') {
        this.fail('Не удалось запустить движок: ' + msg.message);
      } else if (msg.type === 'result' && this.pending && msg.id === this.pending.id) {
        const { resolve, timer, started: t0 } = this.pending;
        clearTimeout(timer);
        this.pending = null;
        this.setStatus('ready', this.readyText);
        resolve({ ...msg.result, totalMs: Math.round(performance.now() - t0) });
      }
    };
    worker.onerror = (e) => this.fail('Ошибка воркера: ' + (e.message || 'неизвестная ошибка'));
  }

  fail(text) {
    this.setStatus('fatal', 'Движок не загрузился');
    for (const resolve of this.readyWaiters.splice(0)) resolve(false);
    if (this.pending) {
      const { resolve, timer } = this.pending;
      clearTimeout(timer);
      this.pending = null;
      resolve({ exitCode: 2, output: [{ kind: 'err', text }], totalMs: 0 });
    } else {
      this.fatalText = text;
    }
  }

  restart() {
    if (this.worker) this.worker.terminate();
    this.worker = null;
    this.dropCheckWaiters();
    this.start();
  }

  /** Ждёт загрузки движка (запускает загрузку, если её ещё не было). false — движок не загрузился. */
  whenReady() {
    if (this.ready) return Promise.resolve(true);
    this.start();
    return new Promise((resolve) => this.readyWaiters.push(resolve));
  }

  /** Выполняет код. Результат: {exitCode, ms, totalMs, output: [{kind, text}], timedOut?} */
  async run(code, input) {
    if (this.pending) return null;
    if (!(await this.whenReady())) {
      return { exitCode: 2, output: [{ kind: 'err', text: this.fatalText || 'Движок не загрузился.' }], totalMs: 0 };
    }
    if (this.pending) return null;
    const id = ++this.seq;
    this.setStatus('running', 'Выполняю…');
    return new Promise((resolve) => {
      this.pending = {
        id,
        resolve,
        started: performance.now(),
        timer: setTimeout(() => {
          // Бесконечный цикл: останавливаем поток целиком и поднимаем движок заново
          this.pending = null;
          this.restart();
          resolve({
            exitCode: 1,
            timedOut: true,
            totalMs: RUN_TIMEOUT_MS,
            output: [{ kind: 'err', text: `Программа работала дольше ${RUN_TIMEOUT_MS / 1000} с и была остановлена. Проверьте условия выхода из циклов.` }],
          });
        }, RUN_TIMEOUT_MS),
      };
      this.worker.postMessage({ type: 'run', id, code, input: input || '' });
    });
  }

  /** Проверка без запуска: [{line, column, message}] или null, если движок занят или не загружен. */
  check(code) {
    if (!this.worker || !this.ready || this.pending) return Promise.resolve(null);
    const id = ++this.seq;
    return new Promise((resolve) => {
      // Защита от зависания компилятора: перезапускаем воркер, как при таймауте программы
      const timer = setTimeout(() => {
        if (!this.checkWaiters.has(id) || this.pending) return;
        this.restart();
      }, CHECK_TIMEOUT_MS);
      this.checkWaiters.set(id, (diagnostics) => { clearTimeout(timer); resolve(diagnostics); });
      this.worker.postMessage({ type: 'check', id, code });
    });
  }

  dropCheckWaiters() {
    for (const resolve of this.checkWaiters.values()) resolve(null);
    this.checkWaiters.clear();
  }
}
