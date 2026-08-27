import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const ROOT = process.argv[2];
const REAL_ROAMING = 'C:\\Users\\maltm\\AppData\\Roaming\\Pia';

const roaming = path.join(ROOT, 'roaming');
const local = path.join(ROOT, 'local');
const files = path.join(ROOT, 'files');
for (const d of [roaming, local, files]) fs.mkdirSync(d, { recursive: true });

// --- profile copy (settings + providers + templates only) ---
for (const f of ['settings.json', 'providers.json', 'templates.json']) {
  const src = path.join(REAL_ROAMING, f);
  if (fs.existsSync(src)) fs.copyFileSync(src, path.join(roaming, f));
}

const sPath = path.join(roaming, 'settings.json');
const s = JSON.parse(fs.readFileSync(sPath, 'utf8').replace(/^\uFEFF/, ''));
Object.assign(s, {
  syncEnabled: false,
  autoIngestSources: false,
  defaultWindowMode: 1,          // Assistant
  lastActiveView: null,
  startMinimized: false,
  launchAtStartup: false,
  autoUpdateEnabled: false,
  assistantFilesFolder: files,
  assistantDefaultWorkingDirectory: 'Playground',
  assistantAgentModeDefault: true,
  assistantBackgroundRunConfirmSuppressed: true,
  agentRunAutoApproveBuiltInWrites: true,
  chatAutoTitleEnabled: true,
  windowWidth: 1400,
  windowHeight: 1000,
  windowLeft: 60,
  windowTop: 40,
});
fs.writeFileSync(sPath, JSON.stringify(s, null, 2), 'utf8');

// --- fixtures ---
const W = (rel, body) => {
  const p = path.join(files, rel);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, body, 'utf8');
};
fs.mkdirSync(path.join(files, 'Playground'), { recursive: true });

/* ============ Inventory: 12 products, 5 below reorder point, 1 discontinued ============ */
W('Inventory/inventory.csv', [
  'sku,name,on_hand,reorder_point,unit_cost',
  'SKU-1001,Blue Widget,4,10,3.50',        // below  -> reorder
  'SKU-1002,Red Widget,48,10,3.50',
  'SKU-1003,Green Gasket,2,12,1.20',       // below  -> reorder
  'SKU-1004,Steel Bracket,30,15,7.80',
  'SKU-1005,Copper Coil,9,20,12.40',       // below  -> reorder
  'SKU-1006,Nylon Strap,55,25,0.90',
  'SKU-1007,Brass Fitting,7,7,4.10',       // equal, not below
  'SKU-1008,Rubber Seal,0,8,0.65',         // below  -> DISCONTINUED, exclude
  'SKU-1009,Alu Panel,140,40,22.00',
  'SKU-1010,Glass Cover,19,6,9.30',
  'SKU-1011,Zinc Screw,3,30,0.15',         // below  -> reorder
  'SKU-1012,Teflon Tape,26,10,1.75',
].join('\n') + '\n');

W('Inventory/reorder-policy.md', `# Reorder policy

A product needs reordering when \`on_hand\` is **strictly less than** \`reorder_point\`.
A product whose \`on_hand\` equals its \`reorder_point\` does **not** need reordering.

The order quantity is \`(reorder_point * 3) - on_hand\`.

Products listed in a discontinued marker file are never reordered, whatever their stock.
`);

W('Inventory/discontinued.txt', `These SKUs are DISCONTINUED and must never be reordered:

SKU-1008  Rubber Seal   (supplier exited the market 2026-03)
`);

/* ============ ReleaseNotes: 6 fragments, 2 without a ticket id ============ */
W('ReleaseNotes/VERSION.txt', '2.4.7\n');
W('ReleaseNotes/fragments/0001-agent-panel.md',
  '[PIA-401] Added: the run panel now shows a per-step timeline toggle.\n');
W('ReleaseNotes/fragments/0002-timeout.md',
  '[PIA-388] Fixed: a slow provider no longer aborts the step at 100 seconds.\n');
W('ReleaseNotes/fragments/0003-workdir.md',
  '[PIA-412] Added: a new folder can be created straight from the working-directory picker.\n');
W('ReleaseNotes/fragments/0004-scroll.md',
  'Changed: the chat scroller keeps its position when a reply streams in.\n');   // no ticket
W('ReleaseNotes/fragments/0005-e2ee.md',
  '[PIA-395] Fixed: a recovery-key restore no longer blanks synced provider rows.\n');
W('ReleaseNotes/fragments/0006-icons.md',
  'Changed: sidebar icons use the outline weight at rest.\n');                    // no ticket

/* ============ Support: 9 tickets -> 3 billing / 4 bug / 2 howto, 2 URGENT ============ */
const ticket = (id, cat, subj, body) =>
  W(`Support/tickets/${id}.txt`,
    `Ticket: ${id}\n${cat ? `Category: ${cat}\n` : ''}Subject: ${subj}\n\n${body}\n`);
ticket('T-2001', 'billing', 'Charged twice for August',
  'My card shows two identical charges on the 3rd. Please refund one.');
ticket('T-2002', 'bug', 'Crash when opening the vault',
  'URGENT - the app closes with no message every time I click Memory.');
ticket('T-2003', 'howto', 'How do I change the working folder?',
  'I cannot find where the assistant writes its files.');
ticket('T-2004', 'billing', 'Invoice address is wrong',
  'The company name on invoice 8841 is misspelled.');
