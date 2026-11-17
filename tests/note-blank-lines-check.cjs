'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function extractFunction(script, name) {
  const header = new RegExp(`(?:async\\s+)?function\\s+${name}\\s*\\(`).exec(script);
  assert.ok(header, `The production ${name} function was not found.`);
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

const htmlPath = path.resolve(__dirname, '../src/TuckPane/Assets/NoteEditor.html');
const html = fs.readFileSync(htmlPath, 'utf8');
const inlineScript = html.match(/<script\b[^>]*>([\s\S]*?)<\/script>/i)?.[1];
assert.ok(inlineScript, 'NoteEditor.html must contain its inline script.');
// Compilation only: no document/window, browser, selection or input automation.
new vm.Script(inlineScript, { filename: htmlPath });

const functionSource = extractFunction(inlineScript, 'blankLinesToAppend');
const append = vm.runInNewContext(`(${functionSource})`, {}, { timeout: 1000 });
const cases = [
  ['implicit first line', 0, 0, 25.2, 0],
  ['just above next line', 25.19, 0, 25.2, 0],
  ['next line boundary', 25.2, 0, 25.2, 1],
  ['just below next line', 25.21, 0, 25.2, 1],
  ['existing trailing empty rows', 4 * 25.2, 3 * 25.2, 25.2, 1],
  ['scrolled content coordinate', 5 * 25.2 + 3, 2 * 25.2, 25.2, 3],
  ['fractional line height', 3 * 23.75, 23.75, 23.75, 2],
  ['last line below a non-grid-aligned image', 165, 115, 25, 2],
  ['inside existing last line', 15, 25.2, 25.2, 0]
];
for (const [name, contentY, lastLineTop, lineHeight, expected] of cases)
  assert.equal(append(contentY, lastLineTop, lineHeight), expected, name);

console.log(`PASS note-blank-lines: ${cases.length} production pure-function cases and complete inline-script syntax. DOM measurement, caret, IME, images and undo remain manual acceptance.`);
