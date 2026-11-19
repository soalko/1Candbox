// Код и ввод в адресе страницы: #c=<код>&i=<ввод>.
// Текст сжимается deflate-raw (CompressionStream) и кодируется base64url. Первая буква значения —
// формат: «z» — сжатый текст, «u» — несжатый UTF-8 (если сжатие недоступно или не уменьшает размер).
// Всё лежит после «#», поэтому код не уходит на сервер и не попадает в его журналы.

const enc = new TextEncoder();
const dec = new TextDecoder();

// Защита от «бомбы»: ссылка из пары килобайт не должна распаковаться в сотни мегабайт
const MAX_UNPACKED = 1 << 20;

function toBase64Url(bytes) {
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromBase64Url(str) {
  let b64 = str.replace(/-/g, '+').replace(/_/g, '/');
  while (b64.length % 4) b64 += '=';
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

async function readAll(stream, limit) {
  const reader = stream.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > limit) { reader.cancel(); throw new Error('слишком большой текст'); }
    chunks.push(value);
  }
  const out = new Uint8Array(size);
  let pos = 0;
  for (const c of chunks) { out.set(c, pos); pos += c.length; }
  return out;
}

const canCompress = typeof CompressionStream === 'function' && typeof DecompressionStream === 'function';

async function packText(text) {
  const raw = enc.encode(text);
  const plain = 'u' + toBase64Url(raw);
  if (!canCompress) return plain;
  try {
    const packed = await readAll(new Blob([raw]).stream().pipeThrough(new CompressionStream('deflate-raw')), Infinity);
    const z = 'z' + toBase64Url(packed);
    return z.length < plain.length ? z : plain;
  } catch {
    return plain;
  }
}

async function unpackText(value) {
  const kind = value[0];
  const body = fromBase64Url(value.slice(1));
  if (kind === 'u') return dec.decode(body);
  if (kind === 'z') {
    if (!canCompress) throw new Error('браузер не умеет распаковывать ссылку');
    return dec.decode(await readAll(new Blob([body]).stream().pipeThrough(new DecompressionStream('deflate-raw')), MAX_UNPACKED));
  }
  throw new Error('неизвестный формат ссылки');
}

/** {code, input} → строка для адреса после «#» (без самого «#»). extra — дополнительные параметры. */
export async function encodeState({ code, input }, extra = {}) {
  const p = new URLSearchParams();
  p.set('c', await packText(code || ''));
  if (input) p.set('i', await packText(input));
  for (const [k, v] of Object.entries(extra)) if (v != null && v !== '') p.set(k, String(v));
  return p.toString();
}

/**
 * Разбирает location.hash. Возвращает {code, input, example, params} или null, если кода в адресе нет.
 * example — id примера из ссылки вида #ex=recursion. Ошибка распаковки бросает исключение.
 */
export async function decodeState(hash) {
  const p = new URLSearchParams(String(hash || '').replace(/^#/, ''));
  const example = p.get('ex');
  const c = p.get('c');
  if (!c && !example) return { params: p, code: null, input: null, example: null };
  return {
    params: p,
    example,
    code: c ? await unpackText(c) : null,
    input: p.get('i') ? await unpackText(p.get('i')) : '',
  };
}
