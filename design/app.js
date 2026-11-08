'use strict';
const PRODUCT = 'SVS Performance Monitor'; // Single runtime product-name token.
const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const finite = v => typeof v === 'number' && Number.isFinite(v);
const number = (v,d=1) => finite(v) ? v.toFixed(d) : '—';
const fmt = (v,unit,d=1) => finite(v) ? number(v,d)+' '+unit : '—';
const readout = (label,value,note='') => `<dl><dt>${esc(label)}</dt><dd>${esc(value)}${note?`<small>${esc(note)}</small>`:''}</dd></dl>`;
const clone = o => JSON.parse(JSON.stringify(o));
const params = new URLSearchParams(location.search);
function toast(message){$('toast-text').textContent=message;$('toast').hidden=false;}
$('dismiss').onclick=()=>{$('toast').hidden=true;};
// Device preference is the default. The light palette is explicitly proposed.
function theme(value){
  document.documentElement.removeAttribute('data-theme');
  $('light-override')?.remove();
  if(value==='dark') document.documentElement.dataset.theme='dark';
  if(value==='light'){
    const style=document.createElement('link');style.id='light-override';
    style.rel='stylesheet';style.href='light.css';document.head.append(style);
  }
}
$('theme').value=params.get('theme')||'system';
$('theme').onchange=e=>theme(e.target.value);theme($('theme').value);

function fixture(state){
  const s=clone(FIXTURES.gpu);
  if(state==='waiting')return clone(FIXTURES.waiting);
  if(state==='cpu'){
    s.verdict={label:'CPU-bound',reason:'CPU busy 20.1 ms, GPU busy 10.0 ms, frame 21.4 ms'};
    s.mainThreadBusyPct=97;s.gpu3DPct=58;
    s.topThreads[0].busyPct=97;
    Object.assign(s.frames,{fps:46.7,frames:47,low1PctFps:31.4,frameTimeAvgMs:21.4,frameTimeP50Ms:21.0,frameTimeP99Ms:31.8,cpuBusyMs:20.1,cpuWaitMs:1.3,gpuBusyMs:10.0,gpuWaitMs:11.4});
  }
  if(state==='neither'){s.verdict={label:'neither',reason:'Both busy times are below 80% of the frame time'};s.mainThreadBusyPct=42;s.gpu3DPct=55;Object.assign(s.frames,{fps:60,low1PctFps:58,frames:60,frameTimeAvgMs:16.7,frameTimeP99Ms:17.2,cpuBusyMs:7,cpuWaitMs:9.7,gpuBusyMs:9,gpuWaitMs:7.7});}
  if(state==='both'){s.verdict={label:'both near the limit',reason:'CPU busy 16.0 ms and GPU busy 16.5 ms, frame 17.2 ms'};s.mainThreadBusyPct=95;s.gpu3DPct=98;s.frames.cpuBusyMs=16;s.frames.cpuWaitMs=1.2;}
  if(['basic','setup','mixed','unknown','starting','no-frames','missing','error','exited'].includes(state)){
    s.mode='basic';s.mainThreadBusyPct=62;s.gpu3DPct=98;s.verdict={label:'GPU-bound',reason:'GPU 3D 98%, main thread 62% busy'};
    s.frames=null;
  }
  if(state==='setup')s.frames={state:'denied',hint:'Performance Log Users membership required',frames:0};
  if(state==='mixed'){s.verdict={label:'mixed',reason:'main thread 87%, GPU 3D 86%'};s.mainThreadBusyPct=87;s.gpu3DPct=86;}
  if(state==='unknown'){s.verdict={label:'unknown',reason:'no GPU counters on this machine'};s.gpu3DPct='NaN';s.gpuDedicatedMb='NaN';}
  if(state==='starting')s.frames={state:'starting',hint:'Starting capture',frames:0};
  if(state==='no-frames')s.frames={state:'running',hint:'No frames received',frames:0};
  if(state==='missing')s.frames={state:'off',hint:'PresentMon not found',frames:0};
  if(state==='error')s.frames={state:'error',hint:'Unable to start capture',frames:0};
  if(state==='exited')s.frames={state:'exited',hint:'PresentMon stopped',frames:0};
  if(state==='no-host')s.fsp={present:false,version:'',channel:'',build:'',plugins:[],services:[],written:null};
  if(state==='sampling-off'){s.mainThreadModules=[];s.mainThreadSamples=0;s.note='Sampling disabled';}
  return s;
}