ticket('T-2005', 'bug', 'Reminders fire an hour late',
  'Every reminder arrives 60 minutes after the time I set.');
ticket('T-2006', null, 'Cannot log in after password reset',
  'I reset my password and now the login button does nothing. This is a defect, not a question.');
ticket('T-2007', 'bug', 'Export produces an empty zip',
  'URGENT - diagnostics export writes a zip with zero-byte entries.');
ticket('T-2008', null, 'Where are the logs kept?',
  'Support asked me for a log file. Which folder should I look in? Just tell me how to find it.');
ticket('T-2009', 'billing', 'Cancel the annual plan',
  'Please cancel my renewal before the next cycle.');

/* ============ Finance: 6 over-budget categories ============ */
W('Finance/budget.md', `# Annual budget by category (EUR, first half)

| category | budget |
|---|---|
| travel | 4000 |
| software | 6000 |
| hardware | 3000 |
| marketing | 5000 |
| training | 2000 |
| catering | 800 |
| legal | 2500 |
| office | 1200 |

Spend is the sum of the matching category rows across every quarter file in this folder.
`);
W('Finance/expenses-q1.csv', [
  'date,category,amount,note',
  '2026-01-14,travel,1200.00,Berlin offsite',
  '2026-01-22,software,2100.00,IDE licences',
  '2026-02-03,hardware,900.00,two monitors',
  '2026-02-11,marketing,3100.00,conference booth',
  '2026-02-19,training,450.00,workshop',
  '2026-03-02,catering,610.00,team lunch',
  '2026-03-08,legal,700.00,contract review',
  '2026-03-15,office,300.00,chairs',
  '2026-03-27,travel,1500.00,client visit PENDING',
].join('\n') + '\n');
W('Finance/expenses-q2.csv', [
  'date,category,amount,note',
  '2026-04-04,travel,1900.00,Munich summit',
  '2026-04-17,software,4600.00,seat expansion',
  '2026-05-02,hardware,2600.00,laptop refresh',
  '2026-05-13,marketing,2600.00,paid ads',
  '2026-05-21,training,1900.00,certification',
  '2026-06-01,catering,450.00,offsite dinner PENDING',
  '2026-06-09,legal,1100.00,trademark',
  '2026-06-20,office,250.00,stationery',
].join('\n') + '\n');
// travel 4600>4000, software 6700>6000, hardware 3500>3000, training 2350>2000,
// catering 1060>800, legal 1800<2500, marketing 5700>5000, office 550<1200  => 6 over

/* ============ Docs: 7 relative links, 3 broken ============ */
W('Docs/index.md', `# Handbook

Start with [setup](setup.md), then read [the tool guide](tools.md).
A deeper dive lives in [architecture](architecture.md).
`);   // architecture.md missing -> broken
W('Docs/setup.md', `# Setup

Install, then continue to [tools](tools.md).
If something breaks, see [troubleshooting](troubleshooting.md).
`);   // troubleshooting.md missing -> broken
W('Docs/tools.md', `# Tools

Back to [the index](index.md).
`);
W('Docs/glossary.md', `# Glossary

Terms used across [the handbook](handbook.md).
`);   // handbook.md missing -> broken
W('Docs/faq.md', `# FAQ

See [setup](setup.md) for installation questions.
`);
// links: index->setup(ok), index->tools(ok), index->architecture(BROKEN),
//        setup->tools(ok), setup->troubleshooting(BROKEN), tools->index(ok),
//        glossary->handbook(BROKEN), faq->setup(ok)  = 8 links, 3 broken

/* ============ Config: drift ============ */
W('Config/baseline.env', [
  '# reference configuration',
  'APP_NAME=pia',
  'LOG_LEVEL=info',
  'MAX_RETRIES=3',
  'TIMEOUT_SECONDS=30',
  'FEATURE_SYNC=on',
  'REGION=eu-central',
].join('\n') + '\n');
W('Config/prod.env', [
  'APP_NAME=pia',
  'LOG_LEVEL=warn',
  'MAX_RETRIES=5',
  'TIMEOUT_SECONDS=30',
  'FEATURE_SYNC=on',
  'REGION=eu-central',
  'SENTRY_DSN=https://example.invalid/1',
].join('\n') + '\n');
W('Config/staging.env', [
  '# TODO: align with baseline before the next release',
  'APP_NAME=pia-staging',
  'LOG_LEVEL=debug',
  'MAX_RETRIES=3',
  'FEATURE_SYNC=on',
  '# TODO: REGION is unset on purpose for now',
].join('\n') + '\n');

// --- baseline hashes of the REAL profile, to prove it is untouched ---
const hash = (p) => fs.existsSync(p)
  ? crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')
  : 'MISSING';
const guard = {
  'roaming/settings.json': hash(path.join(REAL_ROAMING, 'settings.json')),
  'roaming/providers.json': hash(path.join(REAL_ROAMING, 'providers.json')),
  'roaming/templates.json': hash(path.join(REAL_ROAMING, 'templates.json')),
  'local/history.db': hash('C:\\Users\\maltm\\AppData\\Local\\Pia\\history.db'),
};
fs.writeFileSync(path.join(ROOT, 'real-profile-baseline.json'), JSON.stringify(guard, null, 2));

console.log('ROOT      ' + ROOT);
console.log('roaming   ' + roaming);
console.log('local     ' + local);
console.log('files     ' + files);
console.log('folders   ' + fs.readdirSync(files).join(', '));
