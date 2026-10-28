'use strict';
// Mode selection is explicit. Only the historical preview's missing route may use v1.
async function startManagementPage(){
 try{
  const response=await fetch('/api/configuration',{credentials:'same-origin',cache:'no-store'});
  let mode;
  if(response.status===404)mode={schemaVersion:1,canApply:true};
  else{if(!response.ok)throw Error();mode=await response.json();}
  if(mode.schemaVersion===2&&mode.canApply===false){const {startV2App}=await import('./v2-app.mjs');await startV2App();return;}
  if(mode.schemaVersion!==1||mode.canApply!==true)throw Error();
  startLegacyPage();
 }catch{
  document.getElementById('login-form').classList.add('hidden');
  document.getElementById('login-error').textContent='无法确认管理模式，请检查服务后重新加载；不会回退或改写配置。';
  const retry=document.createElement('button');retry.type='button';retry.textContent='重新加载页面';
  retry.addEventListener('click',()=>location.reload());document.getElementById('login').append(retry);
 }
}
function startLegacyPage(){
const $=id=>document.getElementById(id);
let csrf='',model=null,rows=[],saved='',contacts=[],running=false,statusStale=true,busy=false,serial=0,deleteKey=null,connectionText='';
const show=(id,value)=>$(id).classList.toggle('hidden',!value);
const draft=()=>JSON.stringify(rows.map(({key,...row})=>row));
const banner=(text,error=false)=>{$('banner').textContent=text;$('banner').classList.toggle('error',error);};
async function api(path,method='GET',body){const res=await fetch('/api/'+path,{method,headers:{'Content-Type':'application/json',...(csrf?{'X-CSRF-Token':csrf}:{})},credentials:'same-origin',cache:'no-store',...(body===undefined?{}:{body:JSON.stringify(body)})});const value=await res.json();if(!res.ok){if(res.status===401){show('login',true);show('workspace',false);show('logout',false);csrf='';}throw Error(value.error||'请求失败，请重试');}return value;}
function adopt(value){model=value;rows=value.extensions.map(row=>({...row,password:'',key:++serial}));saved=draft();$('sip-address').textContent=value.sipServer+':'+value.sipPort;render();}
function changed(){return draft()!==saved;}
function updateSave(){const dirty=changed();$('dirty').textContent=busy?'正在验证并应用…':dirty?'有未保存的修改':'配置已保存';$('save').disabled=busy||!dirty;$('add').disabled=busy;}
function stateFor(row){if(!row.enabled)return ['','已停用'];if(statusStale)return ['loading','状态待刷新'];const list=contacts.filter(c=>c.number===row.number);if(list.some(c=>c.state==='Avail'))return ['online','在线'];if(list.length)return ['loading','终端待确认'];return ['','离线'];}
function inputField(label,value,type,onchange,attrs={}){const box=document.createElement('div');box.className='field';const id='field-'+(++serial);const lab=document.createElement('label');lab.htmlFor=id;lab.textContent=label;const input=document.createElement('input');input.id=id;input.type=type;input.value=value;input.disabled=busy;Object.assign(input,attrs);input.addEventListener('input',()=>onchange(input.value));box.append(lab,input);return {box,input};}
function check(label,value,onchange){const lab=document.createElement('label');lab.className='check';const input=document.createElement('input');input.type='checkbox';input.checked=value;input.disabled=busy;input.addEventListener('change',()=>onchange(input.checked));const text=document.createElement('span');text.textContent=label;lab.append(input,text);return lab;}
function button(label,action,cls=''){const b=document.createElement('button');b.textContent=label;b.type='button';b.className=cls;b.disabled=busy;b.addEventListener('click',action);return b;}
function patch(row,key,value){row[key]=value;updateSave();}
function render(){const list=$('extensions');list.replaceChildren();show('empty',rows.length===0);for(const row of rows){
 const card=document.createElement('article');card.className='extension';const top=document.createElement('div');top.className='extension-top';const title=document.createElement('div');title.className='account-title';const dot=document.createElement('i');const [state,label]=stateFor(row);dot.className='dot '+state;const name=document.createElement('span');name.textContent=row.number||'新分机';const badge=document.createElement('span');badge.className='badge';badge.textContent=label;title.append(dot,name,badge);
 const actions=document.createElement('div');actions.className='actions';if(row.hasPassword&&model.extensions.some(v=>v.number===row.number)){actions.append(button('连接信息',()=>connection(row),'quiet'));}actions.append(button('移除',()=>{deleteKey=row.key;$('delete-text').textContent='将分机 '+(row.number||'（未填写）')+' 从本次草稿中移除？';$('delete-dialog').showModal();},'quiet'));top.append(title,actions);card.append(top);
 const grid=document.createElement('div');grid.className='form-grid';const number=inputField('分机号',row.number,'text',v=>{patch(row,'number',v);name.textContent=v||'新分机';},{inputMode:'numeric',maxLength:8,autocomplete:'off'});const display=inputField('显示名称（可选）',row.name,'text',v=>patch(row,'name',v),{maxLength:80});const pass=inputField('分机密码',row.password,'password',v=>patch(row,'password',v),{maxLength:128,autocomplete:'new-password',placeholder:row.hasPassword?'留空保留已保存密码':'至少 16 位，或生成随机密码'});const pw=document.createElement('div');pw.className='password-row';pass.input.replaceWith(pw);pw.append(pass.input,button('生成',()=>{const data=new Uint8Array(24);crypto.getRandomValues(data);row.password=Array.from(data,x=>x.toString(16).padStart(2,'0')).join('');pass.input.value=row.password;updateSave();}));grid.append(number.box,display.box,pass.box);card.append(grid);
 const options=document.createElement('div');options.className='options';options.append(check('启用账号',row.enabled,v=>{patch(row,'enabled',v);render();}),check('接收外线群振',row.ring,v=>patch(row,'ring',v)));card.append(options);
 const details=document.createElement('details');details.className='advanced';const summary=document.createElement('summary');summary.textContent='权限与终端';details.append(summary);const more=document.createElement('div');more.className='options';more.append(check('允许运营商外呼',row.allowOutgoing,v=>patch(row,'allowOutgoing',v)),check('允许消息与短信路由',row.allowMessages,v=>patch(row,'allowMessages',v)));const max=inputField('最多注册终端',row.maxContacts,'number',v=>patch(row,'maxContacts',Number(v)),{min:1,max:8});max.box.className='max-contacts';more.append(max.box);details.append(more);card.append(details);list.append(card);
 }updateSave();paintStatus();}
function paintStatus(){const online=new Set(contacts.filter(c=>c.state==='Avail').map(c=>c.number));$('online').textContent=statusStale?'—':rows.filter(r=>r.enabled&&online.has(r.number)).length+' / '+rows.filter(r=>r.enabled).length;$('pbx-state').replaceChildren();const dot=document.createElement('i');dot.className='dot '+(statusStale?'loading':running?'online':'error');$('pbx-state').append(dot,document.createTextNode(statusStale?'状态暂不可用':running?'运行正常':'连接失败'));for(const [i,card]of [...$('extensions').children].entries()){const [state,label]=stateFor(rows[i]);card.querySelector('.dot').className='dot '+state;card.querySelector('.badge').textContent=label;}}
async function refreshStatus(){try{const value=await api('status');contacts=value.contacts||[];running=value.running;statusStale=false;}catch{statusStale=true;}paintStatus();}
async function load(){const data=await api('extensions');adopt(data);show('login',false);show('workspace',true);show('logout',true);await refreshStatus();}
async function connection(row){try{const data=await api('connection','POST',{number:row.number});connectionText='服务器：'+data.server+'\n端口：'+data.port+'\n传输：'+data.transport+'\n账号：'+data.username+'\n密码：'+data.password;$('connection-data').textContent=connectionText;$('copy-status').textContent='';$('connection').showModal();}catch(e){banner(e.message,true);}}
$('login-form').addEventListener('submit',async e=>{e.preventDefault();$('login-button').disabled=true;$('login-error').textContent='';try{const v=await api('login','POST',{key:$('key').value});csrf=v.csrf;$('key').value='';await load();}catch(e){$('login-error').textContent=e.message;}finally{$('login-button').disabled=false;}});
$('add').addEventListener('click',()=>{rows.push({key:++serial,number:'',name:'',enabled:true,ring:true,maxContacts:2,allowOutgoing:false,allowMessages:false,password:'',hasPassword:false});render();$('extensions').lastElementChild.querySelector('input').focus();});
$('refresh').addEventListener('click',async()=>{await refreshStatus();banner(statusStale?'状态刷新失败，草稿保持不变':'状态已刷新',statusStale);});
$('save').addEventListener('click',async()=>{const numbers=new Set();for(const row of rows){if(!/^\d{2,8}$/.test(row.number)||numbers.has(row.number)){banner('分机号须为不重复的 2–8 位数字',true);return;}numbers.add(row.number);const isNew=!model.extensions.some(saved=>saved.number===row.number);if((row.password||isNew||!row.hasPassword)&&!/^[A-Za-z0-9_-]{16,128}$/.test(row.password)){banner('新增或改号时请设置至少 16 位密码，或使用随机生成',true);return;}}if(rows.length===0){banner('请至少保留一个分机',true);return;}busy=true;render();banner('');try{const next=await api('extensions','PUT',{version:model.version,extensions:rows.map(({key,...row})=>row)});adopt(next);banner('分机配置已应用，原配置已备份');await refreshStatus();}catch(e){banner(e.message,true);}finally{busy=false;render();}});
$('logout').addEventListener('click',async()=>{try{await api('logout','POST',{});}finally{csrf='';model=null;rows=[];contacts=[];$('extensions').replaceChildren();show('workspace',false);show('login',true);show('logout',false);}});
$('close-connection').addEventListener('click',()=>{$('connection').close();$('connection-data').textContent='';connectionText='';});$('connection').addEventListener('close',()=>{$('connection-data').textContent='';connectionText='';});
$('copy-connection').addEventListener('click',async()=>{try{await navigator.clipboard.writeText(connectionText);$('copy-status').textContent='已复制；请勿分享给不可信的人';}catch{$('copy-status').textContent='浏览器不允许复制，请选中文本手动复制';}});
$('cancel-delete').addEventListener('click',()=>$('delete-dialog').close());$('confirm-delete').addEventListener('click',()=>{rows=rows.filter(row=>row.key!==deleteKey);$('delete-dialog').close();render();});
window.addEventListener('beforeunload',e=>{if(model&&changed()){e.preventDefault();e.returnValue='';}});
setInterval(()=>{if(csrf&&!busy)void refreshStatus();},15000);
api('session').then(v=>{csrf=v.csrf;return load();}).catch(()=>{});
}
void startManagementPage();