const verdictCopy={
  'GPU-bound':['Your graphics card is the limit','The game is waiting for graphics work to finish. Try lowering graphics settings, then compare the same place in the game.'],
  'CPU-bound':['Your processor is the limit','The game needs more time to finish its calculations. Try reducing the number of active vehicles or objects, then compare the same place.'],
  'neither':['Neither is the limit right now','The game may be holding a steady frame rate, showing a menu or loading.'],
  'both near the limit':['Your processor and graphics card are both near the limit','Both are working hard. Try one change at a time and compare the same place in the game.'],
  'mixed':['The limit is changing','The readings are close. Keep playing in the same area to see a clearer pattern.'],
  'unknown':['There is not enough information yet','Graphics readings are unavailable. The other readings are still shown below.'],
  'waiting':['Waiting for the game','Start Farming Simulator 25. This page will pick it up automatically.']
};
function chart(kind,s,state){
  if(!s.attached)return '<p class="meta" style="padding-block:24px">The trend will begin when the game starts.</p>';
  if(state==='disconnected')return '<p class="meta" style="padding-block:24px">Live trend paused. Waiting to reconnect.</p>';
  const full=kind==='frame',max=full?40:100;
  const width=Math.max(240,(full?$('frame-chart'):$('load-chart')).clientWidth||940),right=width-8;
  const base=full?[s.frames.frameTimeAvgMs,s.frames.cpuBusyMs,s.frames.gpuBusyMs]:[0,s.mainThreadBusyPct,s.gpu3DPct];
  const names=full?['frame','cpu','gpu']:['cpu','gpu'];
  const paths=names.map((name,k)=>{
    const idx=full?k:k+1;
    if(!finite(base[idx]))return '';
    const points=Array.from({length:61},(_,i)=>{
      const v=Math.max(0,Math.min(max,base[idx]*(.94+.05*Math.sin(i*.63+idx))+(full&&idx===0&&i===39?9:0)));
      return `${i?'L':'M'}${40+i*(right-40)/60},${125-v/max*112}`;
    }).join(' ');
    return `<path class="${name}" d="${points}"/>`;
  }).join('');
  return `<svg class="chart" viewBox="0 0 ${width} 155" role="img" aria-label="${full?'Frame times in milliseconds':'Processor and graphics load in percent'}, synthetic five-minute sample"><title>${full?'Frame times':'Load over time'}</title>${[0,max/2,max].map(v=>`<line class="grid" x1="40" x2="${right}" y1="${125-v/max*112}" y2="${125-v/max*112}"/><text x="0" y="${129-v/max*112}">${v}</text>`).join('')}${paths}<text x="40" y="151">−5 min</text><text x="${right-32}" y="151">Now</text></svg>`;
}
function dashboard(state){
 const s=fixture(state),f=s.frames,full=s.attached&&s.mode==='full'&&f?.frames>0,active=s.attached;
 const copy=verdictCopy[active?s.verdict.label:'waiting']||verdictCopy.unknown;
 $('verdict').textContent=state==='disconnected'?'Connection lost':copy[0];
 $('reason').textContent=state==='disconnected'?'The last readings below are from 2:32:05 PM. Waiting to reconnect to the PC running '+PRODUCT+'.':copy[1]+(active&&!full?' This is an estimate from current loads.':'');
 $('game-status').innerHTML=`<i class="dot ${!active||state==='disconnected'?'wait':''}" aria-hidden="true"></i>${state==='disconnected'?'Reconnecting…':active?'Watching the game':'Ready when you are'}`;
 $('mode').textContent=full?'Full mode · frame times':'Basic mode · loads only';
 $('fps').textContent=full?number(f.fps):'—';$('low').textContent=full?number(f.low1PctFps):'—';$('ft').innerHTML=full?`${number(f.frameTimeAvgMs)} <span class="unit">ms</span>`:'—';
 $('trend').hidden=!full;
 if(full){$('frame-chart').innerHTML=chart('frame',s,state);$('trend-summary').textContent=`Current average ${number(f.frameTimeAvgMs)} ms · slowest 1% boundary ${number(f.frameTimeP99Ms)} ms. Sample trend includes an occasional spike.`;}
 const banners={
 setup:['See FPS and spot stutter','Set up frame times on this PC. Windows will ask for permission once, then sign out and back in.','Set up','settings.html#frame-times'],
 basic:['Frame times are off','You can still see processor and graphics loads. Turn on frame times to see FPS and stutter.','Set up','settings.html#frame-times'],
 starting:['Starting frame times','Keep the game open. Load readings are available while frame times start.'],
 'no-frames':['Waiting for frame times','No frames have arrived yet. Bring the game to the foreground and keep playing.'],
 missing:['Frame times need a missing file','The frame-times tool is missing. Check the files that came with '+PRODUCT+'.','Open Settings','settings.html#frame-times'],
 error:['Frame times could not start','Load readings are still available. Open Settings to check setup, then restart '+PRODUCT+'.','Open Settings','settings.html#frame-times'],
 exited:['Frame times stopped','Load readings are still available. Restart '+PRODUCT+' to try again.'],
 waiting:['No readings yet','FPS, frame times and loads will appear when the game is running.'],
 disconnected:['These readings are out of date','Check that '+PRODUCT+' is running on your PC and that your home network is connected.']};
 const b=banners[state];$('frame-banner').hidden=!b;
 if(b)$('frame-banner').innerHTML=`<span class="symbol" aria-hidden="true">ⓘ</span><div><strong>${esc(b[0])}</strong><p class="small">${esc(b[1])}</p></div>${b[2]?`<div class="actions"><a class="button primary" href="${b[3]}">${b[2]}</a></div>`:''}`;
 $('process').textContent=active?`${s.process} · PID ${s.pid}`:'No game connected';
 $('loads').innerHTML=readout('Main thread busy',active?fmt(s.mainThreadBusyPct,'%'):'—','Of one logical core')+readout('GPU 3D',active?fmt(s.gpu3DPct,'%'):'—','Busiest 3D engine')+readout('Game CPU',active?fmt(s.processCpuPct,'%'):'—',`Across ${s.cores} logical cores`)+readout('VRAM · game',active?fmt(finite(s.gpuDedicatedMb)?s.gpuDedicatedMb/1024:null,'GiB'):'—','Dedicated graphics memory')+readout('RAM · game',active?fmt(s.workingSetMb/1024,'GiB'):'—','Working set')+readout('Private memory',active?fmt(s.privateMb/1024,'GiB'):'—','Committed private bytes');
 $('load-chart').innerHTML=chart('load',s,state);
 const fields=[['CPU busy / frame','cpuBusyMs'],['CPU wait / frame','cpuWaitMs'],['GPU busy / frame','gpuBusyMs'],['GPU wait / frame','gpuWaitMs'],['GPU latency','gpuLatencyMs'],['Display latency','displayLatencyMs'],['Median frame time · p50','frameTimeP50Ms'],['Slowest 1% boundary · p99','frameTimeP99Ms']];
 $('frame-details').innerHTML=fields.map(([label,key])=>readout(label,full?fmt(f[key],'ms'):'—')).join('')+readout('Frames this interval',full?number(f.frames,0):'—')+readout('Presentation / runtime',full?`${f.presentMode} / ${f.runtime}`:'—');
 $('capture-note').textContent=`Capture: ${f?.state||'disabled'}${f?.hint?' · '+f.hint:''}. ${s.elevated?'Full Windows permissions':'Standard Windows permissions'}.`;
 $('modules').innerHTML=s.mainThreadModules.length?s.mainThreadModules.map(m=>`<tr><td>${esc(m.name)}<div class="meta">${esc(m.kind)}</div></td><td class="numeric">${number(m.percent)}%<div class="meta">${m.samples} samples</div></td></tr>`).join(''):'<tr><td colspan="2">'+(active?'No samples. Turn on the breakdown in Settings to collect them.':'No samples while waiting for the game.')+'</td></tr>';
 $('sampling').textContent=`${s.mainThreadSamples} samples this interval. Shares reflect sampled wall time, including waits.`;
 $('threads').innerHTML=s.topThreads.length?s.topThreads.map(t=>`<tr><td>${t.tid}${t.main?' · main':''}</td><td class="numeric">${number(t.busyPct)}%</td></tr>`).join(''):'<tr><td colspan="2">No thread readings yet.</td></tr>';
 const host=s.fsp;
 $('host').textContent=host.present?`Host ${host.version} · channel ${host.channel} · build ${host.build}`:'FSPluginHost was not found. '+PRODUCT+' works without it.';
 $('host-written').textContent=host.present?`${!active?'Saved status; this does not mean the game is running. ':''}Status file written ${host.written}.`:'';
 $('plugins').innerHTML=(host.plugins||[]).map(p=>`<article class="plugin"><h3>${esc(p.name)}</h3><div class="row" style="margin-top:6px"><span class="meta">Version ${esc(p.version)}</span><span class="tag">${esc(p.state)}</span></div>${p.reason?`<p class="small" style="margin-top:8px">${esc(p.reason)}</p>`:''}<div class="plugin-fields">${Object.entries(p.fields||{}).map(([k,v])=>`<code>${esc(k)}=${esc(v)}</code>`).join('')}</div></article>`).join('');
 $('services').textContent=host.present?'Services: '+(host.services.length?host.services.join(' · '):'None reported'):'';
 $('diagnostics').textContent=`Snapshot ${s.time} · Mode ${s.mode} · Attached ${s.attached} · Verdict ${s.verdict.label}: ${s.verdict.reason}${s.note?' · '+s.note:''}`;
}
if(document.body.dataset.page==='dashboard'){
 $('scenario').value=params.get('state')||'gpu';dashboard($('scenario').value);$('scenario').onchange=e=>dashboard(e.target.value);
 if(params.get('advanced')==='1')$('advanced').open=true;
 $('advanced').addEventListener('toggle',()=>{if($('advanced').open)dashboard($('scenario').value);});
 window.addEventListener('resize',()=>dashboard($('scenario').value));
}

