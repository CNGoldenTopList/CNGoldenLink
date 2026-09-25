import {test} from 'node:test';import assert from 'node:assert/strict';
import {normalizeInsights,normalizeSettings,rolling,connectionLabel} from '../public/insights.mjs';
import {demoInsights,demoSettings} from '../public/demo.mjs';
test('insights keep unknown numbers as null and reject foreign schemas',()=>{
  assert.throws(()=>normalizeInsights({}));
  const d=demoInsights();d.rooms[0].choke=NaN;d.token='secret';
  const n=normalizeInsights(d);
  assert.equal(n.rooms[0].choke,null);assert.equal(n.token,undefined);
  assert.equal(n.rooms.length,n.roomCount);assert.equal(n.winRoom,n.roomCount+1);
  assert.ok(n.sessions.at(-1).current);
});
test('demo funnel is monotonic and consistent with totals',()=>{
  const n=normalizeInsights(demoInsights());
  assert.equal(n.rooms[0].reached,n.totals.runs);
  for(let i=1;i<n.rooms.length;i++)assert.equal(n.rooms[i].reached,n.rooms[i-1].reached-n.rooms[i-1].goldenDeaths);
  assert.equal(n.rooms.at(-1).reached-n.rooms.at(-1).goldenDeaths,n.totals.wins);
});
test('unavailable insights carry the reason only',()=>{
  const n=normalizeInsights({schema:'goldenlink.insights/1',available:false,reason:'no_route'});
  assert.equal(n.available,false);assert.equal(n.reason,'no_route');assert.deepEqual(n.rooms,[]);
});
test('settings drop non-https update links',()=>{
  const raw=demoSettings();assert.equal(normalizeSettings(raw).update.downloadUrl,raw.update.downloadUrl);
  raw.update.downloadUrl='javascript:alert(1)';assert.equal(normalizeSettings(raw).update.downloadUrl,null);
  assert.equal(normalizeSettings({}).update,null);
});
test('rolling mean and connection labels',()=>{
  assert.deepEqual(rolling([2,4,6,8],2),[2,3,5,7]);
  assert.equal(connectionLabel('connected'),'已连接');
  assert.match(connectionLabel('connected_data_error:scope_too_large'),/scope_too_large/);
  assert.match(connectionLabel('server_error:x'),/连接错误/);
});
