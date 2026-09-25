import {HttpSource,formatTier} from './data.mjs';
import {normalizeInsights,normalizeSettings,rolling,connectionLabel} from './insights.mjs';
const $=id=>document.getElementById(id);
const live=document.body.dataset.source==='live'||new URLSearchParams(location.search).get('source')==='live';
const api=live?{state:'/api/overlay/state',insights:'/api/overlay/insights',settings:'/api/overlay/settings'}
  :{state:'/api/demo',insights:'/api/demo/insights',settings:'/api/demo/settings'};
const stateSource=new HttpSource(api.state);
const fmt=(n,d=0)=>n==null||!Number.isFinite(n)?'—':n.toLocaleString('en-US',{minimumFractionDigits:d,maximumFractionDigits:d});
const pct=(n,d=1)=>n==null?'—':fmt(n,d)+'%';
const text=(id,value)=>{const e=$(id);if(e.textContent!==value)e.textContent=value;};
async function json(url,signal){const r=await fetch(url,{signal,cache:'no-store',credentials:'omit'});if(!r.ok)throw new Error(`HTTP ${r.status}`);return r.json();}
async function post(url,body){
  const r=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-GoldenLink':'overlay'},body:JSON.stringify(body)});
  if(!r.ok)throw new Error(`HTTP ${r.status}`);
}
$('source').textContent=live?'LIVE':'DEMO';

// OBS links: absolute so they can be pasted as-is; the mod always serves live data.
for(const theme of ['apex','orbit'])$(`url-${theme}`).textContent=`${location.origin}/${theme}?obs=1`;
document.querySelectorAll('[data-copy]').forEach(button=>button.onclick=async()=>{
  const value=$(button.dataset.copy).textContent,label=button.textContent;
  try{await navigator.clipboard.writeText(value);}catch{const range=document.createRange();range.selectNodeContents($(button.dataset.copy));getSelection().removeAllRanges();getSelection().addRange(range);}
  button.textContent='已复制';button.classList.add('done');setTimeout(()=>{button.textContent=label;button.classList.remove('done');},1600);
});

// ---- Current state -----------------------------------------------------------------
let state,settings;
const choice=$('challenge');
choice.onchange=async()=>{
  if(!live||!state?.mapId||!choice.value)return;choice.disabled=true;
  try{await post('/api/overlay/selection',{mapId:state.mapId,challengeId:choice.value});$('challenge-note').textContent='已保存，OBS 同步显示';}
  catch{$('challenge-note').textContent='未保存，请重试';}finally{choice.disabled=false;}
};
function renderState(){
  const d=state,m=d.catalog;
  const inMap=d.connected&&!!d.live.room;
  text('map',inMap?(m.mapName||'未匹配地图'):'等待进入地图');
  text('map-sub',inMap?[m.campaign,m.challenge&&`${m.challenge} · ${formatTier(m.tier)}`].filter(Boolean).join(' · '):'在游戏中进入地图后，这里显示地图、挑战与实时数据。');
  const kicker=$('state');
  kicker.className='kicker'+(d.live.holdingGolden?' golden':!d.connected?' off':'');
  text('state',!d.connected?'游戏未在地图中':d.live.paused?'已暂停':d.live.holdingGolden?'正在带金':'练习中');
  text('room',inMap?`${d.live.room}${d.cct.roomIndex?`  ·  ${d.cct.roomIndex} / ${d.cct.roomCount}`:''}`:'—');
  const signature=JSON.stringify([d.mapId,d.choices,d.selectedChallengeId]);
  if(choice.dataset.signature!==signature){
    choice.dataset.signature=signature;choice.replaceChildren();
    const prompt=document.createElement('option');prompt.value='';prompt.textContent=d.choices.length?'请选择挑战':'无可选挑战';choice.append(prompt);
    for(const c of d.choices){const o=document.createElement('option');o.value=c.id;o.textContent=c.tier?`${c.name} · ${formatTier(c.tier)}`:c.name;choice.append(o);}
    choice.value=d.selectedChallengeId||'';choice.disabled=!live||!d.choices.length;
  }
  if(!document.activeElement||document.activeElement!==choice)
    text('challenge-note',({ready:'',cached:'使用缓存资料',unmatched:'地图尚未配对',authorization_required:'开启金榜连接后可选',unavailable:'金榜资料暂不可用',waiting:''})[d.contextStatus]??'');
}

