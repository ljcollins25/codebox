const {test}=require('node:test'),assert=require('node:assert/strict'),{spawnSync}=require('node:child_process');
const {deadlineUtc}=require('./deadline-utc.cjs');
test('zone-less historical deadline is UTC, not local',()=>assert.equal(deadlineUtc('2026-10-13T02:25:05.0000000'),Date.parse('2026-10-13T02:25:05Z')));
test('explicit Z and offsets preserve the instant',()=>{
 assert.equal(deadlineUtc('2026-10-13T02:25:05Z'),deadlineUtc('2026-10-12T19:25:05-07:00'));
 assert.equal(deadlineUtc('2026-10-13T02:25:05Z')-Date.parse('2026-10-13T02:21:05Z'),240000);
});
test('invalid dates fail closed',()=>assert.throws(()=>deadlineUtc('garbage')));
test('America/Los_Angeles timer delay is 240000ms',()=>{
 const r=spawnSync(process.execPath,['-e',"const {deadlineUtc}=require('./deadline-utc.cjs'); console.log(deadlineUtc('2026-10-13T02:25:05.0000000')-Date.parse('2026-10-13T02:21:05Z'));"],
 {cwd:__dirname,env:{...process.env,TZ:'America/Los_Angeles'},encoding:'utf8'});
 assert.equal(r.status,0);assert.equal(r.stdout.trim(),'240000');
});
