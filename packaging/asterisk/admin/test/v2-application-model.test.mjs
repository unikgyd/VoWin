import test from 'node:test';
import assert from 'node:assert/strict';
import {existsSync} from 'node:fs';
const target=new URL('../web/v2-application-model.mjs',import.meta.url);
const product=existsSync(target)?await import(target.href):{};
const token=letter=>letter.repeat(43);
const initial=()=>({schemaVersion:1,busy:false,operation:null,installed:null,installedState:'unknown',
 statusSource:'journal',gatewayApplied:false,firewallApplied:false});
const operation=(state='prepared',letter='B')=>({operation:token(letter),revision:token(letter),state,fileCount:20,
 changedFiles:['config/pjsip.conf'],pbxApplied:state==='applied',gatewayApplied:false,firewallApplied:false,statusSource:'journal'});
function api(){
 assert.equal(typeof product.adoptApplicationOverview,'function','Actual overview parser must exist');
 assert.equal(typeof product.applicationPresentation,'function','Actual installation-state presentation must exist');
 return product;
}
test('initial unknown record is not promoted to an installed revision',()=>{
 const p=api(), value=initial(), original=JSON.stringify(value), view=p.adoptApplicationOverview(value);
 assert.deepEqual(view,value);assert.equal(JSON.stringify(value),original);
 const ui=p.applicationPresentation(view,token('A'),false);
 assert.equal(ui.tone,'loading');assert.equal(ui.canApply,false);assert.equal(ui.canRecover,false);assert.equal(ui.canPrepare,true);
});
test('prepared draft B preserves installed A and offers explicit apply, never claims B live',()=>{
 const p=api(), view=p.adoptApplicationOverview({...initial(),operation:operation(),installed:{operation:token('A'),revision:token('A')},installedState:'known'});
 const ui=p.applicationPresentation(view,token('B'),false);
 assert.equal(ui.tone,'loading');assert.equal(ui.canApply,true);assert.equal(ui.canRecover,false);
 assert.equal(ui.installedRevision,token('A'));
 const dirty=p.applicationPresentation(view,token('B'),true);
 assert.equal(dirty.canPrepare,false);assert.equal(dirty.canApply,false);
});
test('known installed same revision is green but not an extra live health claim',()=>{
 const p=api(), view=p.adoptApplicationOverview({...initial(),operation:operation('applied','A'),
 installed:{operation:token('A'),revision:token('A')},installedState:'known'});
 const ui=p.applicationPresentation(view,token('A'),false);
 assert.equal(ui.tone,'online');assert.equal(ui.canApply,false);assert.equal(ui.canPrepare,false);
 assert.equal(ui.installedRevision,token('A'));
 assert.match(ui.detail,/记录/);
});
test('rollback B keeps A distinct and is never displayed as applied B',()=>{
 const p=api(), view=p.adoptApplicationOverview({...initial(),operation:operation('rolled-back'),
 installed:{operation:token('A'),revision:token('A')},installedState:'known'});
 const ui=p.applicationPresentation(view,token('B'),false);
 assert.equal(ui.tone,'error');assert.equal(ui.installedRevision,token('A'));assert.equal(ui.canApply,false);
});
test('pending operation and lock busy never offer replay or an installed-green state',()=>{
 const p=api();
 for(const state of ['stopping','installing','starting','recovery-required']){
  const view=p.adoptApplicationOverview({...initial(),operation:operation(state),installed:{operation:token('A'),revision:token('A')},installedState:'unconfirmed'});
  const ui=p.applicationPresentation(view,token('B'),false);
  assert.equal(ui.canPrepare,false);assert.equal(ui.canApply,false);assert.equal(ui.canRecover,true);
  assert.notEqual(ui.tone,'online');
 }
 const busy=p.adoptApplicationOverview({...initial(),busy:true,installedState:'unconfirmed'});
 const ui=p.applicationPresentation(busy,token('B'),false);
 assert.equal(ui.canPrepare,false);assert.equal(ui.canApply,false);assert.equal(ui.canRecover,false);
});
test('foreign current files and terminal index gaps require reconciliation, not a new prepare',()=>{
 const p=api();
 const drift=p.applicationPresentation(p.adoptApplicationOverview({...initial(),operation:operation('prepared'),
 installed:{operation:token('A'),revision:token('A')},installedState:'drifted'}),token('B'),false);
 assert.equal(drift.tone,'error');assert.equal(drift.canPrepare,false);assert.equal(drift.canApply,false);
 const gap=p.applicationPresentation(p.adoptApplicationOverview({...initial(),operation:operation('applied'),
 installed:{operation:token('A'),revision:token('A')},installedState:'unconfirmed'}),token('B'),false);
 assert.equal(gap.canPrepare,false);assert.equal(gap.canApply,false);assert.equal(gap.canRecover,false);
});
test('public schema and nested operation cannot smuggle secrets, arbitrary paths or success flags',()=>{
 const p=api(), malformed=[
 {...initial(),password:'SyntheticNeverRenderSecret'},
 {...initial(),schemaVersion:2},{...initial(),busy:1},{...initial(),installedState:'healthy'},
 {...initial(),gatewayApplied:true},{...initial(),firewallApplied:true},
 {...initial(),installed:{operation:token('A'),revision:token('A'),password:'SyntheticNeverRenderSecret'}},
 {...initial(),installed:{operation:'bad',revision:token('A')}},
 {...initial(),operation:{...operation(),target:'/private/config'}},
 {...initial(),operation:{...operation(),changedFiles:['../../private']}},
 {...initial(),operation:{...operation(),pbxApplied:true}},
 {...initial(),busy:true,operation:operation(),installedState:'unconfirmed'},
 {...initial(),installedState:'known'},
 ];
 for(const value of malformed)assert.throws(()=>p.adoptApplicationOverview(value),error=>{
  assert.ok(!String(error).includes('SyntheticNeverRenderSecret'));assert.ok(!String(error).includes('/private/config'));return true;
 });
});
