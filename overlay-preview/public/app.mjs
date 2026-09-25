import {DemoSource,HttpSource,formatTier} from './data.mjs';
const $=id=>document.getElementById(id);
const params=new URLSearchParams(location.search),orbit=location.pathname==='/orbit',obs=params.has('obs'),liveMode=document.body.dataset.source==='live'||params.get('source')==='live';
document.body.classList.toggle('orbit-page',orbit);document.body.classList.toggle('obs',obs);$('board').classList.toggle('orbit',orbit);
document.documentElement.classList.toggle('obs-root',obs);
$(orbit?'orbit-link':'apex-link').classList.add('active');
$('edition').textContent=orbit?'02 / ORBIT — AMBIENT BROADCAST':'01 / APEX — PRECISION BROADCAST';
$('page-title').textContent=orbit?'暖光随行，专注下一间。':'把每一步，带进直播。';
$('description').textContent=orbit?'铜金、柔和的圆弧与缓慢流动的光。带金时，整套仪表进入暖金状态。':'冰蓝色的转播仪表，清晰的路线层次。把空间让给游戏，让进步留在画面里。';
$('theme-signature').textContent=orbit?'ORBIT / FOLLOW THE LIGHT':'APEX / EVERY ROOM COUNTS';
$('obs-link').href=location.pathname+'?obs=1'+(liveMode?'&source=live':'');
let choiceSelect,contextNote;
if(liveMode&&!obs){
  const panel=document.createElement('div');panel.className='live-controls';
  const label=document.createElement('label');label.textContent='当前挑战 ';
  choiceSelect=document.createElement('select');label.append(choiceSelect);contextNote=document.createElement('span');
  panel.append(label,contextNote);document.querySelector('.controls').after(panel);
  choiceSelect.onchange=async()=>{const selected=choiceSelect.value;if(!data?.mapId||!selected)return;choiceSelect.disabled=true;
    try{const response=await fetch('/api/overlay/selection',{method:'POST',headers:{'Content-Type':'application/json','X-GoldenLink':'overlay'},body:JSON.stringify({mapId:data.mapId,challengeId:selected})});if(!response.ok)throw new Error();}
    catch{contextNote.textContent='选择未保存，请重试';}finally{choiceSelect.disabled=false;}
  };
}
function resize(){if(!obs)$('board').style.transform=`scale(${$('viewport').clientWidth/1920})`;}
new ResizeObserver(resize).observe($('viewport'));resize();
let data,initial,timer,playing=false,liveBusy=false;
const demoButtons=[...document.querySelectorAll('.buttons button')];
demoButtons.forEach(button=>button.disabled=true);
const format=(n,d=0)=>n==null?'—':n.toLocaleString('en-US',{minimumFractionDigits:d,maximumFractionDigits:d});
function set(id,value){const e=$(id);if(e.textContent!==value){e.textContent=value;e.classList.remove('value-change');void e.offsetWidth;e.classList.add('value-change');}}
function render(){
  const d=data,c=d.cct,m=d.catalog;
  if(choiceSelect){
    const signature=JSON.stringify([d.mapId,d.choices,d.selectedChallengeId]);
    if(choiceSelect.dataset.signature!==signature){choiceSelect.dataset.signature=signature;choiceSelect.replaceChildren();
      const prompt=document.createElement('option');prompt.value='';prompt.textContent='请选择挑战';choiceSelect.append(prompt);
      for(const ch of d.choices){const opt=document.createElement('option');opt.value=ch.id;opt.textContent=ch.name;choiceSelect.append(opt);}
      choiceSelect.value=d.selectedChallengeId||'';choiceSelect.disabled=!d.choices.length;
    }
    contextNote.textContent=({ready:'选择自动保存，同步到 OBS',cached:'金榜连接中断，使用缓存资料',unmatched:'地图尚未配对，请到金榜账户页申请配对',authorization_required:'请开启金榜连接完成授权',unavailable:'金榜资料暂不可用',waiting:'等待进入地图'})[d.contextStatus]||'等待本地数据';
  }
  demoButtons.forEach(button=>button.disabled=false);
  $('board').classList.toggle('golden',d.live.holdingGolden);$('board').classList.toggle('paused',d.live.paused);$('board').classList.toggle('disconnected',!d.connected);
  set('status',!d.connected?'连接中断':d.live.paused?'已暂停':d.live.holdingGolden?'正在带金':'练习中');
  set('source-label',d.source==='demo'?'演示':'实时');
  set('map-name',m.mapName||'未匹配地图');set('campaign',m.campaign||'地图包未知');
  const tierLabel=formatTier(m.tier);
  set('tier',tierLabel);$('tier').classList.toggle('tier-long',tierLabel.length>4);
  set('challenge',m.challenge||'未选择挑战');
  set('pb',format(d.area.noGoldenBestDeaths));set('total',format(d.area.totalDeaths));
  $('practice-pb').hidden=d.live.holdingGolden;$('golden-pb-panel').hidden=!d.live.holdingGolden;
  for(const [id,index,name] of [['golden-pb',c.goldenPbRoomIndex,c.goldenPb],['session-golden-pb',c.sessionGoldenPbRoomIndex,c.sessionGoldenPb]]){
    set(id,`${format(index)} / ${format(c.roomCount)}`);
    const bar=$(id+'-bar'),known=index!=null&&c.roomCount>0;
    bar.value=known?Math.min(1,Math.max(0,index/c.roomCount)):0;
    bar.title=name??'暂无记录';bar.setAttribute('aria-valuetext',known?`${index} / ${c.roomCount}，${name??''}`:'暂无记录');
  }
  set('room',d.live.room||'等待关卡');set('room-count',`${format(c.roomIndex)} / ${format(c.roomCount)}`);
  set('cp-label',c.checkpointIndex==null?'—':`CP ${c.checkpointIndex} / ${c.checkpoints.length}`);
  set('streak',format(c.streak));set('best-streak',format(c.bestStreak));set('success',format(c.successRate,2));
  set('samples',`${format(c.successes)} / ${format(c.attempts)}`);set('entry',format(c.entryRate,2));set('session-entry',format(c.sessionEntryRate,2)+'%');
  set('deaths',format(c.goldenDeaths));set('session-deaths',format(c.sessionGoldenDeaths));
  $('progress').style.width=`${c.roomCount?Math.min(100,Math.max(0,c.roomIndex/c.roomCount*100)):0}%`;
  const cpStart=Math.max(0,Math.min(c.checkpoints.length-3,(c.checkpointIndex??1)-2));
  const checkpoints=c.checkpoints.slice(cpStart,cpStart+3).map((cp,j)=>{const i=cpStart+j;
    const e=document.createElement('div');e.className='checkpoint'+(i+1===c.checkpointIndex?' current':i+1<c.checkpointIndex?' done':'');
    const node=document.createElement('div');node.className='cp-node';const n=document.createElement('span');n.textContent=i+1<c.checkpointIndex?'✓':String(i+1).padStart(2,'0');node.append(n);
    const names=document.createElement('div'),name=document.createElement('b'),en=document.createElement('small');name.textContent=cp.name||'未命名';names.append(name);
    const count=document.createElement('span');count.className='cp-rooms';count.textContent=`${cp.rooms??'—'} 间`;e.append(node,names,count);return e;
  });$('checkpoints').replaceChildren(...checkpoints);
  $('history').replaceChildren(...c.recent.map(value=>{const e=document.createElement('i');e.className=value?'pass':'fail';e.title=value?'通过':'失败';return e;}));
  const update=$('update-dot'),u=d.update;
  update.hidden=!(u.available&&(!obs||u.showInObs));update.title=u.latest?`CNGoldenLink ${u.latest} 可更新 · 在控制台查看`:'';
  set('state-line',!d.connected?'连接中断 · 保留最后快照':d.live.paused?'稍作停留，下一次继续。':d.live.holdingGolden?'金莓在身，下一间见。':'练习的每一步，都有迹可循。');
  for(const [id,state]of [['golden',d.live.holdingGolden],['paused',d.live.paused],['offline',!d.connected],['play',playing]])$(id).setAttribute('aria-pressed',String(state));
}
function step(){if(!data||data.live.paused||!data.connected)return;const c=data.cct;c.roomIndex=c.roomIndex%c.roomCount+1;data.live.room=`Room-${c.roomIndex}`;c.checkpointIndex=c.roomIndex<=4?1:c.roomIndex<=9?2:3;c.streak++;c.bestStreak=Math.max(c.streak,c.bestStreak);c.recent=[...c.recent.slice(1),true];c.successes++;c.attempts++;c.successRate=c.successes/c.attempts*100;render();}
$('change-challenge').onclick=()=>{const choices=['C','FC','All Major Secrets'];data.catalog.challenge=choices[(choices.indexOf(data.catalog.challenge)+1)%choices.length];render();};
$('next').onclick=step;$('golden').onclick=()=>{data.live.holdingGolden=!data.live.holdingGolden;render();};$('paused').onclick=()=>{data.live.paused=!data.live.paused;render();};$('offline').onclick=()=>{data.connected=!data.connected;render();};
$('play').onclick=()=>{playing=!playing;clearInterval(timer);if(playing)timer=setInterval(step,3500);$('play').textContent=playing?'停止演示':'播放演示';render();};
$('reset').onclick=()=>{clearInterval(timer);playing=false;data=structuredClone(initial);$('play').textContent='播放演示';render();};
async function start(){
  const live=liveMode,source=live?new HttpSource():new DemoSource();
  if(live)document.querySelector('.controls').hidden=true;
  const poll=async()=>{if(liveBusy)return;liveBusy=true;try{data=await source.read(AbortSignal.timeout(3000));initial=structuredClone(data);render();}catch{
    if(data){data.connected=false;render();}else{$('map-name').textContent='等待本地数据';$('status').textContent='数据接口尚未连接';$('source-label').textContent=live?'NO DATA':'DEMO ERROR';}
  }finally{liveBusy=false;}};
  await poll();if(live)setInterval(poll,500);
}
start();
