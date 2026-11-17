'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function extractFunction(script, name) {
  const header = new RegExp(`(?:async\\s+)?function\\s+${name}\\s*\\(`).exec(script);
  assert.ok(header, `Missing production ${name} handler.`);
  const start = header.index;
  const openBrace = script.indexOf('{', start + header[0].length);
  assert.ok(openBrace >= 0, `Missing body for ${name}.`);
  let depth = 0;
  let quote = null;
  let lineComment = false;
  let blockComment = false;
  for (let i = openBrace; i < script.length; i++) {
    const ch = script[i];
    const next = script[i + 1];
    if (lineComment) {
      if (ch === '\n') lineComment = false;
      continue;
    }
    if (blockComment) {
      if (ch === '*' && next === '/') { blockComment = false; i++; }
      continue;
    }
    if (quote) {
      if (ch === '\\') { i++; continue; }
      if (ch === quote) quote = null;
      continue;
    }
    if (ch === '/' && next === '/') { lineComment = true; i++; continue; }
    if (ch === '/' && next === '*') { blockComment = true; i++; continue; }
    if (ch === '\'' || ch === '"' || ch === '`') { quote = ch; continue; }
    if (ch === '{') depth++;
    else if (ch === '}' && --depth === 0) return script.slice(start, i + 1);
  }
  assert.fail(`Unterminated body for ${name}.`);
}

function readCSharpSources(dir) {
  let combined = '';
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) combined += readCSharpSources(full);
    else if (entry.name.endsWith('.cs')) combined += '\n' + fs.readFileSync(full, 'utf8');
  }
  return combined;
}

const htmlPath = path.resolve(__dirname, '../src/TuckPane/Assets/NoteEditor.html');
const script = fs.readFileSync(htmlPath, 'utf8').match(/<script\b[^>]*>([\s\S]*?)<\/script>/i)?.[1];
assert.ok(script, 'Missing inline editor script.');
new vm.Script(script, { filename: htmlPath }); // Syntax only; never run the editor/DOM.
const source = extractFunction(script, 'onCutText');
assert.match(script, /editor\.addEventListener\('cut',\s*onCutText\)/);
assert.doesNotMatch(source, /preventDefault|deleteFromDocument|deleteContents|execCommand/);

let selected = '中文第一行\n第二行';
const timers = [], messages = [];
const cut = vm.runInNewContext(`(${source})`, {
  window: { getSelection: () => selected == null ? null : { toString: () => selected } },
  setTimeout: (callback, delay) => { assert.equal(delay, 0); timers.push(callback); },
  post: message => messages.push(JSON.parse(JSON.stringify(message)))
}, { timeout: 1000 });
const event = { preventDefault: () => assert.fail('Default cut must remain enabled.') };
cut(event);
assert.equal(messages.length, 0, 'Do not race the browser default clipboard write.');
assert.equal(timers.length, 1, 'Schedule exactly one host write.');
selected = 'selection changed after cut';
timers.shift()();
assert.deepEqual(messages, [{ type: 'copyText', text: '中文第一行\n第二行' }], 'Send the original selection snapshot once.');
for (selected of ['', null]) cut(event);
assert.equal(timers.length, 0, 'Empty/missing selection must not schedule a write.');
assert.equal(messages.length, 1);
const hostSources = readCSharpSources(path.resolve(__dirname, '../src/TuckPane'));
assert.match(hostSources, /Clipboard\.SetContentWithOptions\(\s*content,\s*new ClipboardContentOptions\s*\{\s*IsAllowedInHistory\s*=\s*true\s*\}\)/);
console.log('PASS note-cut-history: production handler snapshot, delayed single write, empty selection and default-cut preservation; full script syntax and host history option. Clipboard, undo and Win+V remain manual.');
