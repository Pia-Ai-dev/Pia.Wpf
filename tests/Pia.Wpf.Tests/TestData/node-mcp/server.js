'use strict';
const fs = require('fs');
const readline = require('readline');

function isEntryPoint() {
  try { return fs.realpathSync(process.argv[1]) === fs.realpathSync(__filename); } catch { return false; }
}
if (!isEntryPoint()) process.exit(0);

const send = (message) => process.stdout.write(JSON.stringify(message) + '\n');
readline.createInterface({ input: process.stdin }).on('line', (line) => {
  let request;
  try { request = JSON.parse(line); } catch { return; }
  if (request.id === undefined) return;
  const reply = (result) => send({ jsonrpc: '2.0', id: request.id, result });
  switch (request.method) {
    case 'initialize':
      return reply({
        protocolVersion: (request.params && request.params.protocolVersion) || '2025-06-18',
        capabilities: { tools: {} },
        serverInfo: { name: 'pia-node-fixture', version: '1.0.0' },
      });
    case 'tools/list':
      return reply({ tools: [{ name: 'echo', description: 'Returns the text it gets.',
        inputSchema: { type: 'object', properties: { text: { type: 'string' } } } }] });
    case 'tools/call':
      return reply({ content: [{ type: 'text',
        text: String((request.params && request.params.arguments && request.params.arguments.text) || '') }] });
    case 'ping':
      return reply({});
    default:
      return send({ jsonrpc: '2.0', id: request.id, error: { code: -32601, message: 'Method not found' } });
  }
});
