import {spawn} from 'node:child_process';
import {mkdir,writeFile,readFile,rm} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
const root=import.meta.dirname;
const sizes=[16,20,24,32,40,48,64,128,256];
const variants=['sv-mark','sv-mark-waiting','sv-mark-16','sv-mark-16-waiting','sv-mark-24','sv-mark-24-waiting'];
await mkdir(resolve(root,'png'),{recursive:true});
await mkdir(resolve(root,'comparison'),{recursive:true});
// Filled outlines avoid stroke alignment and font dependencies.
for(const name of variants){
 const waiting=name.includes('waiting'),grid=name.includes('-24')?24:name.includes('-16')?16:32;
 const s=grid===24?'M11 5H5L3 7V12L5 14H8V17H3V20H9L11 18V13L9 11H6V8H11Z':grid===16?'M7 3H3L2 4V8L3 9H5V11H2V13H6L7 12V8L6 7H4V5H7Z':'M14 6H6L4 8V16L6 18H10V22H4V26H12L14 24V16L12 14H8V10H14Z';
 const v=grid===24?'M12 5H15L17 15L19 5H22L19 20H15Z':grid===16?'M8 3H10L11 9L12 3H14L12 13H10Z':'M16 6H20L22 18L24 6H28L24 26H20Z';
 const edge=waiting?'#909090':'#526568',tile=waiting?'#171717':'#0B1416';
 const inset=grid===16?1:grid===24?1:2,r=grid===16?2:grid===24?3:4;
 const svg=`<svg xmlns="http://www.w3.org/2000/svg" width="${grid}" height="${grid}" viewBox="0 0 ${grid} ${grid}" role="img" aria-label="SV${waiting?' waiting':''}">\n  <rect width="${grid}" height="${grid}" rx="${r}" fill="${edge}"/>\n  <rect x="${inset}" y="${inset}" width="${grid-inset*2}" height="${grid-inset*2}" rx="${r-inset}" fill="${tile}"/>\n  <path fill="${waiting?'#D6D6D6':'#C6F33A'}" d="${s}"/>\n  <path fill="${waiting?'#BDBDBD':'#2CD4E0'}" d="${v}"/>\n</svg>\n`;
 await writeFile(resolve(root,name+'.svg'),svg);
}
const originals=[['current-watching','../../../src/wwwroot/brand/tray-watching.ico'],['current-waiting','../../../src/wwwroot/brand/tray-waiting.ico']];
const all=[...variants.map(v=>[v,`png/${v}-SIZE.png`]),...originals.map(([name])=>[name,`comparison/${name}-SIZE.png`])];
const cells=size=>all.map(([name,src])=>`<article><h3>${name}</h3>${['dark','light'].map(theme=>`<div class="taskbar ${theme}"><div class="sample"><img alt="${name}, ${size} pixels" src="${src.replace('SIZE',size)}" width="${size}" height="${size}"><span>1×</span></div><div class="sample"><img class="zoom" alt="${name}, ${size} pixels enlarged four times" src="${src.replace('SIZE',size)}" width="${size*4}" height="${size*4}"><span>4×</span></div></div>`).join('')}</article>`).join('');
await writeFile(resolve(root,'preview.html'),`<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>SV mark · taskbar comparison</title><style>
*{box-sizing:border-box}body{margin:0;padding:24px;background:#050C0E;color:#EDF4F5;font:14px/1.3 "Segoe UI",sans-serif}h1{font-size:24px;margin:0 0 8px}p{color:#C5D4D6;margin:0 0 20px}h2{font-size:20px;margin:24px 0 12px}h3{font:600 13px/1.3 "Segoe UI",sans-serif;margin:0;padding:10px;background:#101F23}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(var(--card),1fr));gap:12px}article{min-width:0;border:1px solid #294147;border-radius:6px;overflow:hidden}.taskbar{display:flex;gap:16px;align-items:center;justify-content:center;padding:12px;min-height:calc(var(--size)*4px + 48px)}.dark{background:#202020;color:#EEE}.light{background:#F3F3F3;color:#222}.sample{display:flex;flex-direction:column;gap:6px;align-items:center}.sample span{font-size:11px}img{display:block;flex-shrink:0}.zoom{image-rendering:pixelated;image-rendering:crisp-edges}footer{margin-top:24px;color:#C5D4D6}
</style><h1>SV · a mark for the taskbar</h1><p>Lime S / cyan V · neutral waiting state · dark #202020 / light #F3F3F3.<br>Every supplied variant and both current ICOs, at native size and 4× nearest-neighbour. PNGs render at device scale 1; no CSS filters.</p>${sizes.map(size=>`<section style="--size:${size};--card:${Math.max(220,size*5+48)}px"><h2>${size} px</h2><div class="grid">${cells(size)}</div></section>`).join('')}<footer>Use sv-mark-16 at 16–20 px, sv-mark-24 at 24 px, and the master at 32 px and above. Waiting counterparts use identical outlines.</footer></html>`);
await writeFile(resolve(root,'README.md'),`# SV monogram\n\nA compact, angular S and V preserve the studio logo's letter pairing without its small-scale visual noise.\nThe lime S (#C6F33A) and cyan V (#2CD4E0) sit on a dark tile (#0B1416) with a quiet edge (#526568).\nSolid filled paths, open counters and a gap between letters keep the silhouette readable at tray size.\nWaiting uses identical geometry: S #D6D6D6, V #BDBDBD, tile #171717 and edge #909090, all neutral greys.\nUse sv-mark-16.svg / sv-mark-16-waiting.svg at 16–20 px; their 16-unit outlines use integer coordinates.\nUse sv-mark-24.svg / sv-mark-24-waiting.svg at 24 px; their 24-unit outlines have wider three-pixel stems.\nUse sv-mark.svg / sv-mark-waiting.svg at 32 px and above; these are the scalable 32-unit masters.\nPNG contains all six variants at 16, 20, 24, 32, 40, 48, 64, 128 and 256 px, with transparent tile corners.\npreview.html and preview.png compare all sizes at 1× and 4× nearest-neighbour on both taskbar colours against the current ICOs.\nRebuild with node assets/brand/mark/render.mjs (local Chrome; no packages); tools/make_icons.py was absent from this worktree.\n`);
const profile=resolve(root,'.render-profile');
const chrome=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--no-first-run','--no-default-browser-check','--disable-gpu','--in-process-gpu','--no-sandbox','--remote-debugging-port=9447',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
const delay=ms=>new Promise(r=>setTimeout(r,ms));let ws;
try{
 let targets;for(let i=0;i<60;i++){try{targets=await(await fetch('http://127.0.0.1:9447/json')).json();break;}catch{await delay(250);}}
 if(!targets)throw Error('Chrome failed to start');
 ws=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);await new Promise(r=>ws.addEventListener('open',r,{once:true}));
 let id=0;const pending=new Map();ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}});
 const send=(method,params={})=>new Promise((resolve,reject)=>{pending.set(++id,{resolve,reject});ws.send(JSON.stringify({id,method,params}));});
 const js=async expression=>{const r=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
 await send('Page.enable');
 await send('Emulation.setDeviceMetricsOverride',{width:3200,height:1200,deviceScaleFactor:1,mobile:false});
 await send('Page.navigate',{url:pathToFileURL(resolve(root,'preview.html')).href});await delay(500);
 // Canvas draws the SVG at its final output size using Chrome's SVG rasterizer.
 for(const variant of variants)for(const size of sizes){
  const svg=await readFile(resolve(root,variant+'.svg'),'utf8');
  const result=await js(`(async()=>{const img=new Image();img.src='data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}';await img.decode();const c=document.createElement('canvas');c.width=c.height=${size};c.getContext('2d').drawImage(img,0,0,${size},${size});return c.toDataURL('image/png').split(',')[1]})()`);
  await writeFile(resolve(root,'png',`${variant}-${size}.png`),Buffer.from(result,'base64'));
 }
 for(const [name,src] of originals)for(const size of sizes){
  const url='data:image/x-icon;base64,'+(await readFile(resolve(root,src))).toString('base64');
  const result=await js(`(async()=>{const img=new Image();img.src=${JSON.stringify(url)};await img.decode();const c=document.createElement('canvas');c.width=c.height=${size};c.getContext('2d').drawImage(img,0,0,${size},${size});return c.toDataURL('image/png').split(',')[1]})()`);
  await writeFile(resolve(root,'comparison',`${name}-${size}.png`),Buffer.from(result,'base64'));
 }
 await send('Page.reload',{ignoreCache:true});await delay(300);
 const images=await js(`Promise.all([...document.images].map(async i=>{await i.decode();return i.naturalWidth>0})).then(a=>a.length)`);
 const height=await js('document.documentElement.scrollHeight');
 const shot=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:3200,height,scale:1}});
 await writeFile(resolve(root,'preview.png'),Buffer.from(shot.data,'base64'));
 const detail=await send('Page.captureScreenshot',{format:'png',clip:{x:0,y:0,width:3200,height:470,scale:1}});
 await writeFile(resolve(root,'review-16.png'),Buffer.from(detail.data,'base64'));
 console.log(JSON.stringify({pngs:54,previewImages:images,previewWidth:3200,previewHeight:height}));
}finally{ws?.close();chrome.kill();await delay(1000);await rm(profile,{recursive:true,force:true,maxRetries:8,retryDelay:300});}
