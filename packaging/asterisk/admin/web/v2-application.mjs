import {adoptApplicationOverview,applicationPresentation} from './v2-application-model.mjs';

// Explicit maintenance UI only. The server binds all paths/commands and owns jobs.
export function createV2Application({api,getDraft,isDirty,onChange}){
 const $=id=>document.getElementById(id), ids=['prepare','apply','recover','reconcile'];
 const terminal=new Set(['succeeded','failed','uncertain']), token=/^[A-Za-z0-9_-]{43}$/u;
 let active=false,generation=0,envelope=null,stale=true,posting=false,needsRead=false,timer=null,dialogAction=null,request='';
 const exact=(value,keys)=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&Object.keys(value).length===keys.length&&keys.every(key=>Object.hasOwn(value,key));
 function invalid(){throw new Error('应用状态无效，请刷新核对');}
 function validJob(job){
  if(job===null)return null;
  if(!exact(job,['request','action','state','revision','operation','createdAt','updatedAt','result','errorCode'])||
   !token.test(job.request)||!['prepare','apply','recover'].includes(job.action)||
   !['queued','running','succeeded','failed','uncertain'].includes(job.state)||
   typeof job.revision!=='string'||(job.revision!==''&&!token.test(job.revision))||
   typeof job.operation!=='string'||(job.operation!==''&&!token.test(job.operation))||
   typeof job.errorCode!=='string'||!/^$|^[a-z][a-z-]{0,31}$/u.test(job.errorCode))invalid();
  for(const key of ['createdAt','updatedAt'])if(typeof job[key]!=='string'||!/^\d{4}-\d\d-\d\dT[\d:.]+Z$/u.test(job[key])||!Number.isFinite(Date.parse(job[key])))invalid();
  if(Date.parse(job.updatedAt)<Date.parse(job.createdAt))invalid();
  if(job.result!==null){
   const result=adoptApplicationOverview({schemaVersion:1,busy:false,operation:job.result,installed:null,
    installedState:['stopping','installing','starting','recovery-required'].includes(job.result?.state)?'unconfirmed':'unknown',
    statusSource:'journal',gatewayApplied:false,firewallApplied:false}).operation;
   if(result.operation!==job.operation||result.revision!==job.revision)invalid();
  }
  return {...job};
 }
 function adopt(value){
  if(!exact(value,['capability','overview','job'])||!exact(value.capability,['enabled'])||typeof value.capability.enabled!=='boolean')invalid();
  if(!value.capability.enabled&&(value.overview!==null||value.job!==null))invalid();
  return {capability:{enabled:value.capability.enabled},overview:value.overview===null?null:adoptApplicationOverview(value.overview),job:validJob(value.job)};
 }
 const working=()=>posting||needsRead||Boolean(envelope?.job&&!terminal.has(envelope.job.state))||envelope?.overview?.busy===true;
 function dotTitle(title,tone){
  $('application-title').replaceChildren();
  const dot=document.createElement('i');dot.className='dot '+tone;dot.setAttribute('aria-hidden','true');
  $('application-title').append(dot,document.createTextNode(title));
 }
 function render(){
  $('v2-application').classList.toggle('hidden',!active);
  for(const id of ids){$(`application-${id}`).disabled=true;$(`application-${id}`).classList.add('hidden');}
  if(!active)return;
  const draft=getDraft(),overview=envelope?.overview;
  let detail='请先读取应用记录，勿直接重发操作。';
  if(stale){dotTitle('应用记录暂不可读','loading');}
  else if(!envelope?.capability.enabled){dotTitle('PBX 应用入口未开启','loading');detail='服务器未启用网页应用；保存仍只更新草稿。';}
  else if(!overview){dotTitle('应用记录待刷新','loading');}
  else if(draft){
   const ui=applicationPresentation(overview,draft.revision,isDirty());
   dotTitle(ui.title,ui.tone);detail=ui.detail;
   if(isDirty())detail='有未保存修改。先保存草稿，再预检；不会把未保存内容应用到 PBX。';
   for(const [id,allowed]of [['prepare',ui.canPrepare],['apply',ui.canApply],['recover',ui.canRecover],['reconcile',ui.canReconcile]]){
    $(`application-${id}`).classList.toggle('hidden',!allowed);
    $(`application-${id}`).disabled=!allowed||working();
   }
  }
  $('application-detail').textContent=detail;
  $('application-version').textContent=overview?.installed
   ?'最后验证的草稿：'+overview.installed.revision.slice(0,10)+'… · 记录不代表实时在线'
   :'';
  const job=envelope?.job;
  const labels={queued:'任务已排队',running:'任务处理中',succeeded:'任务完成',failed:'任务失败',uncertain:'任务结果待确认'};
  $('application-job').textContent=job?labels[job.state]+(job.errorCode==='rolled-back'?'，已恢复原配置':'')+' · '+job.request.slice(0,8):'';
  $('application-refresh').disabled=posting;
  onChange?.();
 }
 function schedule(){
  clearTimeout(timer);timer=null;
  if(active&&envelope?.job&&!terminal.has(envelope.job.state))timer=setTimeout(()=>void refresh(),1500);
 }
 async function refresh(){
  if(!getDraft())return;
  active=true;const epoch=generation;
  try{
   let value;
   try{value=await api('v2/application'+(request?'?request='+request:''));}
   catch(error){
    // A lost POST may never have been admitted. Only read the latest ledger;
    // do not manufacture a second request or replay a mutation.
    if(request&&error.status===404)value=await api('v2/application');else throw error;
   }
   if(epoch!==generation||!active)return;
   envelope=adopt(value);stale=false;needsRead=false;
   if(envelope.job)request=envelope.job.request;
   $('application-error').textContent='';
  }catch(error){
   if(epoch!==generation||!active)return;
   stale=true;$('application-error').textContent=error.status===401?'登录已过期':'应用记录读取失败；任务可能仍在继续，请稍后刷新。';
  }
  render();schedule();
 }
 function open(action){
  if(stale||working()||!envelope?.capability.enabled||!getDraft())return;
  const ui=applicationPresentation(envelope.overview,getDraft().revision,isDirty());
  const allowed={prepare:ui.canPrepare,apply:ui.canApply,recover:ui.canRecover,reconcile:ui.canReconcile};
  if(!allowed[action])return;
  dialogAction=action;
  const texts={
   prepare:['预检已保存草稿？','将写入永久 v2 管理标记，阻止旧版应用器覆盖；预检不重启目标 PBX。'],
   apply:['应用到 PBX？','需要结束现有通话，将短暂重启 PBX 并验证完整配置。失败时尝试恢复原配置；不会自动启用网关或防火墙。'],
   recover:['恢复原配置？','将恢复该操作留存的原始配置，并可能重启 PBX。检测到外部修改时会拒绝覆盖。'],
   reconcile:['核对原操作记录？','仅重读并补齐原操作的版本记录；不会自动准备或应用新的草稿。']
  };
  $('application-dialog-title').textContent=texts[action][0];$('application-dialog-detail').textContent=texts[action][1];
  $('application-confirm').textContent=action==='prepare'?'确认接管并预检':action==='apply'?'确认应用':action==='recover'?'确认恢复':'确认核对';
  $('application-dialog').showModal();
 }
 function newRequest(){
  const bytes=new Uint8Array(32);crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes)).replaceAll('+','-').replaceAll('/','_').replaceAll('=','');
 }
 async function submit(){
  const action=dialogAction;dialogAction=null;$('application-dialog').close();
  if(!action||stale||working()||!envelope?.overview||!getDraft())return;
  const ui=applicationPresentation(envelope.overview,getDraft().revision,isDirty());
  if(!({prepare:ui.canPrepare,apply:ui.canApply,recover:ui.canRecover,reconcile:ui.canReconcile})[action])return;
  let actual=action,op=envelope.overview.operation;
  if(actual==='reconcile')actual=op.state==='applied'?'apply':'recover';
  const input={request:newRequest(),confirm:true};
  if(actual!=='recover')input.revision=action==='reconcile'?op.revision:getDraft().revision;
  if(actual!=='prepare')input.operation=op.operation;
  request=input.request;posting=true;needsRead=false;const epoch=generation;render();
  try{
   const response=await api('v2/application/'+actual,'POST',input);
   if(epoch!==generation||!active)return;
   envelope=adopt(response);stale=false;$('application-error').textContent='';
  }catch(error){
   if(epoch!==generation||!active)return;
   needsRead=true;
   $('application-error').textContent=error.status===400?'应用请求无效，请刷新后核对配置。':error.status===409
    ?'已有任务或版本已变化，请先刷新核对。':'提交结果待确认；任务可能仍在继续，请先刷新，不要重发。';
  }finally{
   if(epoch===generation&&active){posting=false;render();schedule();}
  }
 }
 $('application-refresh').addEventListener('click',()=>void refresh());
 for(const id of ids)$(`application-${id}`).addEventListener('click',()=>open(id));
 $('application-cancel').addEventListener('click',()=>{dialogAction=null;$('application-dialog').close();});
 $('application-confirm').addEventListener('click',()=>void submit());
 return {refresh,render,isBusy:working,expire(){
  ++generation;active=false;envelope=null;posting=false;needsRead=false;stale=true;request='';dialogAction=null;
  clearTimeout(timer);timer=null;if($('application-dialog').open)$('application-dialog').close();render();
 }};
}