function settingsState(state){
 const phone=state==='phone';
 $('settings-origin').textContent=phone?'Phone · read-only':'This PC';
 document.querySelectorAll('#settings-form input[type=checkbox]').forEach(c=>{
   c.disabled=phone;c.closest('label').hidden=phone;
   const old=c.closest('.setting').querySelector('.readonly-value');old?.remove();
   if(phone){const v=document.createElement('span');v.className='tag readonly-value';v.textContent=c.checked?'On':'Off';c.closest('.setting').append(v);}
 });
 ['profile','port'].forEach(id=>$(id).readOnly=phone);
 $('setup').hidden=phone||state==='ready'||state==='signout';$('save').hidden=phone;
 $('setup-status').textContent=state==='ready'?'Ready':state==='signout'?'Sign out needed':'Not set up';
 const msg={phone:['Settings can be changed on the PC running '+PRODUCT,'These values are read-only. You can select and copy the folder and port.'],signout:['One more step','Sign out of Windows and back in to finish setting up frame times.'],cancelled:['Permission was cancelled','No changes were made. You can try Set up again when you are ready.'],failure:['Frame times could not be set up','Try again on the PC running '+PRODUCT+'. Open Advanced for technical details.']};
 $('settings-notice').hidden=!msg[state];
 if(msg[state])$('settings-notice').innerHTML=`<div><strong>${msg[state][0]}</strong><p class="small">${msg[state][1]}</p>${state==='failure'?'<details><summary>Advanced</summary><p class="mono">Sample error: account group update failed. No system changes were made.</p></details>':''}</div>`;
 $('save-note').textContent=phone?'Viewing settings from another device.':'Preview only. Changes are not saved to your PC.';
}
if(document.body.dataset.page==='settings'){
 $('settings-state').value=params.get('view')||'pc';settingsState($('settings-state').value);
 $('settings-state').onchange=e=>settingsState(e.target.value);
 $('setup').onclick=()=>{$('settings-state').value='signout';settingsState('signout');toast('Preview: Windows would ask for permission here. No system changes were made.');};
 $('lan').onchange=e=>{$('network-address').textContent=e.target.checked?'Preview address: http://192.168.1.42:8765/ · home network only':'Home network sharing is off.';if(e.target.checked)toast('Preview: Windows would ask once to allow access on your private home network.');};
 $('elevated').onchange=()=>toast('Preview: Windows would ask once to update how '+PRODUCT+' starts.');
 $('settings-form').onsubmit=e=>{e.preventDefault();const n=$('port').valueAsNumber,valid=Number.isInteger(n)&&n>=1&&n<=65535;$('port-error').hidden=valid;$('port').setAttribute('aria-invalid',!valid);if(!valid){$('port').focus();return;}toast('Preview changes applied to this page. Nothing was saved to your PC.');};
 $('port').addEventListener('invalid',e=>{e.preventDefault();$('port-error').hidden=false;$('port').setAttribute('aria-invalid','true');$('port').focus();});
}
if(document.body.dataset.page==='welcome'){
 $('welcome-setup').onclick=()=>{$('welcome-status').textContent='Preview: permission granted. Sign out of Windows and back in to finish.';$('welcome-setup').textContent='Sign out needed';$('welcome-setup').disabled=true;};
}

