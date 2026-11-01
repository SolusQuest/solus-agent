import { crc32 } from 'node:zlib';
import { dirname } from 'node:path';
import { sha, run, text } from './common.ts';

// Only the SDK-generated DLL/XML package shape used by this four-library set.
export function readPackage(path: string, id: string, expectedVersion: string) {
  const names = text('unzip', ['-Z1', path], dirname(path)).trim().split(/\r?\n/);
  const meta = names.filter(n => /^package\/services\/metadata\/core-properties\/[a-f0-9]+\.psmdcp$/.test(n));
  const required = [`${id}.nuspec`, `lib/net10.0/${id}.dll`, `lib/net10.0/${id}.xml`, '[Content_Types].xml', '_rels/.rels'];
  if (meta.length !== 1 || names.length !== 6 || new Set(names).size !== names.length || names.some(n => !required.includes(n) && !meta.includes(n))) throw new Error('package_assets');
  const entries = new Map(names.map(name => [name, run('unzip', ['-p', path, name.replaceAll('[', '\\[').replaceAll(']', '\\]')], dirname(path))]));
  const nuspec = entries.get(`${id}.nuspec`)!.toString('utf8');
  if (!nuspec.includes(`<id>${id}</id>`) || !nuspec.includes(`<version>${expectedVersion}</version>`)) throw new Error('package_identity');
  const dependencies: Record<string, string> = {};
  for (const match of nuspec.matchAll(/<dependency\s+([^>]+)\/?\s*>/g)) {
    const name = /\bid="([^"]+)"/.exec(match[1])?.[1]; const range = /\bversion="([^"]+)"/.exec(match[1])?.[1];
    if (!name || !range || Object.hasOwn(dependencies, name)) throw new Error('package_dependencies');
    dependencies[name] = range;
  }
  return { entries, nuspec, dependencies, dllSha256: sha(entries.get(`lib/net10.0/${id}.dll`)!) };
}

export function checkDependencies(actual: Record<string, string>, expected: Record<string, string>) {
  if (JSON.stringify(Object.keys(actual).sort()) !== JSON.stringify(Object.keys(expected).sort())
    || Object.entries(expected).some(([name, version]) => actual[name] !== `[${version}]`)) throw new Error('package_dependencies');
}

// Standard ZIP (stored entries), fixed timestamp and OPC part name. No semantic package rewrite.
export function canonicalPackage(entries: Map<string, Buffer>, id: string, version: string) {
  const copy = new Map(entries);
  const old = [...copy.keys()].find(n => n.endsWith('.psmdcp'))!;
  if (!old) throw new Error('package_metadata');
  const renamed = `package/services/metadata/core-properties/${sha(`${id}/${version}`).slice(0, 32)}.psmdcp`;
  const relationships = copy.get('_rels/.rels')!.toString('utf8');
  if (relationships.split(old).length !== 2) throw new Error('package_relationships');
  const normalized = relationships.replace(old, renamed).replace(/(<Relationship Type="http:\/\/schemas.openxmlformats.org\/package\/2006\/relationships\/metadata\/core-properties" Target="[^"]+" Id=")[^"]+("\s*\/>)/, '$1RCoreProperties$2');
  if (!normalized.includes('Id="RCoreProperties"')) throw new Error('package_relationships');
  copy.set('_rels/.rels', Buffer.from(normalized));
  copy.set(renamed, copy.get(old)!); if (old !== renamed) copy.delete(old);
  const locals: Buffer[] = []; const central: Buffer[] = []; let offset = 0;
  for (const [name, data] of [...copy].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) {
    if (data.length > 16 * 1024 ** 2 || !/^[A-Za-z0-9._/[\]-]+$/.test(name) || name.includes('..')) throw new Error('package_assets');
    const filename = Buffer.from(name); const crc = crc32(data);
    const header = Buffer.alloc(30); header.writeUInt32LE(0x04034b50); header.writeUInt16LE(20, 4); header.writeUInt16LE(0x800, 6);
    header.writeUInt16LE(0x21, 12); header.writeUInt32LE(crc, 14); header.writeUInt32LE(data.length, 18); header.writeUInt32LE(data.length, 22); header.writeUInt16LE(filename.length, 26);
    const directory = Buffer.alloc(46); directory.writeUInt32LE(0x02014b50); directory.writeUInt16LE(0x314, 4); directory.writeUInt16LE(20, 6); directory.writeUInt16LE(0x800, 8);
    directory.writeUInt16LE(0x21, 14); directory.writeUInt32LE(crc, 16); directory.writeUInt32LE(data.length, 20); directory.writeUInt32LE(data.length, 24);
    directory.writeUInt16LE(filename.length, 28); directory.writeUInt32LE((0o100644 << 16) >>> 0, 38); directory.writeUInt32LE(offset, 42);
    locals.push(header, filename, data); central.push(directory, filename); offset += header.length + filename.length + data.length;
  }
  const directory = Buffer.concat(central); const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50); end.writeUInt16LE(copy.size, 8); end.writeUInt16LE(copy.size, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}