// ---- Settings & update --------------------------------------------------------------
document.querySelectorAll('[data-setting]').forEach(input=>input.onchange=async()=>{
  const key=input.dataset.setting,value=input.checked;input.disabled=true;
  try{if(!live)throw new Error('demo');await post('/api/overlay/settings',{[key]:value});if(settings)settings.values[key]=value;$('settings-note').textContent='已保存。';}
  catch{input.checked=!value;$('settings-note').textContent=live?'保存失败：请确认游戏仍在运行。':'演示模式不能修改设置。';}
  finally{input.disabled=false;}
});
$('check-update').onclick=async()=>{
  const b=$('check-update');b.disabled=true;
  try{if(live)await post('/api/overlay/update-check',{});text('i-update','检查中…');setTimeout(()=>{b.disabled=false;pollSettings();},3000);}
  catch{b.disabled=false;text('i-update','请求失败');}
};
function renderSettings(){
  const s=settings.values,u=settings.update;
  document.querySelectorAll('[data-setting]').forEach(input=>{if(!input.disabled&&s[input.dataset.setting]!=null)input.checked=s[input.dataset.setting];});
  text('version',s.version?`v${s.version}`:'');text('i-version',s.version||'—');
  text('connection',connectionLabel(s.connectionEnabled?s.connectionStatus:'off'));
  text('i-cct',s.cctAvailable==null?'—':s.cctAvailable?'已加载':'未加载（图表与统计不可用）');
  text('i-port',s.overlayPort==null?'—':String(s.overlayPort));text('i-server',s.serviceBaseUrl||'—');
  const link=$('update-link');
  if(u?.available&&u.downloadUrl){link.hidden=false;link.href=u.downloadUrl;link.lastElementChild.textContent=`${u.latest} 可更新`;}
  else link.hidden=true;
  const checked=u?.checkedAt?new Date(u.checkedAt).toLocaleString('zh-CN',{hour12:false}):null;
  text('i-update',!s.checkUpdates?'已关闭自动检查':!u?'等待首次检查':u.available?`有新版本 ${u.latest}`:u.error&&!u.latest?`检查失败（${checked??'—'}）`:`已是最新${checked?` · ${checked}`:''}`);
}

// ---- Charts -------------------------------------------------------------------------
const C=globalThis.Chart;
const css=getComputedStyle(document.documentElement),color=n=>css.getPropertyValue(n).trim();
const RATE=[[50,color('--rate-bad')],[80,color('--rate-warn')],[95,color('--rate-ok')]],RATE_TOP=color('--rate-good');
// <50% 红，<80% 橙，80%–95% 浅绿，≥95% 深绿；未知为灰。
const rateColor=v=>v==null?color('--faint'):(RATE.find(([limit])=>v<limit)?.[1]??RATE_TOP);
const goldenRate=(reached,deaths)=>reached>0?(reached-deaths)/reached*100:null;
const BG=color('--bg'),ACCENT=color('--accent'),WARM=color('--warm'),MUTED=color('--muted'),LINE=color('--line'),FAINT=color('--faint'),INK=color('--ink');
if(C){
  C.defaults.color=MUTED;C.defaults.borderColor=LINE;C.defaults.font.family=color('--number-face');C.defaults.font.size=11;
  C.defaults.animation=false;C.defaults.maintainAspectRatio=false;
  C.defaults.plugins.legend.labels.boxWidth=10;C.defaults.plugins.legend.labels.boxHeight=2;
  C.defaults.plugins.tooltip.backgroundColor='#0b0f15';C.defaults.plugins.tooltip.borderColor=color('--line-strong');C.defaults.plugins.tooltip.borderWidth=1;
}
// CP boundaries as hairlines with the CP abbreviation: route structure without extra chart furniture.
// CP boundaries as hairlines, with abbreviations in a strip above the plot area (never over the bars).
const CP_STRIP=16;
const cpMarks={id:'cpMarks',
  beforeUpdate(chart){
    // Reserve the strip by making the legend box taller; the plot area starts below it.
    const legend=chart.legend;if(!legend||legend._cpStrip)return;legend._cpStrip=true;
    const fit=legend.fit;legend.fit=function(){fit.call(this);this.height+=CP_STRIP;};
  },
  afterDraw(chart,_,options){
    const cps=options.checkpoints;if(!cps?.length)return;const {ctx,chartArea:a,scales:{x}}=chart;
    ctx.save();ctx.font=`11px ${color('--text-face')}`;ctx.textBaseline='bottom';
    let lastRight=-Infinity;
    for(const cp of cps){if(cp.firstRoom==null)continue;
      const px=cp.firstRoom>1?(x.getPixelForValue(cp.firstRoom-2)+x.getPixelForValue(cp.firstRoom-1))/2:a.left;
      if(cp.firstRoom>1){ctx.strokeStyle=FAINT;ctx.setLineDash([2,3]);ctx.beginPath();ctx.moveTo(px,a.top-CP_STRIP+2);ctx.lineTo(px,a.bottom);ctx.stroke();}
      const label=cp.abbreviation||cp.name,w=ctx.measureText(label).width;
      // Keep inside the chart on the right, and skip a label that would collide with the previous one.
      const lx=Math.min(px+4,a.right-w);if(lx<lastRight+4)continue;lastRight=lx+w;
      ctx.fillStyle=MUTED;ctx.fillText(label,lx,a.top-3);}
    ctx.restore();
  }};
