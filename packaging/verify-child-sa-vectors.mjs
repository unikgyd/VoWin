// Independent HMAC oracle and a bounded grid oracle for TS union containment.
// No project imports, network, hardware or file writes. --emit prints fixtures.
import { createHmac } from 'node:crypto';
import { readFile } from 'node:fs/promises';
const hash = { 2: 'sha1', 5: 'sha256', 6: 'sha384', 7: 'sha512' }, authLength = { 1:16, 2:20, 12:32 };
const mac = (id,key,data) => createHmac(hash[id],key).update(data).digest(), hex = s => Buffer.from(s,'hex');
function expand(id,key,seed,length) { let out = Buffer.alloc(0), last = Buffer.alloc(0); for(let i=1;out.length<length;i++){last=mac(id,key,Buffer.concat([last,seed,Buffer.from([i])]));out=Buffer.concat([out,last]);}return out.subarray(0,length); }
function keymat(v) {const e=v.bits/8,a=authLength[v.integrity],s=expand(v.prf,hex(v.skd),Buffer.concat([hex(v.ni),hex(v.nr)]),2*(e+a));return [s.subarray(0,e),s.subarray(e,e+a),s.subarray(e+a,2*e+a),s.subarray(2*e+a)].map(v=>v.toString('hex'));}
function auth(v,side) {const p=mac(v.prf,hex(v.msk),Buffer.from('Key Pad for IKEv2'));return mac(v.prf,p,Buffer.concat([hex(v[side+'Transcript']),hex(v[side==='i'?'nr':'ni']),mac(v.prf,hex(v[side==='i'?'skpi':'skpr']),hex(v[side==='i'?'idi':'idr']))])).toString('hex');}
function ipv4(s){return Buffer.from(s.split('.').map(Number));}
function address(s){if(!s.includes(':'))return ipv4(s);let [lo,hi]=s.split('::');const left=lo?lo.split(':'):[],right=hi?hi.split(':'):[];const words=[...left,...Array(8-left.length-right.length).fill('0'),...right];const out=Buffer.alloc(16);words.forEach((w,i)=>out.writeUInt16BE(parseInt(w,16),i*2));return out;}
const selector=(start,end=start,protocol=0,p0=0,p1=65535)=>({start,end,protocol,p0,p1});
function ts(list){const parts=[Buffer.from([list.length,0,0,0])];for(const s of list){const first=address(s.start),last=address(s.end),head=Buffer.alloc(8);head[0]=first.length===4?7:8;head[1]=s.protocol;head.writeUInt16BE(8+2*first.length,2);head.writeUInt16BE(s.p0,4);head.writeUInt16BE(s.p1,6);parts.push(head,first,last);}return Buffer.concat(parts);}
function proposal(bits,integrity,changes={}) {
  const transforms=[[1,12,bits],[3,integrity],[5,0]].map(([kind,id,bits])=>{const b=Buffer.alloc(bits?12:8);b[4]=kind;b.writeUInt16BE(id,6);if(bits){b.writeUInt16BE(0x800e,8);b.writeUInt16BE(bits,10);}return b;});
  if(changes.omitEsn)transforms.pop();if(changes.extra)transforms.push(Buffer.from([0,0,0,8,changes.extra,0,0,1]));if(changes.esn)transforms[2].writeUInt16BE(1,6);
  if(changes.noAuth)transforms.splice(1,1);if(changes.duplicate)transforms.push(Buffer.from(transforms[0]));
  if(changes.keyOnAuth){const t=Buffer.alloc(12);transforms[1].copy(t);t.writeUInt16BE(12,2);t.writeUInt16BE(0x800e,8);t.writeUInt16BE(128,10);transforms[1]=t;}
  transforms.forEach((t,i)=>{t[0]=i+1===transforms.length?0:3;t.writeUInt16BE(t.length,2);});
  const spi=changes.spi??0x11223344,head=Buffer.alloc(12);head[4]=1;head[5]=changes.protocol??3;head[6]=4;head[7]=transforms.length;head.writeUInt32BE(spi,8);head.writeUInt16BE(12+transforms.reduce((n,v)=>n+v.length,0),2);return Buffer.concat([head,...transforms]);
}
function build(){
  const auths=[],keys=[];
  for(const prf of [2,5,6,7]){const n={2:20,5:32,6:48,7:64}[prf];const v={id:`prf${prf}`,prf,msk:'42'.repeat(64),skpi:'11'.repeat(n),skpr:'22'.repeat(n),ni:'33'.repeat(32),nr:'44'.repeat(32),idi:Buffer.concat([Buffer.from([3,0,0,0]),Buffer.from('synthetic@realm.test')]).toString('hex'),idr:Buffer.concat([Buffer.from([2,0,0,0]),Buffer.from('epdg.example.test')]).toString('hex'),iTranscript:Buffer.concat([Buffer.from('synthetic IKE_SA_INIT request with cookie'),Buffer.alloc(32,0x55)]).toString('hex'),rTranscript:Buffer.concat([Buffer.from('synthetic IKE_SA_INIT response'),Buffer.alloc(32,0x66)]).toString('hex')};auths.push({...v,iMic:auth(v,'i'),rMic:auth(v,'r')});for(const bits of [128,192,256])for(const integrity of [1,2,12]){const k={id:`prf${prf}-aes${bits}-auth${integrity}`,prf,bits,integrity,skd:'77'.repeat(n),ni:v.ni,nr:v.nr};keys.push({...k,keys:keymat(k)});}}
  const any4=selector('0.0.0.0','255.255.255.255'),any6=selector('::','ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff');
  const frames=[];const add=(id,list)=>frames.push({id,hex:ts(list).toString('hex'),accepted:true,selectors:list});
  add('ipv4-any',[any4]);add('ipv6-any',[any6]);add('dual',[any4,any6]);add('udp-port',[selector('10.0.0.1','10.0.0.1',17,5060,5060)]);add('opaque',[selector('10.0.0.1','10.0.0.9',17,65535,0)]);
  add('icmp-type-code',[selector('10.0.0.1','10.0.0.9',1,2048,2048)]);add('ipv6-narrow',[selector('2001:db8::1','2001:db8::ffff',6,100,200)]);
  for(const [id,mutate]of [['count-zero',b=>{b[0]=0;}],['unknown-type',b=>{b[4]=9;}],['wrong-length',b=>{b[7]=15;}],['too-many',b=>{b[0]=65;}],['reverse-address',b=>{b[12]=255;b[16]=0;}],['reverse-port',b=>{b[5]=17;b.writeUInt16BE(10,8);b.writeUInt16BE(9,10);}],['any-protocol-ports',b=>{b.writeUInt16BE(5,8);}]]){const b=ts([any4]);mutate(b);frames.push({id,hex:b.toString('hex'),accepted:false});}
  const reserved=ts([any4]);reserved[1]=0xff;frames.push({id:'reserved-ignored',hex:reserved.toString('hex'),accepted:true,selectors:[any4]});
  for(const count of [0,1,3,4,5,19])frames.push({id:`truncated-${count}`,hex:ts([any4]).subarray(0,count).toString('hex'),accepted:false});frames.push({id:'trailing',hex:Buffer.concat([ts([any4]),Buffer.from([0])]).toString('hex'),accepted:false});
  const scopes=[];const scope=(id,offer,chosen,accepted)=>scopes.push({id,offer:ts(offer).toString('hex'),chosen:ts(chosen).toString('hex'),accepted});
  scope('narrow-v4',[any4],[selector('10.0.0.1','10.0.0.9',17,100,200)],true);scope('wrong-family',[any4],[any6],false);
  scope('address-union',[selector('10.0.0.1','10.0.0.4',17,100,200),selector('10.0.0.5','10.0.0.9',17,100,200)],[selector('10.0.0.1','10.0.0.9',17,100,200)],true);
  scope('address-gap',[selector('10.0.0.1','10.0.0.4',17,100,200),selector('10.0.0.6','10.0.0.9',17,100,200)],[selector('10.0.0.1','10.0.0.9',17,100,200)],false);
  scope('port-union',[selector('10.0.0.1','10.0.0.9',17,100,150),selector('10.0.0.1','10.0.0.9',17,151,200)],[selector('10.0.0.1','10.0.0.9',17,100,200)],true);
  scope('port-gap',[selector('10.0.0.1','10.0.0.9',17,100,149),selector('10.0.0.1','10.0.0.9',17,151,200)],[selector('10.0.0.1','10.0.0.9',17,100,200)],false);
  scope('l-shaped-union',[selector('10.0.0.1','10.0.0.4',17,100,200),selector('10.0.0.5','10.0.0.9',17,100,150)],[selector('10.0.0.1','10.0.0.9',17,100,200)],false);
  scope('protocol-widen',[selector('10.0.0.1','10.0.0.9',6)],[selector('10.0.0.1','10.0.0.9')],false);scope('opaque-in-any',[any4],[selector('10.0.0.1','10.0.0.9',17,65535,0)],true);
  scope('opaque-not-full-port-union',[selector('10.0.0.1','10.0.0.9',17,0,32767),selector('10.0.0.1','10.0.0.9',17,32768,65535)],[selector('10.0.0.1','10.0.0.9',17)],false);
  scope('ipv6-max-no-overflow',[any6],[selector('ffff:ffff:ffff:ffff:ffff:ffff:ffff:fff0','ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff',17,100,200)],true);
  const proposals=[];for(const bits of [128,192,256])for(const integrity of [1,2,12])proposals.push({id:`aes${bits}-auth${integrity}`,hex:proposal(bits,integrity).toString('hex'),accepted:true,bits,integrity});
  proposals.push({id:'no-esn-default',hex:proposal(128,2,{omitEsn:true}).toString('hex'),accepted:true,bits:128,integrity:2});
  for(const [id,changes]of [['spi-zero',{spi:0}],['reserved-spi',{spi:255}],['ah',{protocol:2}],['dh-unoffered',{extra:4}],['prf-unoffered',{extra:2}],['unknown-transform',{extra:99}],['esn-unsupported',{esn:true}],['missing-auth',{noAuth:true}],['duplicate-encryption',{duplicate:true}],['key-on-auth',{keyOnAuth:true}]])proposals.push({id,hex:proposal(128,2,changes).toString('hex'),accepted:false});
  return {version:1,synthetic:true,auth:auths,keys,frames,scopes,proposals};
}
const expected=build();
function decodeTs(h){const b=hex(h),out=[];let at=4;for(let i=0;i<b[0];i++){const n=b[at]===7?4:16;out.push({family:n,protocol:b[at+1],p0:b.readUInt16BE(at+4),p1:b.readUInt16BE(at+6),lo:BigInt('0x'+b.subarray(at+8,at+8+n).toString('hex')),hi:BigInt('0x'+b.subarray(at+8+n,at+8+2*n).toString('hex'))});at+=8+2*n;}return out;}
function gridCovered(offers,chosen){
  for(const c of chosen){const relevant=offers.filter(o=>o.family===c.family);const points=new Set([c.lo,c.hi]);for(const o of relevant)for(const p of [o.lo-1n,o.lo,o.hi,o.hi+1n])if(p>=c.lo&&p<=c.hi)points.add(p);
    const portPoints=new Set([c.p0,c.p1]);for(const o of relevant)for(const p of [o.p0-1,o.p0,o.p1,o.p1+1])if(p>=c.p0&&p<=c.p1)portPoints.add(p);
    const opaque=c.p0===65535&&c.p1===0,ports=opaque?[]:[...portPoints];if(opaque||c.p0===0&&c.p1===65535)ports.push(null);
    for(const protocol of c.protocol?[c.protocol]:Array.from({length:256},(_,i)=>i))for(const ip of points)for(const port of ports)
      if(!relevant.some(o=>o.lo<=ip&&ip<=o.hi&&(!o.protocol||o.protocol===protocol)&&(port===null?(o.p0===0&&o.p1===65535||o.p0===65535&&o.p1===0):o.p0<=port&&port<=o.p1)))return false;
  }return true;
}
for(const c of expected.scopes)if(gridCovered(decodeTs(c.offer),decodeTs(c.chosen))!==c.accepted)throw Error(`Bad independent narrowing case ${c.id}`);
if(process.argv.includes('--emit'))process.stdout.write(JSON.stringify(expected,null,2)+'\n');
else{const c=JSON.parse(await readFile(new URL('../contracts/swu-child-v1.json',import.meta.url),'utf8'));if(JSON.stringify(c)!==JSON.stringify(expected))throw Error('Independent CHILD vector mismatch');process.stdout.write(`Independent AUTH: ${c.auth.length}; CHILD key schedules: ${c.keys.length}; TS frames: ${c.frames.length}; narrowing: ${c.scopes.length}; ESP proposals: ${c.proposals.length}\n`);}
