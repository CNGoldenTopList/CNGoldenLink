// Whitelist adapters for the control hub. Missing numbers stay null; charts show gaps rather than zeros.
const number=v=>typeof v==='number'&&Number.isFinite(v)?v:null;
const text=v=>typeof v==='string'?v:null;
const list=(v,max,map)=>Array.isArray(v)?v.slice(-max).map(map):[];

export function normalizeInsights(raw){
  if(raw?.schema!=='goldenlink.insights/1')throw new Error('Unsupported insights data');
  if(raw.available!==true)return {available:false,reason:text(raw.reason),rooms:[],checkpoints:[],sessionRuns:[],sessions:[]};
  const t=raw.totals??{};
  return {
    available:true,mapName:text(raw.mapName),campaign:text(raw.campaign),challenge:text(raw.challenge),
    window:number(raw.window)??20,roomCount:number(raw.roomCount)??0,winRoom:number(raw.winRoom)??1,
    totals:Object.fromEntries(['runs','runsSession','wins','winsSession','goldenDeaths','goldenDeathsSession','pb','sessionPb',
      'averageDistance','averageDistanceSession','goldenChance'].map(k=>[k,number(t[k])])),
    rooms:list(raw.rooms,2000,r=>({number:number(r.number),key:text(r.key),name:text(r.name)??text(r.key)??'?',checkpoint:number(r.checkpoint),
      ...Object.fromEntries(['goldenDeaths','goldenDeathsSession','reached','reachedSession','choke','chokeSession','successes','attempts',
        'successRate','goldenChance','streak','timeMs','timeInRunsMs'].map(k=>[k,number(r[k])]))})),
    checkpoints:list(raw.checkpoints,2000,c=>({index:number(c.index),name:text(c.name)??'CP',abbreviation:text(c.abbreviation),
      firstRoom:number(c.firstRoom),rooms:number(c.rooms),goldenDeaths:number(c.goldenDeaths),goldenDeathsSession:number(c.goldenDeathsSession),
      clearChance:number(c.clearChance)})),
    sessionRuns:list(raw.sessionRuns,2000,r=>({distance:number(r.distance)??0,won:r.won===true,room:text(r.room)})),
    sessions:list(raw.sessions,500,s=>({started:text(s.started),current:s.current===true,pb:number(s.pb),sessionPb:number(s.sessionPb),
      averageDistance:number(s.averageDistance),averageDistanceSession:number(s.averageDistanceSession),successRate:number(s.successRate),
      runs:number(s.runs)}))
  };
}

export function normalizeSettings(raw){
  const s=raw?.settings??{},u=raw?.update;
  const bool=v=>typeof v==='boolean'?v:null;
  return {
    values:{version:text(s.version),connectionEnabled:bool(s.connectionEnabled),connectionStatus:text(s.connectionStatus),
      diagnosticsEnabled:bool(s.diagnosticsEnabled),checkUpdates:bool(s.checkUpdates),updateDotInObs:bool(s.updateDotInObs),
      backgroundOpacity:number(s.backgroundOpacity)==null?null:Math.min(100,Math.max(0,Math.round(s.backgroundOpacity))),
      overlayPort:number(s.overlayPort),serviceBaseUrl:text(s.serviceBaseUrl),cctAvailable:bool(s.cctAvailable)},
    // Only follow https links; the mod already restricts hosts, this guards the preview and future sources.
    update:u?{available:u.available===true,current:text(u.current),latest:text(u.latest),
      downloadUrl:/^https:\/\//.test(text(u.downloadUrl)??'')?u.downloadUrl:null,checkedAt:text(u.checkedAt),error:text(u.error)}:null
  };
}

/** Trailing mean over up to `size` previous values; the first points average what exists so far. */
export function rolling(values,size){
  let sum=0;return values.map((v,i)=>{sum+=v;if(i>=size)sum-=values[i-size];return sum/Math.min(i+1,size);});
}

const CONNECTION={off:'未连接',connecting:'正在连接…',awaiting_browser:'等待浏览器授权…',connected:'已连接',
  connected_waiting_snapshot:'已连接，等待游戏数据',reconnecting:'连接中断，正在重试…',authorization_required:'授权不可用：检查网站权限或清除本机登录',
  authorization_timeout:'授权超时：重新开启连接',connection_replaced:'已被另一游戏实例连接',credential_delete_failed:'清除本机登录失败'};
export function connectionLabel(status){
  if(!status)return '—';
  if(CONNECTION[status])return CONNECTION[status];
  if(status.startsWith('connected_data_error:'))return `已连接，数据错误（${status.slice(21)}）`;
  return `连接错误（${status}）`;
}