if(document.body.dataset.page==='tray'){
 const copy={waiting:'Waiting for the game',watching:'Watching the game',cpu:'Your processor is the limit',gpu:'Your graphics card is the limit'};
 $('tray-state').onchange=e=>{$('tray-status').textContent=copy[e.target.value];};
 $('exit').onclick=()=>toast('Preview only. Exit would stop monitoring, stop frame capture and remove the tray icon.');
 $('icon-gallery').innerHTML=[['app','App icon'],['tray-waiting','Waiting'],['tray-watching','Watching'],['tray-waiting-light','Waiting · light Windows tray'],['tray-watching-light','Watching · light Windows tray']].map(([src,label])=>`<div class="icon-sample"${src.endsWith('-light')?' style="background:#EFF4F4;color:#10262C;padding:8px;border-radius:4px"':''}><img src="icons/${src}.svg" width="16" height="16" alt="${label}, 16 pixels"><img src="icons/${src}.svg" width="32" height="32" alt="${label}, 32 pixels"><span>${label}</span></div>`).join('');
}

if(document.body.dataset.page==='log'){
 const samples=[
 ['marker','Game restarted · 2:30:00 PM'],['','2026-11-08 14:30:00 Farming Simulator 25 · Version 1.25.1.0'],['','2026-11-08 14:30:01 Settings: C:\\Users\\Player\\Documents\\My Games\\FarmingSimulator2025'],['','2026-11-08 14:30:03 Loading map: Riverbend Springs'],['','2026-11-08 14:30:04 [FSPluginHost] FSPMesh 0.3.0 active'],['warn','2026-11-08 14:30:06 Warning: Missing optional texture for FS25_DemoTrailer/decals/dirt_diffuse.dds'],['','2026-11-08 14:30:08 [FSPMesh] shapes=on captures=128 heldMB=42.6'],['pass','2026-11-08 14:30:09 [FSPAutoTest] PASS: vehicle loaded'],['err','2026-11-08 14:30:10 Error: Cannot open file mods/FS25_DemoTrailer/sounds/coupling.xml'],['fail','2026-11-08 14:30:11 [FSPAutoTest] FAIL: trailer coupling sound was not found'],['','2026-11-08 14:30:12 Game saved successfully.']
 ];
 let model=[],paused=false,stick=true,tick=0,connected=true,held=[],state='live';const levels={err:false,warn:false,fail:false};
 function renderLog(){
   const previousScroll=$('log').scrollTop;
   const needle=$('filter').value.trim().toLowerCase(),filtered=Object.values(levels).some(Boolean);
   const visible=model.filter(([cls,t])=>cls==='marker'||(!needle||t.toLowerCase().includes(needle))&&(!filtered||levels[cls]));
   $('log').innerHTML=visible.length?visible.slice(-3000).map(([cls,t])=>`<div class="log-line ${cls}">${cls&&cls!=='marker'?`<span class="${cls==='warn'?'warning':cls==='pass'?'':'error'}">${cls==='err'?'ERROR':cls.toUpperCase()}</span> `:''}${esc(t)}</div>`).join(''):`<p class="meta">${model.length?'No lines match your filter.':'Waiting for lines…'}</p>`;
   $('log-count').textContent=`${model.length.toLocaleString()} lines · ${visible.length.toLocaleString()} shown`;
   $('log-status').innerHTML=`<i class="dot ${connected?'':'wait'}" aria-hidden="true"></i>${connected?'Live':'Reconnecting…'}${paused?' · paused'+(held.length?' (+'+held.length+')':''):''}`;
   $('pause').textContent=paused?'Resume'+(held.length?' ('+held.length+')':''):'Pause';$('pause').setAttribute('aria-pressed',paused);
   $('jump').hidden=stick||paused;if(stick&&!paused)$('log').scrollTop=$('log').scrollHeight;else $('log').scrollTop=previousScroll;
 }
 function logState(value){
   state=value;tick=0;held=[];paused=value==='paused';stick=value!=='scrolled';connected=value!=='reconnecting';
   model=value==='waiting'?[]:samples.map(l=>[...l]);
   if(value==='scrolled')model=[...model,...Array.from({length:50},(_,i)=>['',`2026-11-08 14:31:${String(i).padStart(2,'0')} [FSPMesh] captured shape ${i+129}`])];
   if(value==='restart')model=[['marker','Game restarted · 2:34:00 PM'],['','2026-11-08 14:34:00 Loading a new game. Previous run cleared.']];
   if(value==='gap')model.push(['marker','Some lines were missed while this page was disconnected.'],['','2026-11-08 14:33:10 Reconnected. Showing the newest available lines.']);
   renderLog();if(value==='scrolled')$('log').scrollTop=0;
 }
 for(const [id,key] of [['bErr','err'],['bWarn','warn'],['bFail','fail']])$(id).onclick=()=>{levels[key]=!levels[key];$(id).setAttribute('aria-pressed',levels[key]);renderLog();};
 $('filter').oninput=renderLog;
 $('pause').onclick=()=>{paused=!paused;if(!paused){model.push(...held);held=[];model=model.slice(-10000);}renderLog();};
 $('clear').onclick=()=>{model=[];held=[];renderLog();};
 $('jump').onclick=()=>{stick=true;renderLog();};
 $('log').addEventListener('scroll',()=>{stick=$('log').scrollHeight-$('log').scrollTop-$('log').clientHeight<40;$('jump').hidden=stick||paused;},{passive:true});
 $('log-state').value=params.get('state')||'live';logState($('log-state').value);$('log-state').onchange=e=>logState(e.target.value);
 // Deliberately slow local sample updates demonstrate Pause/Clear without a server.
 setInterval(()=>{if(!connected||['waiting','restart','gap','scrolled'].includes(state))return;tick++;const line=['',`2026-11-08 14:33:${String(tick%60).padStart(2,'0')} [FSPMesh] captures=${128+tick} heldMB=42.6`];if(paused)held.push(line);else model.push(line);held=held.slice(-10000);model=model.slice(-10000);renderLog();},4000);
}
