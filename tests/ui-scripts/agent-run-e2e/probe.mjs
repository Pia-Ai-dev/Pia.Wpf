// Reads AgentRuns / chats / artifacts out of a throwaway profile after an agent-run walkthrough.
//   node probe.mjs [root] [runs|msgs|files|all]     root defaults to %TEMP%\pia-e2e
import { DatabaseSync } from 'node:sqlite';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const ROOT = process.argv[2] || path.join(os.tmpdir(), 'pia-e2e');
const DB = path.join(ROOT, 'local', 'history.db');
const RUNS = path.join(ROOT, 'local', 'runs');
const FILES = path.join(ROOT, 'files');
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
