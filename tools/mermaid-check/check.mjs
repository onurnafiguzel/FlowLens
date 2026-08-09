// Parses every ```mermaid block in the generated docs with the same library GitHub renders them
// with. Syntax only - it says nothing about whether a diagram is READABLE, which is why three of
// them are also inspected by eye (docs/phase-5-notes.md).
//
// Exit 0 = all parsed. Exit 1 = at least one failed, with file, block index and the parser's
// message. Exit 2 = could not run at all, which must never be mistaken for success.
import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';

const root = process.argv[2];

if (!root) {
  console.error('usage: node check.mjs <docs-directory>');
  process.exit(2);
}

let mermaid;

try {
  // Mermaid targets the browser and pulls in DOMPurify, which binds to `window` the moment it is
  // imported - so even parse() fails under bare Node with "DOMPurify.addHook is not a function".
  // A jsdom window installed BEFORE the dynamic import is what makes the same library GitHub uses
  // runnable here at all.
  const { JSDOM } = await import('jsdom');
  const dom = new JSDOM('<!DOCTYPE html><body></body>');

  globalThis.window = dom.window;
  globalThis.document = dom.window.document;
  globalThis.navigator ??= dom.window.navigator;

  ({ default: mermaid } = await import('mermaid'));
} catch (error) {
  console.error(`could not load the mermaid package: ${error.message}`);
  console.error('run: npm ci   (in tools/mermaid-check)');
  process.exit(2);
}

async function* markdownFiles(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) yield* markdownFiles(path);
    else if (entry.name.endsWith('.md')) yield path;
  }
}

const blockPattern = /```mermaid\n([\s\S]*?)```/g;
let blocks = 0;
let failures = 0;

for await (const file of markdownFiles(root)) {
  const text = await readFile(file, 'utf8');
  let match;
  let index = 0;

  while ((match = blockPattern.exec(text)) !== null) {
    blocks++;
    index++;

    try {
      await mermaid.parse(match[1]);
    } catch (error) {
      failures++;
      console.error(`FAIL ${relative(root, file)} [block ${index}]: ${error.message}`);
    }
  }
}

if (blocks === 0) {
  // No diagrams at all means the generator changed shape or the path is wrong. Reporting "all
  // passed" here would be the silent-empty-result failure this project treats as the worst kind.
  console.error(`no mermaid blocks found under ${root} - nothing was verified`);
  process.exit(2);
}

console.log(`${blocks - failures}/${blocks} mermaid blocks parsed`);
process.exit(failures === 0 ? 0 : 1);
