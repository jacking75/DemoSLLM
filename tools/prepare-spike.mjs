import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, stat, readFile, rename } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const status = {started: false, finished: false, files: [], error: null};
export function getStatus() { return structuredClone(status); }

async function exists(file) {
  try { return await stat(file); } catch (e) { if (e.code === 'ENOENT') return null; throw e; }
}

async function verify(file, entry) {
  const size = (await stat(file)).size;
  if (size !== entry.size) throw new Error(`${entry.name}: file size mismatch ${size}/${entry.size}`);
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  if (hash.digest('hex') !== entry.sha256) throw new Error(`${entry.name}: SHA-256 mismatch`);
}

async function download(entry, progress) {
  const target = path.resolve(root, entry.path);
  if (!target.startsWith(root + path.sep)) throw new Error('Target outside workspace');
  if (await exists(target)) {
    progress.state = 'verifying-existing';
    await verify(target, entry);
    progress.bytes = entry.size;
    progress.state = 'verified';
    return;
  }
  await mkdir(path.dirname(target), {recursive: true});
  const partial = target + '.part';
  const offset = (await exists(partial))?.size ?? 0;
  if (offset > entry.size) throw new Error(`${entry.name}: unexpected partial file size`);
  progress.bytes = offset;
  if (offset < entry.size) {
    progress.state = 'downloading';
    const res = await fetch(entry.url, {
      headers: offset ? {Range: `bytes=${offset}-`} : {},
      signal: AbortSignal.timeout(60 * 60 * 1000)
    });
    if (res.status !== (offset ? 206 : 200)) throw new Error(`${entry.name}: HTTP ${res.status}`);
    if (offset && !res.headers.get('content-range')?.startsWith(`bytes ${offset}-`)) throw new Error('Invalid range response');
    const meter = new Transform({transform(chunk, encoding, callback) {
      progress.bytes += chunk.length;
      if (progress.bytes > entry.size) callback(new Error('Response exceeds expected size'));
      else callback(null, chunk);
    }});
    await pipeline(Readable.fromWeb(res.body), meter, createWriteStream(partial, {flags: offset ? 'a' : 'wx'}));
  }
  progress.state = 'verifying';
  await verify(partial, entry);
  if (await exists(target)) throw new Error('Target appeared during download; preserving both files');
  await rename(partial, target);
  progress.state = 'verified';
}

export async function prepare(names = null) {
  if (status.started) throw new Error('Preparation already started');
  status.started = true;
  const allEntries = JSON.parse(await readFile(path.join(root, 'tools/spike-artifacts.json'), 'utf8'));
  const entries = names === null ? allEntries : allEntries.filter(e => names.includes(e.name));
  if (names !== null && entries.length !== names.length) throw new Error('Unknown artifact name');
  status.files = entries.map(e => ({name:e.name, bytes:0, total:e.size, state:'pending'}));
  let next = 0;
  async function worker() {
    while (next < entries.length) {
      const index = next++;
      try { await download(entries[index], status.files[index]); }
      catch (e) { status.files[index].state = 'failed'; status.files[index].error = e.message; }
    }
  }
  await Promise.all([worker(), worker()]);
  status.finished = true;
  if (status.files.some(f => f.state !== 'verified')) status.error = 'Some downloads failed; existing files and partial downloads were preserved';
  return getStatus();
}
