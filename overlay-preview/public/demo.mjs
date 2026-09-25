// Deliberately synthetic; these names and tiers are not CNGist catalog assertions.
export function demoSnapshot() {
  return {
    schema:'goldenlink.overlay/1', source:'demo', connected:true,
    catalog:{mapName:'棱光之间', mapNameEn:'Between the Prisms', campaign:'演示地图包', challenge:'FC', tier:'T3', verified:false},
    live:{room:'Room-3', holdingGolden:false, paused:false},
    cct:{roomIndex:3,roomCount:13,checkpointIndex:1,
      checkpoints:[{name:'初光',short:'DAWN',rooms:4},{name:'折射',short:'REFRACT',rooms:5},{name:'余晖',short:'AFTERGLOW',rooms:4}],
      streak:1,bestStreak:4,successRate:51.61,successes:48,attempts:93,entryRate:69.92,
      sessionEntryRate:57.14,goldenDeaths:94,sessionGoldenDeaths:5,
      goldenPb:'Room-11',sessionGoldenPb:'Room-7',
      goldenPbRoomIndex:11,sessionGoldenPbRoomIndex:7,
      recent:[true,true,false,true,false,false,true,true,true,false,true,false,true,true,false,true,true,true,false,true]},
    area:{noGoldenBestDeaths:12,totalDeaths:2401}
  };
}
// Synthetic chart data in the goldenlink.insights/1 shape, seeded so screenshots are stable.
export function demoInsights() {
  let seed=7;const random=()=>(seed=(seed*16807)%2147483647)/2147483647;
  const cps=[{name:'初光',abbreviation:'DAWN',rooms:4},{name:'折射',abbreviation:'REFR',rooms:5},{name:'余晖',abbreviation:'GLOW',rooms:4}];
  const difficulty=[.02,.05,.03,.18,.04,.09,.22,.06,.05,.12,.08,.26,.1];
  const rooms=[];let n=0;
  cps.forEach((cp,ci)=>{for(let i=0;i<cp.rooms;i++){const d=difficulty[n++];rooms.push({number:n,key:`room-${n}`,name:`Room-${n}`,checkpoint:ci+1,d,
    successRate:Math.round((1-d)*100*(0.85+random()*0.15)*10)/10,timeMs:Math.round((20+d*400+random()*60)*60000),timeInRunsMs:Math.round((6+d*120)*60000)});}});
  // Simulate runs: each run dies in a room with that room's difficulty.
  const run=()=>{for(const r of rooms)if(random()<r.d)return r.number;return null;};
  const all=Array.from({length:420},run),session=Array.from({length:46},run);
  const count=(runs,i)=>runs.filter(x=>x===i).length,wins=runs=>runs.filter(x=>x===null).length;
  const win=rooms.length+1,total=all.concat(session),reached=(runs,i)=>runs.filter(x=>x===null||x>=i).length;
  const pb=runs=>runs.includes(null)?win:Math.max(0,...runs.filter(Boolean));
  const avg=runs=>runs.length?runs.reduce((s,x)=>s+(x??win),0)/runs.length:null;
  let chance=1;const chances=[...rooms].reverse().map(r=>chance*=r.successRate/100).reverse();
  const roomRows=rooms.map((r,i)=>({...r,goldenDeaths:count(total,r.number),goldenDeathsSession:count(session,r.number),
    reached:reached(total,r.number),reachedSession:reached(session,r.number),
    choke:reached(total,r.number)?count(total,r.number)/reached(total,r.number)*100:null,
    chokeSession:reached(session,r.number)?count(session,r.number)/reached(session,r.number)*100:null,
    successes:Math.round(r.successRate/5),attempts:20,goldenChance:chances[i]*100,streak:Math.floor(random()*6),d:undefined}));
  let first=1;
  const checkpoints=cps.map((cp,ci)=>{const rs=roomRows.filter(r=>r.checkpoint===ci+1),start=first;first+=cp.rooms;
    return {index:ci+1,name:cp.name,abbreviation:cp.abbreviation,firstRoom:start,rooms:cp.rooms,
      goldenDeaths:rs.reduce((s,r)=>s+r.goldenDeaths,0),goldenDeathsSession:rs.reduce((s,r)=>s+r.goldenDeathsSession,0),
      clearChance:rs.reduce((p,r)=>p*r.successRate/100,1)*100};});
  const sessions=Array.from({length:14},(_,i)=>{const runs=all.slice(i*30,i*30+30),prior=all.slice(0,i*30+30);
    return {started:new Date(Date.UTC(2026,8,10+i,12)).toISOString(),current:false,pb:pb(prior),sessionPb:pb(runs),
      averageDistance:avg(prior),averageDistanceSession:avg(runs),successRate:62+i*1.4+random()*3,runs:runs.length};});
  sessions.push({started:new Date(Date.UTC(2026,8,26,12)).toISOString(),current:true,pb:pb(total),sessionPb:pb(session),
    averageDistance:avg(total),averageDistanceSession:avg(session),successRate:83.2,runs:session.length});
  return {schema:'goldenlink.insights/1',available:true,mapName:'棱光之间',campaign:'演示地图包',challenge:'FC',window:20,
    roomCount:rooms.length,winRoom:win,
    totals:{runs:total.length,runsSession:session.length,wins:wins(total),winsSession:wins(session),
      goldenDeaths:total.length-wins(total),goldenDeathsSession:session.length-wins(session),pb:pb(total),sessionPb:pb(session),
      averageDistance:avg(total),averageDistanceSession:avg(session),goldenChance:chances[0]*100},
    rooms:roomRows,checkpoints,sessions,
    sessionRuns:session.map(x=>({distance:x??win,won:x===null,room:x?`Room-${x}`:null}))};
}
export function demoSettings() {
  return {settings:{version:'0.3.0',connectionEnabled:true,connectionStatus:'connected',diagnosticsEnabled:false,checkUpdates:true,
    updateDotInObs:true,overlayPort:32272,serviceBaseUrl:'https://gist.diving-fish.com',cctAvailable:true},
    update:{available:true,current:'0.3.0',latest:'0.3.1',downloadUrl:'https://aliyun-static.diving-fish.com/cngist/CNGoldenLink-0.3.1.zip',
      checkedAt:new Date().toISOString(),error:null,showInObs:true}};
}
