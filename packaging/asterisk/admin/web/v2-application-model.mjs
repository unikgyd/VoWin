// Strict public application-state model. No HTTP, secrets, runtime or side effects.
const tokens=/^[A-Za-z0-9_-]{43}$/u;
const states=new Set(['prepared','stopping','installing','starting','applied','rolled-back','recovery-required']);
const pending=new Set(['stopping','installing','starting','recovery-required']);
const files=new Set(['asterisk','modules','logger','rtp','http','manager','pjsip','extensions','stasis','udptl','acl','indications','features','cel','cdr','ccss','pjproject'].map(name=>'config/'+name+'.conf')
 .concat(['gateway-client.json','deployment.normalized.json','sip-guard.nft']));
const fail=()=>{throw new Error('应用状态无效，请重新读取核对');};
const exact=(value,keys)=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&
 Object.keys(value).length===keys.length&&keys.every(key=>Object.hasOwn(value,key));
const token=value=>typeof value==='string'&&tokens.test(value);
export function adoptApplicationOverview(value){
 if(!exact(value,['schemaVersion','busy','operation','installed','installedState','statusSource','gatewayApplied','firewallApplied'])||
 value.schemaVersion!==1||typeof value.busy!=='boolean'||!['known','unknown','unconfirmed','drifted'].includes(value.installedState)||
 value.statusSource!=='journal'||value.gatewayApplied!==false||value.firewallApplied!==false)fail();
 let installed=null,operation=null;
 if(value.installed!==null){
  if(!exact(value.installed,['operation','revision'])||!token(value.installed.operation)||!token(value.installed.revision))fail();
  installed={operation:value.installed.operation,revision:value.installed.revision};
 }
 if(value.operation!==null){
  const op=value.operation;
  if(!exact(op,['operation','revision','state','fileCount','changedFiles','pbxApplied','gatewayApplied','firewallApplied','statusSource'])||
   !token(op.operation)||!token(op.revision)||!states.has(op.state)||op.fileCount!==20||
   !Array.isArray(op.changedFiles)||op.changedFiles.length>20||new Set(op.changedFiles).size!==op.changedFiles.length||
   !op.changedFiles.every(file=>typeof file==='string'&&files.has(file))||
   op.pbxApplied!==(op.state==='applied')||op.gatewayApplied!==false||op.firewallApplied!==false||op.statusSource!=='journal')fail();
  operation={...op,changedFiles:[...op.changedFiles]};
 }
 if(value.busy&&(operation!==null||installed!==null||value.installedState!=='unconfirmed'))fail();
 if(['known','drifted'].includes(value.installedState)&&installed===null)fail();
 if(value.installedState==='unknown'&&installed!==null)fail();
 if(operation&&pending.has(operation.state)&&value.installedState!=='unconfirmed')fail();
 if(!value.busy&&operation===null&&(installed!==null||value.installedState!=='unknown'))fail();
 if(value.installedState==='known'&&operation?.state==='applied'&&
   (installed.operation!==operation.operation||installed.revision!==operation.revision))fail();
 return {schemaVersion:1,busy:value.busy,operation,installed,installedState:value.installedState,
  statusSource:'journal',gatewayApplied:false,firewallApplied:false};
}
export function applicationPresentation(value,draftRevision,dirty){
 const view=adoptApplicationOverview(value);
 if(!token(draftRevision)||typeof dirty!=='boolean')fail();
 const state=view.operation?.state;
 const result={tone:'loading',title:'草稿尚未应用',detail:'尚无已安装版本记录；保存草稿不会修改 PBX。',installedRevision:view.installed?.revision??null,
  canPrepare:!dirty,canApply:false,canRecover:false,canReconcile:false};
 if(view.busy)return {...result,title:'应用任务进行中',detail:'任务持有配置锁；不要重复提交。',canPrepare:false};
 if(state&&pending.has(state))return {...result,tone:state==='recovery-required'?'error':'loading',
  title:'应用结果待确认',detail:'请检查操作记录后显式恢复；不要重复应用。',canPrepare:false,canRecover:true};
 if(view.installedState==='drifted')return {...result,tone:'error',title:'现用配置已被外部修改',
  detail:'已安装记录与现用文件不符；请先人工核对，不会覆盖外部修改。',canPrepare:false};
 if(view.installedState==='unconfirmed')return {...result,title:'应用记录待核对',
  detail:'运行验证与版本记录尚未对齐；先核对原操作，不要准备新版本。',canPrepare:false,canReconcile:state==='applied'||state==='rolled-back'};
 if(state==='rolled-back')return {...result,tone:'error',title:'上次应用已回滚',
  detail:view.installed?'已恢复先前配置；当前草稿没有生效。':'已恢复原配置，但没有可确认的原草稿版本。'};
 if(view.installedState==='known'&&view.installed.revision===draftRevision&&!dirty)
  return {...result,tone:'online',title:'PBX 配置已应用',detail:'配置指纹与最后验证记录一致；实时运行状态请看 PBX 状态。',canPrepare:false};
 if(state==='prepared'&&view.operation.revision===draftRevision&&!dirty)
  return {...result,title:'预检完成，等待应用',detail:'需要确认重启 PBX；网关与防火墙不会自动启用。',canApply:true};
 if(view.installed)return {...result,detail:'已有已验证的 PBX 配置；当前草稿尚未应用。'};
 return result;
}
