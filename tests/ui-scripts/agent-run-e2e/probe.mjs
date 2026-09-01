// Reads AgentRuns / chats / artifacts out of a throwaway profile after an agent-run walkthrough.
//   node probe.mjs [root] [runs|msgs|files|exchanges|vault|park|all]   root defaults to %TEMP%\pia-e2e
import { DatabaseSync } from 'node:sqlite';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const ROOT = process.argv[2] || path.join(os.tmpdir(), 'pia-e2e');
const DB = path.join(ROOT, 'local', 'history.db');
const RUNS = path.join(ROOT, 'local', 'runs');
const FILES = path.join(ROOT, 'files');
// The redirect that makes a vault-writing e2e safe: the vault follows assistantFilesFolder, not %LOCALAPPDATA%.
const VAULT = path.join(FILES, 'Vault');
const mode = process.argv[3] || 'runs';

if (!fs.existsSync(DB)) {
  console.error(`no history.db at ${DB} — wrong root, or the app never launched against it`);
  process.exit(2);
}

const db = new DatabaseSync(DB, { readOnly: true });

const chats = new Map(db.prepare(
  'SELECT Id, Title, WorkingDirectory FROM AssistantChats').all().map(r => [r.Id, r]));

const runs = db.prepare(`SELECT Id, ChatId, RunShape, State, TriggerKind, ParentRunId, Goal,
  CreatedAt, StartedAt, CompletedAt, FailureJson, LedgerJson, PolicyJson
  FROM AgentRuns ORDER BY CreatedAt`).all();

const stamp = (s) => s ? String(s).replace('T', ' ').slice(11, 19) : '—';

if (mode === 'runs' || mode === 'all') {
  console.log('=== AgentRuns (' + runs.length + ') ===');
  for (const r of runs) {
    const chat = chats.get(r.ChatId);
    let steps = [];
    try {
      const led = r.LedgerJson ? JSON.parse(r.LedgerJson) : null;
      steps = led?.Steps ?? led?.steps ?? [];
    } catch { /* ignore */ }
    const done = steps.filter(s => /complete|done|succeed/i.test(s.State ?? s.state ?? '')).length;
    console.log(
      `\n  run ${String(r.Id).slice(0, 8)}  shape=${r.RunShape} state=${r.State} trigger=${r.TriggerKind}` +
      (r.ParentRunId ? ` parent=${String(r.ParentRunId).slice(0, 8)}` : ''));
    console.log(`    chat "${chat?.Title ?? '?'}"  workdir="${chat?.WorkingDirectory ?? ''}"`);
    console.log(`    created ${stamp(r.CreatedAt)}  started ${stamp(r.StartedAt)}  completed ${stamp(r.CompletedAt)}`);
    console.log(`    steps ${steps.length} (${done} done)`);
    for (const s of steps) {
      console.log(`      - [${s.State ?? s.state}] ${String(s.Title ?? s.title ?? '').slice(0, 78)}`);
    }
    if (r.FailureJson) console.log('    FAILURE ' + String(r.FailureJson).slice(0, 300));
    const wsMeta = path.join(RUNS, r.Id + '.workspace.json');
    const wsDir = path.join(RUNS, r.Id);
    console.log(`    workspace meta=${fs.existsSync(wsMeta) ? 'YES' : 'no'} dir=${fs.existsSync(wsDir) ? 'YES' : 'no'}`);
    if (fs.existsSync(wsDir)) {
      const list = [];
      const w = (d, rel) => {
        for (const e of fs.readdirSync(d, { withFileTypes: true })) {
          if (e.isDirectory()) w(path.join(d, e.name), rel + e.name + '/');
          else list.push(rel + e.name + ' (' + fs.statSync(path.join(d, e.name)).size + 'b)');
        }
      };
      w(wsDir, '');
      console.log('      ws: ' + (list.join(' | ') || '(empty)'));
    }
  }
}

