import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import assert from 'node:assert/strict';

const [executable, sessionId] = process.argv.slice(2);
if (!executable || !sessionId) throw new Error('Usage: node scripts/tests/check-replay-mcp.mjs <ansight-executable> <session-id>');
const child = spawn(executable, ['mcp', 'serve'], { stdio: ['pipe', 'pipe', 'pipe'] });
let nextId = 0, errors = '';
const pending = new Map();
child.stderr.on('data', data => { errors = (errors + data).slice(-8000); });
child.on('error', error => { for (const item of pending.values()) item.reject(error); });
child.on('exit', code => { for (const item of pending.values()) item.reject(new Error(`MCP exited ${code}: ${errors}`)); });
const lines = createInterface({ input: child.stdout });
lines.on('line', line => {
  let result;
  try { result = JSON.parse(line); } catch { for (const item of pending.values()) item.reject(new Error(`Non-protocol stdout: ${line}`)); return; }
  const waiting = pending.get(result.id);
  if (!waiting) return;
  pending.delete(result.id);
  if (result.error) waiting.reject(new Error(JSON.stringify(result.error)));
  else waiting.resolve(result.result);
});
async function request(method, params = {}) {
  const id = ++nextId;
  let timer;
  try {
    return await new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}. ${errors}`)); }, 45000);
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  } finally { clearTimeout(timer); }
}
try {
  const hello = await request('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'ansight-release-check', version: '1' } });
  assert.equal(hello.serverInfo.name, 'ansight');
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const tools = (await request('tools/list')).tools;
  assert.equal(tools.length, 7);
  const openTool = tools.find(tool => tool.name === 'open_replay_in_browser');
  assert.deepEqual(openTool._meta.ui.visibility, ['app']);
  assert.equal(openTool.annotations.readOnlyHint, false);
  const replayTool = tools.find(tool => tool.name === 'show_replay');
  const resourceUri = replayTool._meta.ui.resourceUri;
  const resource = await request('resources/read', { uri: resourceUri });
  assert.ok(resource.contents[0].text.includes('touch-overlay'));
  assert.ok(resource.contents[0].text.includes('Replay timeline'));
  assert.ok(resource.contents[0].text.includes('Open full replay'));
  assert.ok(!resource.contents[0].text.includes('Show recorded touches'));
  assert.ok(!resource.contents[0].text.includes('Annotate screenshot'));
  const call = async (name, args) => {
    const result = await request('tools/call', { name, arguments: args });
    assert.equal(result.isError ?? false, false, JSON.stringify(result.content));
    return result;
  };
  const listed = await call('list_replays', { search: sessionId });
  assert.ok(listed.structuredContent.sessions.some(item => item.sessionId === sessionId));
  const result = await call('show_replay', { sessionId });
  assert.ok(result._meta.frames[0].dataUrl.startsWith('data:image/'));
  assert.equal(result.structuredContent.touchCount, result._meta.replay.touches.length);
  assert.ok(resourceUri.includes(result.structuredContent.viewerBuild));
  const full = new URL(result._meta.replay.fullReplayUrl);
  assert.equal(full.searchParams.get('sessionId'), sessionId);
  // Scrubbing requests more screenshots after show_replay. Exercise overlapping
  // requests and distant positions while the resident host owns the data directory.
  const frames = result._meta.replay.frames;
  const positions = [...new Set([0, Math.floor(frames.length / 2), Math.max(0, frames.length - 4)])];
  const batches = await Promise.all(positions.map(async position => {
    const frameIds = frames.slice(position, position + 4).map(frame => frame.frameId);
    const batch = await call('get_replay_frames', { sessionId, frameIds });
    assert.deepEqual(batch._meta.frames.map(frame => frame.frameId), frameIds);
    assert.ok(batch._meta.frames.every(frame => /^data:image\/(webp|png|jpeg);base64,/.test(frame.dataUrl)));
    return frameIds.length;
  }));
  const refreshed = await call('refresh_replay_annotations', { sessionId });
  assert.equal(refreshed.structuredContent.annotationCount, refreshed._meta.annotations.length);
  const marker = result.structuredContent.markers.at(-1);
  if (marker) {
    const read = await call('read_replay_annotation', { sessionId, annotationId: marker.id });
    assert.equal(read.structuredContent.annotation.annotationId, marker.id);
    assert.ok(read.content.some(item => item.type === 'image' && item.data.length > 0));
  }
  console.log(JSON.stringify({ verified: true, server: hello.serverInfo, sessionId,
    frames: result.structuredContent.frameCount, scrubbedFrames: batches.reduce((sum, count) => sum + count, 0), touches: result.structuredContent.touchCount,
    annotations: result.structuredContent.markers.length, resourceUri }, null, 2));
} finally {
  child.stdin.end();
  lines.close();
  const force = setTimeout(() => child.kill('SIGTERM'), 3000);
  force.unref();
}
