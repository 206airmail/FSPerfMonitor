// Local Chrome render and interaction checks. Run: node design/verify.mjs
import {spawn} from 'node:child_process';
import {mkdir,writeFile,rm} from 'node:fs/promises';
import {resolve} from 'node:path';
import {openSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const root=resolve(import.meta.dirname),qa=resolve(root,'qa');await mkdir(qa,{recursive:true});
const profile=resolve(qa,'browser-profile');
const chrome=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--no-first-run','--no-default-browser-check','--disable-gpu','--in-process-gpu','--no-sandbox','--remote-debugging-port=9439',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:['ignore',openSync(resolve(qa,'chrome-output.txt'),'w'),openSync(resolve(qa,'chrome-errors.txt'),'w')]});
const delay=ms=>new Promise(r=>setTimeout(r,ms));
let ws;
try{
 let targets;for(let i=0;i<40;i++){try{targets=await (await fetch('http://127.0.0.1:9439/json')).json();break;}catch{await delay(250);}}
 if(!targets)throw Error('Local Chrome did not start');
 ws=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);await new Promise(r=>ws.addEventListener('open',r,{once:true}));
 let id=0;const pending=new Map(),errors=[],external=[];ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}else if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails.text+': '+(m.params.exceptionDetails.exception?.description||''));else if(m.method==='Network.requestWillBeSent'&&/^https?:/.test(m.params.request.url))external.push(m.params.request.url);});
 const send=(method,params={})=>new Promise((resolve,reject)=>{const requestId=++id;const timeout=setTimeout(()=>reject(Error('CDP timeout: '+method)),10000);pending.set(requestId,{resolve:v=>{clearTimeout(timeout);resolve(v)},reject});ws.send(JSON.stringify({id:requestId,method,params}));});
 const js=async expression=>{const v=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(v.exceptionDetails)throw Error(v.exceptionDetails.exception?.description||v.exceptionDetails.text);return v.result.value;};
 await send('Page.enable');await send('Runtime.enable');await send('Network.enable');await send('Network.setBlockedURLs',{urls:['http://*','https://*']});
 const reports=[],strings=new Map();
 async function view(page,width,state='',theme='dark',shot=false){
   await send('Emulation.setDeviceMetricsOverride',{width,height:width===390?844:900,deviceScaleFactor:1,mobile:width===390});
   await send('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value:theme}]});
   const query=page==='settings'?`?view=${state||'pc'}`:`?state=${state||(page==='log'?'live':'gpu')}`;
   await send('Page.navigate',{url:pathToFileURL(resolve(root,page+'.html')).href+query});await delay(180);
   const data=await js(`(()=>{const text=[];const walk=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT);while(walk.nextNode()){const n=walk.currentNode,t=n.textContent.trim();if(t&&!['SCRIPT','STYLE'].includes(n.parentElement.tagName))text.push(t);}for(const e of document.querySelectorAll('[aria-label],[placeholder]'))text.push(e.getAttribute('aria-label')||e.getAttribute('placeholder'));return {width:innerWidth,scrollWidth:document.documentElement.scrollWidth,title:document.title,text,body:document.body.innerText,theme:getComputedStyle(document.body).backgroundColor}})()`);
   for(const t of data.text){if(!strings.has(t))strings.set(t,new Set());strings.get(t).add(page);}
   if(data.scrollWidth>width)throw Error(`${page} ${state} overflow ${data.scrollWidth}/${width}`);
   reports.push({page,width,state,theme,overflow:false});
   if(shot){const img=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});await writeFile(resolve(qa,`${page}-${width}-${state||'default'}-${theme}.png`),Buffer.from(img.data,'base64'));}
   return data;
 }
 for(const width of [1280,1024,390])for(const page of ['dashboard','settings','log','welcome','tray'])await view(page,width,'','dark',true);
 for(const page of ['dashboard','settings','log','welcome','tray'])await view(page,320);
 for(const state of ['cpu','waiting','setup','basic','neither','both','mixed','unknown','starting','no-frames','missing','error','exited','disconnected','no-host','sampling-off']){
   await view('dashboard',390,state,'dark',['waiting','setup'].includes(state));
   await js(`document.getElementById('advanced').open=true`);
   if(await js('document.documentElement.scrollWidth>innerWidth'))throw Error('Advanced overflow '+state);
   const text=await js('document.getElementById("advanced").innerText');strings.set(text,new Set(['dashboard · Advanced']));
   if(state==='cpu'){await delay(50);const img=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});await writeFile(resolve(qa,'dashboard-390-cpu-advanced-dark.png'),Buffer.from(img.data,'base64'));}
 }
 for(const state of ['phone','ready','signout','cancelled','failure'])await view('settings',390,state,'dark',state==='phone');
 for(const state of ['paused','scrolled','reconnecting','waiting','restart','gap'])await view('log',390,state,'dark',['gap','paused'].includes(state));
 for(const page of ['dashboard','settings','log','welcome','tray'])await view(page,390,'','light',true);
 await view('settings',390,'phone');
 if(!await js(`document.getElementById('profile').readOnly&&document.getElementById('port').readOnly&&document.getElementById('save').hidden&&[...document.querySelectorAll('#settings-form input[type=checkbox]')].every(e=>e.disabled)`))throw Error('Phone read-only failed');
 await view('settings',1024,'pc');
 await js(`document.getElementById('setup').click()`);if(!await js(`document.getElementById('setup-status').innerText==='Sign out needed'`))throw Error('Setup preview failed');
 await js(`document.getElementById('port').value='70000';document.getElementById('settings-form').requestSubmit()`);if(!await js(`!document.getElementById('port-error').hidden`))throw Error('Port validation failed');
 await view('log',390,'live');
 await js(`document.getElementById('filter').value='COUPLING';document.getElementById('filter').dispatchEvent(new Event('input'))`);
 if(!await js(`document.getElementById('log').innerText.includes('coupling')&&!document.getElementById('log').innerText.includes('Game saved')`))throw Error('Filter failed');
 await js(`document.getElementById('filter').value='';document.getElementById('bWarn').click()`);
 if(!await js(`document.getElementById('log').innerText.includes('Warning:')&&!document.getElementById('log').innerText.includes('Error:')&&document.getElementById('log').innerText.includes('Game restarted')`))throw Error('Level filter or marker failed');
 await js(`document.getElementById('clear').click()`);if(!await js(`document.getElementById('log').innerText.includes('Waiting for lines')`))throw Error('Clear failed');
 await view('log',390,'scrolled');await js(`document.getElementById('jump').click()`);if(!await js(`document.getElementById('jump').hidden`))throw Error('Jump failed');
 await view('welcome',390);await js(`document.getElementById('welcome-setup').click()`);if(!await js(`document.getElementById('welcome-setup').disabled`))throw Error('Welcome setup failed');
 await view('dashboard',390);await js(`document.getElementById('theme').value='light';document.getElementById('theme').dispatchEvent(new Event('change'))`);await delay(150);
 if(!await js(`getComputedStyle(document.body).backgroundColor==='rgb(239, 244, 244)'`))throw Error('Manual light theme failed');
 // Keyboard operability of the native disclosure is checked on its summary.
 await js(`document.getElementById('advanced').open=false;document.querySelector('#advanced summary').focus()`);
 await send('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',windowsVirtualKeyCode:13,text:'\r',unmodifiedText:'\r'});await send('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});
 if(!await js(`document.getElementById('advanced').open`))throw Error('Keyboard disclosure failed');
 if(external.length||errors.length)throw Error(JSON.stringify({external,errors}));
 await writeFile(resolve(qa,'checks.json'),JSON.stringify({reports,checks:['All requested dashboard scenarios','Advanced has no page overflow','Phone settings read-only','Setup preview','Invalid port','Case-insensitive log filter','Level filter and persistent markers','Clear view only','Jump to live','Welcome setup','Manual and device light theme','No external requests','No JavaScript exceptions'],errors,external},null,2));
 await writeFile(resolve(qa,'copy-observed.json'),JSON.stringify([...strings].map(([text,pages])=>({text,pages:[...pages]})),null,2));
 console.log(`${reports.length} renders passed; interaction checks passed; zero JavaScript exceptions or external requests.`);
}finally{ws?.close();chrome.kill();await delay(1000);await rm(profile,{recursive:true,force:true,maxRetries:4,retryDelay:300}).catch(()=>{});}