const KIND = ['?', 'Call', 'Result', 'ParkedCall', 'WithheldCall'];
// The approval-park evidence: what the model saw (Kind 1/2, tokenized) beside what the gate saw
// (Kind 3/4, detokenized and replayable). Payload-bearing, so only lengths and heads are printed.
if (mode === 'exchanges' || mode === 'all') {
  console.log('\n=== AgentToolExchanges ===');
  let rows = [];
  try {
    rows = db.prepare(`SELECT RunId, StepId, MessageSeq, Seq, Round, Role, Kind, CallId, ToolName,
      ArgumentsJson, ArgsOmitted, DisplayArgs, ResultKind, ResultText, Chars, AnchorMessageId,
      CreatedAt, ReplayedAt, SupersededAt FROM AgentToolExchanges ORDER BY RunId, Seq`).all();
  } catch (ex) {
    console.log('  no AgentToolExchanges table (' + ex.message + ')');
  }
  if (rows.length === 0) console.log('  (none)');
  let currentRun = null;
  for (const r of rows) {
    if (r.RunId !== currentRun) {
      currentRun = r.RunId;
      console.log('\n  run ' + String(r.RunId).slice(0, 8));
    }
    const flags = [
      r.ReplayedAt ? 'REPLAYED ' + stamp(r.ReplayedAt) : null,
      r.SupersededAt ? 'superseded' : null,
      r.ArgsOmitted ? 'args-omitted' : null,
      r.AnchorMessageId ? 'anchored' : 'unanchored',
    ].filter(Boolean).join(' ');
    const args = String(r.ArgumentsJson ?? '');
    console.log(`    seq ${String(r.Seq).padStart(3)} msg ${r.MessageSeq} round ${r.Round ?? '-'}` +
      `  ${(KIND[r.Kind] ?? r.Kind).padEnd(12)} ${String(r.ToolName ?? '-').padEnd(16)}` +
      ` args=${args.length}b display=${String(r.DisplayArgs ?? '').length}b` +
      ` result=${String(r.ResultText ?? '').length}b  ${flags}`);
    if (args.length > 0) console.log('        args: ' + args.replace(/\s+/g, ' ').slice(0, 160));
  }
}

if (mode === 'vault' || mode === 'all') {
  console.log('\n=== vault (' + VAULT + ') ===');
  if (!fs.existsSync(VAULT)) console.log('  ABSENT — the run wrote no vault, or the redirect failed');
  else {
    const walk = (dir, depth = 0) => {
      for (const e of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
        const q = path.join(dir, e.name);
        if (e.isDirectory()) { console.log('  '.repeat(depth + 1) + e.name + '/'); walk(q, depth + 1); }
        else console.log('  '.repeat(depth + 1) + e.name + '  (' + fs.statSync(q).size + 'b)');
      }
    };
    walk(VAULT);
  }
}

// Issue 1's discriminator is the ROUND COUNT between the park and WaitingForInput, not the wall clock:
// pre-fix the advisory string sometimes stopped the model on its own, so a small delta alone proves nothing.
if (mode === 'park' || mode === 'all') {
  console.log('\n=== park latency (from the log) ===');
  const logDir = path.join(ROOT, 'local', 'Logs');
  if (!fs.existsSync(logDir)) { console.log('  no Logs directory at ' + logDir); }
  else {
    const lines = fs.readdirSync(logDir).filter((f) => f.endsWith('.log')).sort()
      .flatMap((f) => fs.readFileSync(path.join(logDir, f), 'utf8').split(/\r?\n/));
    const at = (line) => {
      const m = line.match(/(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}[.,]\d+)/);
      return m ? Date.parse(m[1].replace(' ', 'T').replace(',', '.')) : NaN;
    };
    let park = null, rounds = 0;
    for (const line of lines) {
      if (line.includes('parked') && line.includes('for human approval')) { park = line; rounds = 0; continue; }
      if (park && /Round \d+: \d+ tool call\(s\) detected/.test(line)) { rounds++; continue; }
      if (park && line.includes('WaitingForInput (paused)')) {
        const ms = at(line) - at(park);
        const verdict = rounds === 0 ? 'PASS' : 'FAIL';
        console.log(`  park -> WaitingForInput: ${Number.isFinite(ms) ? ms + 'ms' : '?'}` +
          `  rounds in between = ${rounds}  ${verdict}`);
        console.log('    ' + park.trim().slice(0, 150));
        console.log('    ' + line.trim().slice(0, 150));
        park = null;
      }
    }
    if (park) console.log('  a park never reached WaitingForInput: ' + park.trim().slice(0, 150));
    for (const marker of ['re-seeded', 'replaying', 'parked/withheld call(s)', 'parked step for approval']) {
      const hits = lines.filter((l) => l.includes(marker));
      console.log(`  "${marker}": ${hits.length} line(s)`);
      for (const h of hits.slice(0, 6)) console.log('    ' + h.trim().slice(0, 170));
    }
  }
}

if (mode === 'msgs' || mode === 'all') {
  console.log('\n=== last assistant message per chat ===');
  for (const [id, c] of chats) {
    const m = db.prepare(`SELECT Role, Content FROM AssistantChatMessages
      WHERE ChatId = ? ORDER BY Ordinal DESC LIMIT 1`).get(id);
    if (!m) continue;
    console.log(`\n  "${c.Title}" [${c.WorkingDirectory}] (${m.Role})`);
    console.log('    ' + String(m.Content ?? '').replace(/\s+/g, ' ').slice(0, 700));
  }
}

if (mode === 'files' || mode === 'all') {
  console.log('\n=== files tree ===');
  const walk = (dir, depth = 0) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { console.log('  '.repeat(depth + 1) + e.name + '/'); walk(p, depth + 1); }
      else console.log('  '.repeat(depth + 1) + e.name + '  (' + fs.statSync(p).size + 'b)');
    }
  };
  walk(FILES);
}
