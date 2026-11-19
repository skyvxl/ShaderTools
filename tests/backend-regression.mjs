#!/usr/bin/env node
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Use the same Bridge and server as VS, and record every terminal response, including duplicates.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const bridge = process.argv.includes('--bridge') ? path.resolve(process.argv[process.argv.indexOf('--bridge') + 1])
  : path.join(root, 'ShaderTools.Bridge/bin/Release/net472/ShaderTools.Bridge.exe');
const server = process.argv.includes('--server') ? path.resolve(process.argv[process.argv.indexOf('--server') + 1])
  : path.join(root, 'ShaderTools.VisualStudio/Server/shader-language-server.exe');
const temporary = mkdtempSync(path.join(os.tmpdir(), 'shader-backend-regression-'));
const source = '#version 450\nstruct Material { float intensity; };\nfloat computeLocal(float value) { return value * 2.0; }\nvoid main() { float sampleValue = computeLocal(1.0); }\n';
const file = path.join(temporary, 'main.frag.glsl');
writeFileSync(file, source);
const uri = pathToFileURL(file).href;
const config = { validate: true, symbols: true, automaticVariantDiscovery: false, includes: [], defines: { BASE_CONFIG: '1' } };
writeFileSync(path.join(temporary, '.shadertools.json'), JSON.stringify(config));
const child = spawn(bridge, ['--server', server, '--workspace', temporary],
  { cwd: temporary, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
const closed = new Promise(resolve => child.once('close', resolve));
let buffer = Buffer.alloc(0), next = 10000, failures = 0;
const responses = new Map(), pending = new Map();
const key = id => JSON.stringify(id);
const send = message => {
  const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', ...message }));
  child.stdin.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`), body]));
};
const notify = (method, params) => send({ method, params });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const request = (method, params, id = next++, timeout = 3000) => {
  const promise = new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(key(id)); reject(new Error(`No terminal response for ${key(id)} within ${timeout} ms`)); }, timeout);
    pending.set(key(id), message => { clearTimeout(timer); pending.delete(key(id)); resolve(message); });
  });
  send({ id, method, params });
  return promise;
};
child.stdout.on('data', chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  while (true) {
    const end = buffer.indexOf('\r\n\r\n');
    if (end < 0) return;
    const length = Number(buffer.subarray(0, end).toString().match(/Content-Length:\s*(\d+)/i)?.[1]);
    assert.ok(Number.isFinite(length));
    if (buffer.length < end + 4 + length) return;
    const message = JSON.parse(buffer.subarray(end + 4, end + 4 + length));
    buffer = buffer.subarray(end + 4 + length);
    if (message.method && Object.hasOwn(message, 'id')) {
      const result = message.method === 'workspace/configuration' ? message.params.items.map(() => config)
        : message.method === 'textDocument/shaderVariant' ? { shaderVariant: null } : null;
      send({ id: message.id, result });
    } else if (Object.hasOwn(message, 'id')) {
      const id = key(message.id);
      responses.set(id, [...(responses.get(id) ?? []), message]);
      pending.get(id)?.(message);
    }
  }
});
let stderr = '';
child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-8_192); });
child.on('error', error => { console.error(error); process.exitCode = 1; });
const test = async (name, action) => {
  try { await action(); console.log(`PASS ${name}`); }
  catch (error) { failures++; console.error(`FAIL ${name}: ${error.message}`); }
};
const hover = { textDocument: { uri }, position: { line: 3, character: 37 } };
const projectSettings = (file, settings) => {
  writeFileSync(file, JSON.stringify(settings));
  notify('workspace/didChangeConfiguration', { settings: {} });
};
const errors = async uri => {
  const response = await request('textDocument/diagnostic', { textDocument: { uri } });
  assert.equal(response.result?.kind, 'full', JSON.stringify(response));
  return response.result.items.filter(item => item.severity === 1);
};
try {
  assert.ok((await request('initialize', { processId: process.pid, rootUri: pathToFileURL(temporary).href, capabilities: {} })).result);
  notify('initialized', {});
  notify('textDocument/didOpen', { textDocument: { uri, languageId: 'glsl', version: 1, text: source } });
  assert.ok((await request('textDocument/documentSymbol', { textDocument: { uri } })).result.length);
  await test('compilation context reflects the actual analysis parameters', async () => {
    const response = await request('shaderTools/compilationContext', { textDocument: { uri } });
    assert.ok(response.result, JSON.stringify(response));
    assert.equal(response.result.defines.BASE_CONFIG, '1');
    assert.ok(response.result.includes.includes(temporary));
    assert.deepEqual(response.result.pathRemapping, {});
    assert.equal(response.result.documentVersion, 1);
  });
  await test('compilation context preserves backend variant merge precedence', async () => {
    const variantInclude = path.join(temporary, 'variant-includes');
    mkdirSync(variantInclude);
    notify('textDocument/didChangeShaderVariant', { shaderVariant: {
      url: uri, shadingLanguage: 'Glsl', entryPoint: 'main', stage: 'fragment',
      defines: { BASE_CONFIG: '2', VARIANT_FEATURE: '1' }, includes: [variantInclude]
    } });
    try {
      const response = await request('shaderTools/compilationContext', { textDocument: { uri } });
      assert.equal(response.result?.defines.BASE_CONFIG, '1');
      assert.equal(response.result?.defines.VARIANT_FEATURE, '1');
      assert.equal(response.result?.stage, 'fragment');
      assert.deepEqual(response.result?.includes, [temporary, variantInclude]);
    } finally { notify('textDocument/didChangeShaderVariant', { shaderVariant: null }); }
  });
  await test('sibling document configurations isolate includes, validation, reload, and close/reopen', async () => {
    const roots = ['project-a', 'project-b'].map(name => {
      const directory = path.join(temporary, name), include = path.join(directory, 'includes');
      mkdirSync(include, { recursive: true });
      writeFileSync(path.join(include, 'settings.glsl'), `float ${name === 'project-a' ? 'fromProjectA' : 'fromProjectB'}() { return 1.0; }\n`);
      const text = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#include "settings.glsl"\nvoid main() { missingFunction(); }\n';
      const file = path.join(directory, 'main.frag.glsl');
      writeFileSync(file, text);
      return { uri: pathToFileURL(file).href, directory, include, text, configurationPath: path.join(directory, '.shadertools.json') };
    });
    const [a, b] = roots;
    const settings = (item, extra = {}) => ({ ...config, includes: [item.include], defines: { PROJECT: item === a ? 'A' : 'B' }, ...extra });
    const configure = (item, value = settings(item)) => projectSettings(item.configurationPath, value);
    const opened = new Set();
    const open = item => { notify('textDocument/didOpen', { textDocument: { uri: item.uri, languageId: 'glsl', version: 1, text: item.text } }); opened.add(item.uri); };
    const context = async item => (await request('shaderTools/compilationContext', { textDocument: { uri: item.uri } })).result;
    const symbols = async () => (await request('workspace/symbol', { query: 'fromProject' })).result;
    try {
      configure(a); configure(b, settings(b, { validate: false })); open(a); open(b);
      assert.deepEqual((await context(a)).includes, [a.directory, a.include]);
      assert.deepEqual((await context(b)).includes, [b.directory, b.include]);
      assert.equal((await context(a)).configurationPath, a.configurationPath);
      assert.deepEqual((await symbols()).map(s => s.name).sort(), ['fromProjectA', 'fromProjectB']);
      writeFileSync(a.configurationPath, '{');
      notify('workspace/didChangeConfiguration', { settings: {} });
      assert.equal((await context(a)).configurationPath, a.configurationPath);
      assert.equal((await context(b)).defines.PROJECT, 'B');
      assert.ok((await errors(a.uri)).length > 0);
      assert.equal((await errors(b.uri)).length, 0);

      // A configuration-only edit must replace symbols and diagnostics at the same text version.
      configure(a, settings(a, { includes: [b.include], validate: false, glsl: { targetClient: 'OpenGL450', spirvVersion: 'SPIRV1_0' } }));
      const changed = await context(a);
      assert.equal(changed.documentVersion, 1);
      assert.deepEqual(changed.includes, [a.directory, b.include]);
      assert.equal(changed.glsl.targetClient, 'OpenGL450');
      assert.equal((await errors(a.uri)).length, 0);
      assert.deepEqual((await symbols()).map(s => s.name), ['fromProjectB']);
      assert.equal((await context(b)).defines.PROJECT, 'B');

      // Reloading the workspace must preserve each project's configuration.
      notify('workspace/didChangeConfiguration', { settings: {} });
      assert.deepEqual((await context(a)).includes, [a.directory, b.include]);
      assert.equal((await context(b)).defines.PROJECT, 'B');

      notify('textDocument/didChangeShaderVariant', { shaderVariant: { url: a.uri, shadingLanguage: 'Glsl', entryPoint: 'main', stage: 'fragment', defines: { PROJECT: 'variant', VARIANT_LOCAL: '1' }, includes: [] } });
      assert.equal((await context(a)).defines.PROJECT, 'A');
      assert.equal((await context(a)).defines.VARIANT_LOCAL, '1');
      notify('textDocument/didClose', { textDocument: { uri: a.uri } });
      opened.delete(a.uri);
      assert.equal((await context(a)).configurationPath, a.configurationPath);
      assert.equal((await context(a)).defines.PROJECT, 'A');
      notify('textDocument/didChangeShaderVariant', { shaderVariant: null });
      open(a);
      assert.equal((await context(a)).configurationPath, a.configurationPath);
      assert.equal((await context(a)).defines.VARIANT_LOCAL, undefined);
      configure(a);
      assert.deepEqual((await context(a)).includes, [a.directory, a.include]);
      assert.deepEqual((await symbols()).map(s => s.name).sort(), ['fromProjectA', 'fromProjectB']);
    } finally {
      notify('textDocument/didChangeShaderVariant', { shaderVariant: null });
      for (const documentUri of opened) notify('textDocument/didClose', { textDocument: { uri: documentUri } });
    }
  });
  await test('closed selected variant keeps its root settings for open headers until release', async () => {
    const directory = path.join(temporary, 'retained-variant');
    mkdirSync(directory);
    const includes = path.join(temporary, 'independent-header');
    mkdirSync(includes);
    const rootFile = path.join(directory, 'main.frag.glsl'), headerFile = path.join(includes, 'shared.glsl');
    const rootUri = pathToFileURL(rootFile).href, headerUri = pathToFileURL(headerFile).href;
    const rootText = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#if ROOT_FEATURE == 1\n#include "shared.glsl"\n#endif\nvoid main() {}\n';
    const headerText = '#if PROJECT_FEATURE != 7\n#error Project define lost\n#endif\nfloat fromRetainedHeader() { return 1.0; }\n';
    writeFileSync(rootFile, rootText); writeFileSync(headerFile, headerText);
    const configurationPath = path.join(directory, '.shadertools.json');
    const configureRoot = (value, includeHeader = true) => projectSettings(configurationPath,
      { ...config, includes: [includes], defines: { PROJECT_FEATURE: value, ROOT_FEATURE: includeHeader ? '1' : '0' } });
    const context = async documentUri => (await request('shaderTools/compilationContext', { textDocument: { uri: documentUri } })).result;
    configureRoot('7');
    // A sibling config prevents inherited root defines from hiding a failed detach.
    projectSettings(path.join(includes, '.shadertools.json'), { validate: false, defines: { HEADER_ONLY: '1' } });
    notify('textDocument/didOpen', { textDocument: { uri: headerUri, languageId: 'glsl', version: 1, text: headerText } });
    notify('textDocument/didChangeShaderVariant', { shaderVariant: { url: rootUri, shadingLanguage: 'Glsl', entryPoint: 'main', stage: 'fragment', defines: {}, includes: [] } });
    let rootOpen = false;
    try {
      assert.equal((await context(rootUri)).configurationPath, configurationPath);
      assert.equal((await context(headerUri)).defines.PROJECT_FEATURE, '7');
      notify('textDocument/didOpen', { textDocument: { uri: rootUri, languageId: 'glsl', version: 1, text: rootText } }); rootOpen = true;
      notify('textDocument/didClose', { textDocument: { uri: rootUri } }); rootOpen = false;
      assert.equal((await context(rootUri)).configurationPath, configurationPath);
      assert.equal((await context(headerUri)).defines.PROJECT_FEATURE, '7');
      assert.equal((await errors(headerUri)).length, 0);
      configureRoot('8');
      assert.equal((await context(headerUri)).defines.PROJECT_FEATURE, '8');
      assert.ok((await errors(headerUri)).length > 0);
      configureRoot('7');
      await context(headerUri);
      assert.equal((await errors(headerUri)).length, 0);
      // A configuration-only include-tree change must release the former header context too.
      configureRoot('7', false);
      assert.equal((await context(rootUri)).defines.ROOT_FEATURE, '0');
      assert.equal((await context(headerUri)).defines.HEADER_ONLY, '1');
      assert.equal((await context(headerUri)).defines.PROJECT_FEATURE, undefined);
      notify('textDocument/didChangeShaderVariant', { shaderVariant: null });
      assert.equal((await context(headerUri)).defines.HEADER_ONLY, '1');
      assert.equal((await context(headerUri)).defines.PROJECT_FEATURE, undefined);
      notify('textDocument/didOpen', { textDocument: { uri: rootUri, languageId: 'glsl', version: 1, text: rootText } }); rootOpen = true;
      assert.equal((await context(rootUri)).configurationPath, configurationPath);
      assert.equal((await context(rootUri)).defines.PROJECT_FEATURE, '7');
    } finally {
      notify('textDocument/didChangeShaderVariant', { shaderVariant: null });
      if (rootOpen) notify('textDocument/didClose', { textDocument: { uri: rootUri } });
      notify('textDocument/didClose', { textDocument: { uri: headerUri } });
    }
  });
  await test('optional absent include and remapping paths preserve valid document settings', async () => {
    const directory = path.join(temporary, 'optional-project'), include = path.join(directory, 'includes');
    const absent = path.join(directory, 'optional-generated');
    mkdirSync(include, { recursive: true });
    writeFileSync(path.join(include, 'optional.glsl'), 'float fromOptionalInclude() { return 1.0; }\n');
    const file = path.join(directory, 'main.frag.glsl'), documentUri = pathToFileURL(file).href;
    const configurationPath = path.join(directory, '.shadertools.json');
    const text = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#include "optional.glsl"\nvoid main() {}\n';
    writeFileSync(file, text);
    projectSettings(configurationPath,
      { ...config, includes: [absent, include], pathRemapping: { '/optional': absent }, defines: { OPTIONAL_PROJECT: '1' } });
    notify('textDocument/didOpen', { textDocument: { uri: documentUri, languageId: 'glsl', version: 1, text } });
    try {
      const context = (await request('shaderTools/compilationContext', { textDocument: { uri: documentUri } })).result;
      assert.equal(context.configurationPath, configurationPath);
      assert.deepEqual(context.includes, [directory, absent, include]);
      assert.equal(context.defines.OPTIONAL_PROJECT, '1');
      assert.deepEqual(context.pathRemapping, { '/optional': absent });
      const symbols = (await request('workspace/symbol', { query: 'fromOptionalInclude' })).result;
      assert.equal(symbols.length, 1);
      assert.equal(symbols[0].location.uri, pathToFileURL(path.join(include, 'optional.glsl')).href);
      assert.equal((await errors(documentUri)).length, 0);
      assert.equal((await request('shaderTools/compilationContext', { textDocument: { uri } })).result.defines.OPTIONAL_PROJECT, undefined);
    } finally { notify('textDocument/didClose', { textDocument: { uri: documentUri } }); }
  });
  await test('HLSL settings and intrinsics remain local and refresh without a text edit', async () => {
    const text = 'float4 main() : SV_Target { return __spirv__; }\n';
    const roots = ['old', 'new'].map(name => {
      const directory = path.join(temporary, name);
      mkdirSync(directory);
      return pathToFileURL(path.join(directory, 'main.hlsl')).href;
    });
    const configure = (rootUri, newer) => projectSettings(path.join(path.dirname(fileURLToPath(rootUri)), '.shadertools.json'),
      { ...config, validate: false, hlsl: { shaderModel: newer ? 'ShaderModel6_8' : 'ShaderModel6', version: newer ? 'V2021' : 'V2016', enable16bitTypes: newer, spirv: newer } });
    const context = async rootUri => (await request('shaderTools/compilationContext', { textDocument: { uri: rootUri } })).result;
    const spirvHover = async rootUri => (await request('textDocument/hover', { textDocument: { uri: rootUri }, position: { line: 0, character: text.indexOf('__spirv__') + 3 } })).result;
    try {
      for (const [index, rootUri] of roots.entries()) {
        writeFileSync(fileURLToPath(rootUri), text);
        configure(rootUri, index === 1);
        notify('textDocument/didOpen', { textDocument: { uri: rootUri, languageId: 'hlsl', version: 1, text } });
      }
      assert.deepEqual((await context(roots[0])).hlsl, { shaderModel: 'ShaderModel6', version: 'V2016', enable16bitTypes: false, spirv: false });
      assert.deepEqual((await context(roots[1])).hlsl, { shaderModel: 'ShaderModel6_8', version: 'V2021', enable16bitTypes: true, spirv: true });
      assert.equal(await spirvHover(roots[0]), null);
      assert.match((await spirvHover(roots[1])).contents.value, /#define __spirv__ 1/);
      configure(roots[0], true);
      assert.match((await spirvHover(roots[0])).contents.value, /#define __spirv__ 1/);
      assert.equal((await context(roots[0])).documentVersion, 1);
      assert.equal((await context(roots[1])).hlsl.version, 'V2021');
    } finally {
      for (const rootUri of roots) notify('textDocument/didClose', { textDocument: { uri: rootUri } });
    }
  });
  for (const [name, a, b, cancelled] of [
    ['cancel A preserves B', 1, 2, 1],
    ['cancel B preserves A', 3, 4, 4],
    ['string cancellation is distinct from numeric ID', 7, '7', '7'],
    ['numeric cancellation is distinct from string ID', 8, '8', 8],
    ['unknown cancellation does nothing', 9, 10, 'missing'],
  ]) await test(name, async () => {
    const first = request('textDocument/hover', hover, a), second = request('textDocument/hover', hover, b);
    notify('$/cancelRequest', { id: cancelled });
    notify('$/cancelRequest', { id: cancelled });
    const values = await Promise.all([first, second]);
    for (const [index, id] of [a, b].entries()) {
      if (id === cancelled) assert.equal(values[index].error?.code, -32800);
      else assert.ok(Object.hasOwn(values[index], 'result'), JSON.stringify(values[index]));
      assert.equal(responses.get(key(id))?.length, 1);
    }
  });
  await test('unopened file receives a terminal error', async () => {
    const unopened = path.join(temporary, 'unopened.frag.glsl');
    writeFileSync(unopened, source);
    const result = await request('textDocument/hover', { ...hover, textDocument: { uri: pathToFileURL(unopened).href } });
    assert.ok(result.error, JSON.stringify(result));
  });
  await test('unmatched workspace query is empty', async () => {
    assert.deepEqual((await request('workspace/symbol', { query: 'NoSuchSymbol_7d19eafc' })).result, []);
  });
  await test('workspace query ignores case and results are unique and stable', async () => {
    const symbols = (await request('workspace/symbol', { query: 'COMPUTEloc' })).result;
    assert.equal(symbols.length, 1);
    assert.equal(symbols[0].name, 'computeLocal');
    const all = (await request('workspace/symbol', { query: '' })).result;
    assert.equal(new Set(all.map(s => JSON.stringify([s.name, s.kind, s.location]))).size, all.length);
    assert.deepEqual((await request('workspace/symbol', { query: '' })).result, all);
  });
  await test('shared include symbols are deduplicated across opened roots', async () => {
    writeFileSync(path.join(temporary, 'shared.glsl'), 'float sharedFunction() { return 1.0; }\n');
    const roots = ['first.frag.glsl', 'second.frag.glsl'].map(name => pathToFileURL(path.join(temporary, name)).href);
    for (const rootUri of roots) {
      const text = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#include "shared.glsl"\nvoid main() {}\n';
      writeFileSync(fileURLToPath(rootUri), text);
      notify('textDocument/didOpen', { textDocument: { uri: rootUri, languageId: 'glsl', version: 1, text } });
    }
    try {
      const symbols = (await request('workspace/symbol', { query: 'sharedFunction' })).result;
      assert.equal(symbols.length, 1);
      assert.equal(symbols[0].name, 'sharedFunction');
    } finally {
      for (const rootUri of roots) notify('textDocument/didClose', { textDocument: { uri: rootUri } });
    }
  });
  await test('VS encoded drive URIs round-trip through definition requests', async () => {
    const file = path.join(temporary, 'encoded path.frag');
    const target = pathToFileURL(file).href.replace(/^file:\/\/\/([a-z]):/i, 'file:///$1%3A');
    const text = '#version 450\nfloat fromEncoded() { return 1.0; }\nvoid main() { float value = fromEncoded(); }\n';
    writeFileSync(file, text);
    notify('textDocument/didOpen', { textDocument: { uri: target, languageId: 'shader-tools-glsl', version: 1, text } });
    try {
      const reply = await request('textDocument/definition', { textDocument: { uri: target }, position: { line: 2, character: 30 } });
      const locations = [].concat(reply.result ?? []);
      assert.ok(locations.some(item => (item.targetUri ?? item.uri) === target), JSON.stringify(reply));
    } finally { notify('textDocument/didClose', { textDocument: { uri: target } }); }
  });
  await test('type symbols identify their struct declaration and selection', async () => {
    const symbols = (await request('textDocument/documentSymbol', { textDocument: { uri } })).result;
    const material = symbols.find(s => s.name === 'Material');
    assert.equal(material?.detail, 'struct Material');
    assert.deepEqual(material.selectionRange, { start: { line: 1, character: 7 }, end: { line: 1, character: 15 } });
  });
  await test('the function body range encloses its local variable for navigation', async () => {
    const symbols = (await request('textDocument/documentSymbol', { textDocument: { uri } })).result;
    const main = symbols.find(s => s.name === 'main');
    assert.equal(main?.kind, 12);
    // shader-sense 1.6.2 emits flat locals. VS uses the function range to omit them from Global Scope.
    const local = [...symbols, ...(main.children ?? [])].find(s => s.name === 'sampleValue');
    assert.equal(local?.kind, 13);
    assert.deepEqual(local.selectionRange.start, { line: 3, character: source.split('\n')[3].indexOf('sampleValue') });
    assert.equal(main.range.start.line, 3);
    assert.equal(main.range.end.line, 3);
    assert.ok(main.range.start.character < local.selectionRange.start.character);
    assert.ok(main.range.end.character >= local.selectionRange.end.character);
  });
  await test('sustained changes do not starve a response', async () => {
    let version = 1;
    const traffic = setInterval(() => notify('textDocument/didChange', {
      textDocument: { uri, version: ++version }, contentChanges: [{ text: source + `// ${version}\n` }]
    }), 15);
    try {
      const started = performance.now();
      const result = await request('textDocument/hover', hover, next++, 1500);
      assert.ok(Object.hasOwn(result, 'result'));
      assert.ok(performance.now() - started < 1500);
    } finally { clearInterval(traffic); }
    await sleep(250);
  });
  await test('all received request IDs have exactly one terminal response', async () => {
    await sleep(250);
    for (const [id, messages] of responses) assert.equal(messages.length, 1, `Duplicate completion for ${id}`);
  });
  await test('analysis context and pulled diagnostics reflect the unsaved document', async () => {
    notify('textDocument/didChange', { textDocument: { uri, version: 500 }, contentChanges: [{ text: source + '\nthis_is_invalid\n' }] });
    const result = await request('shaderTools/compilationContext', { textDocument: { uri } });
    assert.equal(result.result?.documentVersion, 500);
    assert.ok((await errors(uri)).some(item => item.range.start.line >= 4));
  });
} finally {
  try { await request('shutdown', null); notify('exit', null); child.stdin.end(); } catch { child.kill(); }
  await Promise.race([closed, sleep(1000)]);
  if (child.exitCode === null) { child.kill(); await closed; }
  rmSync(temporary, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
}
console.log(`FAILURES ${failures}`);
if (failures) console.error(stderr);
process.exitCode = failures ? 1 : 0;
