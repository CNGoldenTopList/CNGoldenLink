// Frontends depend only on this versioned presentation DTO, never on credentials or raw CCT internals.
export function formatTier(value) {
  if (!value) return '—';
  const code=value.trim().toLowerCase();
  const tier=/^([hml])([0-3])$/.exec(code);
  if(tier)return `${{h:'High',m:'Mid',l:'Low'}[tier[1]]} T${tier[2]}`;
  if(/^t(?:-1|[0-7])$/.test(code))return code.toUpperCase();
  const standard=/^(high|mid|low)(?:-|\s+)(?:std|standard)$/.exec(code);
  if(standard)return `${standard[1][0].toUpperCase()+standard[1].slice(1)} Std`;
  if(code==='standard'||code==='std')return 'Std';
  if(code==='undetermined')return '未定档';
  return value;
}

export function normalize(raw) {
  if(raw?.schema !== 'goldenlink.overlay/1') throw new Error('Unsupported overlay data');
  const number = v => typeof v === 'number' && Number.isFinite(v) ? v : null;
  const text = v => typeof v === 'string' ? v : null;
  return {
    mapId:text(raw.mapId),selectedChallengeId:text(raw.selectedChallengeId),contextStatus:text(raw.contextStatus),
    choices:Array.isArray(raw.choices)?raw.choices.map(c=>({id:text(c.id),name:text(c.name),tier:text(c.tier)})):[],
    schema:raw.schema,source:raw.source === 'demo'?'demo':'live',connected:raw.connected === true,
    catalog:{mapName:text(raw.catalog?.mapName),mapNameEn:text(raw.catalog?.mapNameEn),campaign:text(raw.catalog?.campaign),
      challenge:text(raw.catalog?.challenge),tier:text(raw.catalog?.tier),verified:raw.catalog?.verified === true},
    live:{room:text(raw.live?.room),holdingGolden:raw.live?.holdingGolden === true,paused:raw.live?.paused === true},
    cct:{...Object.fromEntries(['roomIndex','roomCount','checkpointIndex','streak','bestStreak','successRate','successes','attempts','entryRate','sessionEntryRate','goldenDeaths','sessionGoldenDeaths'].map(k=>[k,number(raw.cct?.[k])])),
      goldenPb:text(raw.cct?.goldenPb),sessionGoldenPb:text(raw.cct?.sessionGoldenPb),
      goldenPbRoomIndex:number(raw.cct?.goldenPbRoomIndex),sessionGoldenPbRoomIndex:number(raw.cct?.sessionGoldenPbRoomIndex),
      checkpoints:Array.isArray(raw.cct?.checkpoints)?raw.cct.checkpoints.slice(0,2000).map(cp=>({name:text(cp.name),short:text(cp.short),rooms:number(cp.rooms)})):[],
      recent:Array.isArray(raw.cct?.recent)?raw.cct.recent.slice(-20).filter(v=>typeof v==='boolean'):[]},
    area:{noGoldenBestDeaths:number(raw.area?.noGoldenBestDeaths),totalDeaths:number(raw.area?.totalDeaths)},
    update:{available:raw.update?.available === true,latest:text(raw.update?.latest),showInObs:raw.update?.showInObs !== false}
  };
}
export class HttpSource {
  constructor(endpoint='/api/overlay/state'){this.endpoint=endpoint;}
  async read(signal){
    const response=await fetch(this.endpoint,{signal,cache:'no-store',credentials:'omit'});
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    return normalize(await response.json());
  }
}
// /api/overlay/state is the proposed local Mod contract; the preview server does not fake a live endpoint.
export class DemoSource extends HttpSource {constructor(){super('/api/demo');}}