const charts={};
function chart(id,config){
  if(!C)return null;
  if(!charts[id])charts[id]=new C($(id),config());
  return charts[id];
}
const roomLabels=d=>d.rooms.map(r=>`${r.number} ${r.name}`);
const pbLabel=(n,d)=>n==null?'—':n>=d.winRoom?'通关':`${n} / ${d.roomCount}`;
function renderFunnel(d){
  const c=chart('c-funnel',()=>({type:'bar',plugins:[cpMarks],data:{labels:[],datasets:[
    {label:'累计到达率',data:[],backgroundColor:ACCENT+'99',hoverBackgroundColor:ACCENT,borderWidth:0,barPercentage:.9,categoryPercentage:1,order:2},
    {type:'line',label:'本次到达率',data:[],borderColor:WARM,backgroundColor:WARM,borderWidth:2,pointRadius:0,pointHitRadius:8,stepped:'middle',order:1}]},
    options:{interaction:{mode:'index',intersect:false},scales:{x:{grid:{display:false},ticks:{autoSkip:true,maxRotation:0}},y:{min:0,max:100,ticks:{callback:v=>v+'%'}}},
      plugins:{cpMarks:{},tooltip:{callbacks:{afterBody:items=>{const r=cur?.rooms[items[0].dataIndex];if(!r)return'';
        return[`累计 到达 ${fmt(r.reached)} · 死亡 ${fmt(r.goldenDeaths)} · 带金成功率 ${pct(goldenRate(r.reached,r.goldenDeaths))}`,`本次 到达 ${fmt(r.reachedSession)} · 死亡 ${fmt(r.goldenDeathsSession)} · 带金成功率 ${pct(goldenRate(r.reachedSession,r.goldenDeathsSession))}`];}}}}}}));
  if(!c)return;
  c.data.labels=roomLabels(d);
  c.data.datasets[0].data=d.rooms.map(r=>d.totals.runs?r.reached/d.totals.runs*100:null);
  c.data.datasets[1].data=d.rooms.map(r=>d.totals.runsSession?r.reachedSession/d.totals.runsSession*100:null);
  c.options.plugins.cpMarks.checkpoints=d.checkpoints;c.update('none');
}
let metric='golden';
document.querySelectorAll('[data-metric]').forEach(b=>b.onclick=()=>{metric=b.dataset.metric;
  document.querySelectorAll('[data-metric]').forEach(x=>x.setAttribute('aria-pressed',String(x===b)));if(cur)renderRooms(cur);});
