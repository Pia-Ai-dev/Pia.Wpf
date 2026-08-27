// Polls a throwaway profile's history.db and prints every agent-run state transition as it happens.
//   node watch.mjs [root]     root defaults to %TEMP%\pia-e2e
import { DatabaseSync } from 'node:sqlite';
import os from 'node:os';
import path from 'node:path';

const ROOT = process.argv[2] || path.join(os.tmpdir(), 'pia-e2e');
const DB = path.join(ROOT, 'local', 'history.db');
const NAMES = ['Planning', 'Running', 'Verifying', 'WaitingForInput', 'Paused',
  'Completed', 'Failed', 'Cancelled', 'WaitingForChildren'];
const seen = new Map();
const tick = () => {
  let rows;
  try {
    const db = new DatabaseSync(DB, { readOnly: true });
    rows = db.prepare(`SELECT r.Id, r.State, r.ParentRunId, c.Title
      FROM AgentRuns r LEFT JOIN AssistantChats c ON c.Id = r.ChatId ORDER BY r.CreatedAt`).all();
    db.close();
  } catch { return; }
  for (const r of rows) {
    const key = r.Id;
    if (seen.get(key) === r.State) continue;
    seen.set(key, r.State);
    const kind = r.ParentRunId ? 'child' : 'RUN  ';
    console.log(`${kind} ${String(r.Id).slice(0, 8)} -> ${NAMES[r.State] ?? r.State}  "${String(r.Title ?? '').slice(0, 55)}"`);
  }
};
console.log('watching ' + DB);
tick();
setInterval(tick, 15000);
