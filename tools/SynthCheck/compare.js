// Checks that the line preview's sounds are the mod's: runs src/SoundSynth.cs through SynthCheck, makes the same
// sounds with the generator code in tools/line-preview-widget.html, and compares them.
// Usage, from this folder: node compare.js
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const out = path.join(__dirname, 'out');
fs.mkdirSync(out, { recursive: true });
execSync(`dotnet run -c Release -- "${out}"`, { cwd: __dirname, stdio: 'inherit' });
const cs = JSON.parse(fs.readFileSync(path.join(out, 'csharp.json'), 'utf8'));

const widget = fs.readFileSync(path.join(__dirname, '..', 'line-preview-widget.html'), 'utf8');
const start = widget.indexOf('const SR=44100,');
const end = widget.indexOf('const SOUNDS=[');
const GEN = new Function(widget.slice(start, end) + '\nreturn GEN;')();

// The sounds the mod still makes (v0.34 kept only the ones William used), by their place in the preview's lists.
// The select click is a recording, which the preview decodes from the image ranker's own file instead of making.
const kept = [['catch', 0], ['whoosh', 2]];
console.log('select6  recording, not compared');
let worst = 0;
let failed = false;
for (const [kind, v] of kept) {
  const js = GEN[kind](v);
  const c = cs[kind + v];
  let diff = 0;
  for (let j = 0; j < c.s.length; j++) diff = Math.max(diff, Math.abs(js[j * 100] - c.s[j]));
  worst = Math.max(worst, diff);
  const bad = js.length !== c.n || diff > 0.01;
  failed = failed || bad;
  console.log(`${(kind + v).padEnd(8)} ${bad ? 'DIFFERENT' : 'same'}  length ${js.length}/${c.n}, largest difference ${diff.toExponential(1)}`);
}
console.log(`Worst difference: ${worst.toExponential(2)}. ${failed ? 'The preview and the mod differ.' : 'The preview matches the mod.'}`);
process.exit(failed ? 1 : 0);