function renderRooms(d){
  const c=chart('c-rooms',()=>({type:'bar',plugins:[cpMarks],data:{labels:[],datasets:[
    {label:'',data:[],backgroundColor:[],borderWidth:0,barPercentage:.9,categoryPercentage:1,stack:'a',order:2},
    {label:'',data:[],borderWidth:0,backgroundColor:FAINT,barPercentage:.9,categoryPercentage:1,stack:'a',order:3},
    {type:'line',label:'',data:[],borderColor:INK,backgroundColor:INK,borderWidth:1.5,pointRadius:2.5,pointHitRadius:8,showLine:false,order:1}]},
    options:{interaction:{mode:'index',intersect:false},scales:{x:{stacked:true,grid:{display:false},ticks:{autoSkip:true,maxRotation:0}},y:{stacked:true,beginAtZero:true}},
      plugins:{cpMarks:{},legend:{labels:{filter:item=>!!item.text}}}}}));
  if(!c)return;
  const [main,extra,line]=c.data.datasets;c.data.labels=roomLabels(d);c.options.plugins.cpMarks.checkpoints=d.checkpoints;
  const y=c.options.scales.y;
  if(metric==='golden'){
    const rates=d.rooms.map(r=>goldenRate(r.reached,r.goldenDeaths));
    main.label='累计带金成功率';main.data=rates;main.backgroundColor=rates.map(rateColor);
    extra.label='';extra.data=[];line.label='本次带金成功率';line.data=d.rooms.map(r=>goldenRate(r.reachedSession,r.goldenDeathsSession));
    y.max=100;y.ticks.callback=v=>v+'%';
    text('rooms-note','带金到达该房间后通过的比例。柱为累计，白点为本次；红 <50%，橙 <80%，浅绿 80–95%，深绿 ≥95%。');
  }else if(metric==='success'){
    main.label='最近 20 次成功率';main.data=d.rooms.map(r=>r.successRate);main.backgroundColor=d.rooms.map(r=>rateColor(r.successRate));
    extra.label='';extra.data=[];line.label='';line.data=[];y.max=100;y.ticks.callback=v=>v+'%';
    text('rooms-note','CCT 记录的每间房最近 20 次尝试（含练习）；红 <50%，橙 <80%，浅绿 80–95%，深绿 ≥95%。');
  }else{
    main.label='带金中';main.data=d.rooms.map(r=>r.timeInRunsMs/60000);main.backgroundColor=ACCENT+'aa';
    extra.label='练习';extra.data=d.rooms.map(r=>Math.max(0,r.timeMs-r.timeInRunsMs)/60000);line.label='';line.data=[];
    y.max=undefined;y.ticks.callback=v=>v+' 分';
    text('rooms-note','在每间房花费的游戏时间（分钟）。');
  }
  c.update('none');
}
function renderSession(d){
  const c=chart('c-session',()=>({type:'line',data:{labels:[],datasets:[
    {label:'到达房间',data:[],borderColor:ACCENT+'66',backgroundColor:[],borderWidth:1,pointRadius:2.5,pointHoverRadius:4,pointBorderWidth:0},
    {label:'10 次均值',data:[],borderColor:WARM,borderWidth:2.5,pointRadius:0,tension:.25}]},
    options:{interaction:{mode:'index',intersect:false},scales:{x:{grid:{display:false},ticks:{autoSkip:true,maxRotation:0}},
      y:{min:0,ticks:{precision:0}}},plugins:{tooltip:{callbacks:{title:items=>`第 ${items[0].label} 次`,
        label:item=>{const run=cur?.sessionRuns[item.dataIndex];return item.datasetIndex?`均值 ${fmt(item.parsed.y,1)}`:run?.won?'通关':`${run?.distance} · ${run?.room??''}`;}}}}}}));
  if(!c)return;
  const runs=d.sessionRuns;c.data.labels=runs.map((_,i)=>String(i+1));
  c.data.datasets[0].data=runs.map(r=>r.distance);c.data.datasets[0].backgroundColor=runs.map(r=>r.won?WARM:ACCENT);
  c.data.datasets[1].data=rolling(runs.map(r=>r.distance),10);
  c.options.scales.y.max=d.winRoom;c.options.scales.y.ticks.callback=v=>v===d.winRoom?'通关':v;
  c.update('none');
}
function renderHistory(d){
  const c=chart('c-history',()=>({type:'line',data:{labels:[],datasets:[
    {label:'PB',data:[],borderColor:INK,borderWidth:1.5,pointRadius:0,stepped:true},
    {label:'会话 PB',data:[],borderColor:ACCENT,borderWidth:1.5,pointRadius:2},
    {label:'会话平均到达',data:[],borderColor:WARM,borderWidth:2,pointRadius:2,tension:.2},
    {label:'成功率',data:[],borderColor:MUTED,borderDash:[4,4],borderWidth:1,pointRadius:0,yAxisID:'rate'}]},
    options:{interaction:{mode:'index',intersect:false},scales:{x:{grid:{display:false},ticks:{autoSkip:true,maxRotation:0}},
      y:{min:0,ticks:{precision:0}},rate:{position:'right',min:0,max:100,grid:{display:false},ticks:{callback:v=>v+'%'}}},
      plugins:{tooltip:{callbacks:{afterBody:items=>{const s=cur?.sessions[items[0].dataIndex];return s?`带金 ${fmt(s.runs)} 次`:'';}}}}}}));
  if(!c)return;
  const s=d.sessions;
  c.data.labels=s.map(x=>x.current?'本次':x.started?new Date(x.started).toLocaleDateString('zh-CN',{month:'2-digit',day:'2-digit'}):'—');
  c.data.datasets[0].data=s.map(x=>x.pb);c.data.datasets[1].data=s.map(x=>x.sessionPb);
  c.data.datasets[2].data=s.map(x=>x.averageDistanceSession);c.data.datasets[3].data=s.map(x=>x.successRate);
  c.options.scales.y.max=d.winRoom;c.options.scales.y.ticks.callback=v=>v===d.winRoom?'通关':v;
  c.update('none');
}
let cur;
function renderInsights(d){
  const ready=d.available&&d.roomCount>0;
  $('data-empty').hidden=ready;$('data-body').hidden=!ready;
  if(!ready){text('data-empty',d.reason==='no_route'?'当前地图没有 CCT 路线：在 CCT 中录制或导入路线后显示图表。':'进入一张已配置 CCT 路线的地图后显示图表。');cur=null;return;}
  cur=d;const t=d.totals;
  text('data-scope',`${d.mapName??'当前地图'}${d.challenge?` · ${d.challenge}`:''} · ${d.roomCount} 间 · 成功率取最近 20 次，仅在本机计算。`);
  text('k-runs',fmt(t.runs));text('k-runs-sub',`本次 ${fmt(t.runsSession)}${t.wins?` · 通关 ${fmt(t.wins)}`:''}`);
  text('k-avg',t.averageDistance==null?'—':fmt(t.averageDistance,1));text('k-avg-sub',`本次 ${t.averageDistanceSession==null?'—':fmt(t.averageDistanceSession,1)} / ${d.roomCount}`);
  text('k-pb',pbLabel(t.pb,d));text('k-pb-sub',`本次 ${pbLabel(t.sessionPb,d)}`);
  text('k-chance',pct(t.goldenChance,2));text('k-chance-sub','按每间最近 20 次成功率相乘');
  // 最难房间：到达 ≥ 5 次的房间中带金成功率最低的一间。
  const worst=d.rooms.filter(r=>r.reached>=5).map(r=>({r,rate:goldenRate(r.reached,r.goldenDeaths)})).sort((a,b)=>a.rate-b.rate)[0];
  text('k-choke',worst?worst.r.name:'—');text('k-choke-sub',worst?`带金成功率 ${pct(worst.rate)} · 第 ${worst.r.number} 间`:'样本不足');
  renderFunnel(d);renderRooms(d);renderSession(d);renderHistory(d);
  $('cp-rows').replaceChildren(...d.checkpoints.map(cp=>{const tr=document.createElement('tr');
    for(const [value,cls] of [[cp.name],[fmt(cp.rooms)],[fmt(cp.goldenDeaths)],[fmt(cp.goldenDeathsSession),'session'],[pct(cp.clearChance)]]){
      const td=document.createElement('td');td.textContent=value;if(cls)td.className=cls;tr.append(td);}return tr;}));
}

// ---- Polling ------------------------------------------------------------------------
const busy={};
function every(name,ms,work){const run=async()=>{if(busy[name]||document.hidden)return;busy[name]=true;
  try{await work(AbortSignal.timeout(4000));}catch{}finally{busy[name]=false;}};run();setInterval(run,ms);}
const pollSettings=()=>json(api.settings).then(raw=>{settings=normalizeSettings(raw);renderSettings();}).catch(()=>{
  text('connection','无法连接游戏');text('i-update','—');});
every('state',1000,async signal=>{try{state=await stateSource.read(signal);renderState();}
  catch(e){text('state','本地数据接口未连接');text('map','等待游戏');$('state').className='kicker off';throw e;}});
every('settings',2000,()=>pollSettings());
every('insights',3000,async signal=>{
  try{renderInsights(normalizeInsights(await json(api.insights,signal)));}
  catch(e){if(cur)return;$('data-body').hidden=true;$('data-empty').hidden=false;
    text('data-empty',`图表数据读取失败（${e.message}）。游戏仍在运行时请把 Everest 日志 log.txt 发给开发者。`);throw e;}
});
document.addEventListener('visibilitychange',()=>{if(!document.hidden)pollSettings();});
