// Окно вывода: строки программы, ошибки красным, «Строка N» — ссылка на строку в редакторе.

export function showPlaceholder(container, text) {
  const p = document.createElement('p');
  p.className = 'placeholder';
  p.textContent = text;
  container.replaceChildren(p);
}

export function renderOutput(container, items, exitCode, onGoto) {
  container.replaceChildren();
  if (!items || items.length === 0) {
    showPlaceholder(container, exitCode === 0 ? 'Программа выполнена, вывода нет. Используйте Сообщить().' : 'Нет вывода.');
    return;
  }
  const frag = document.createDocumentFragment();
  for (const it of items) {
    for (const line of String(it.text).split(/\r?\n/)) {
      const div = document.createElement('div');
      div.className = 'line ' + (it.kind === 'err' ? 'line-err' : 'line-out');
      const m = it.kind === 'err' && onGoto && line.match(/^Строка (\d+):/);
      if (m) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'goto';
        btn.textContent = `Строка ${m[1]}`;
        btn.title = 'Перейти к строке в редакторе';
        btn.addEventListener('click', () => onGoto(Number(m[1])));
        div.append(btn, document.createTextNode(line.slice(m[0].length - 1)));
      } else {
        div.textContent = line;
      }
      frag.appendChild(div);
    }
  }
  container.appendChild(frag);
}

/** Подпись под заголовком «Вывод»: время или причина остановки */
export function resultMeta(res) {
  if (res.timedOut) return 'Остановлено по таймауту';
  return res.exitCode === 0 ? `Выполнено за ${res.totalMs} мс` : `Завершено с ошибкой · ${res.totalMs} мс`;
}
