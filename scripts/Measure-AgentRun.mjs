// Decomposes one agent run from a Pia log into phases, LLM rounds and client-side throttle cost.
// Usage: node scripts/Measure-AgentRun.mjs <run-id-prefix> [log-path]
import fs from 'node:fs';
import path from 'node:path';

const [runId, logArg] = process.argv.slice(2);
if (!runId) {
  console.error('Usage: node scripts/Measure-AgentRun.mjs <run-id-prefix> [log-path]');
  process.exit(1);
}

const logPath = logArg ?? path.join(
  process.env.LOCALAPPDATA ?? '', 'Pia', 'Logs',
  `pia-${new Date().toISOString().slice(0, 10)}.log`);

if (!fs.existsSync(logPath)) {
  console.error(`No such log: ${logPath}`);
  process.exit(1);
}

const lines = fs.readFileSync(logPath, 'utf8').split(/\r?\n/).filter(l => l.includes(runId));
if (lines.length === 0) {
  console.error(`No lines for run ${runId} in ${path.basename(logPath)}`);
  process.exit(1);
}

const at = l => Date.parse(l.split('\t')[0]);
const starts = [], ends = [], phases = [];
let throttled = 0;

for (const l of lines) {
  if (l.includes('Throttling request')) throttled++;
  else if (l.includes('[RequestStart]')) starts.push(at(l));
  else if (l.includes('[RequestEnd]')) ends.push(at(l));

  const phase = /→ state (\w+)|→ (Completed|WaitingForInput)/.exec(l);
  if (phase) phases.push([at(l), phase[1] ?? phase[2]]);
}

const rounds = Math.min(starts.length, ends.length);
const modelWait = Array.from({ length: rounds }, (_, i) => ends[i] - starts[i]).reduce((a, b) => a + b, 0);
const span = at(lines.at(-1)) - at(lines[0]);
const s = ms => `${(ms / 1000).toFixed(1)}s`;

console.log(`run ${runId}  (${path.basename(logPath)})`);
console.log(`  wall clock     ${s(span)}`);
console.log(`  LLM rounds     ${rounds}`);
console.log(`  model wait     ${s(modelWait)}  (${(100 * modelWait / span).toFixed(0)}% of wall clock)`);
console.log(`  throttle cost  ${s(throttled * 500)}  (${throttled} rounds x 500ms RateLimitRetryHandler floor)`);

if (phases.length > 1) {
  console.log('  phases:');
  for (let i = 0; i < phases.length; i++) {
    const [t, name] = phases[i];
    const next = phases[i + 1]?.[0] ?? at(lines.at(-1));
    console.log(`    ${name.padEnd(16)} ${s(next - t)}`);
  }
}
