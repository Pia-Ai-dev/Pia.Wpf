import { DatabaseSync } from 'node:sqlite';
const DB = 'C:\\Users\\maltm\\AppData\\Local\\Temp\\pia-e2e-0826\\local\\history.db';
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
tick();
setInterval(tick, 15000);
