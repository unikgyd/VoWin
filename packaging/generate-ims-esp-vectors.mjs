// Independent Node/OpenSSL oracle. Writes JSON to stdout only; no real traffic.
import {createCipheriv,createHmac} from 'node:crypto';
const ck=Buffer.from([...Array(16).keys()]);
const ik=Buffer.from([...Array(16).keys()].map(x=>x+16));
const payload=Buffer.from('IMS-ESP-contract');
const sum=bytes=>{let value=0;for(let i=0;i<bytes.length;i+=2)value+=(bytes[i]<<8)+(bytes[i+1]??0);return value;};
const check=value=>{while(value>65535)value=(value&65535)+(value>>>16);return (~value)&65535;};
const ip=(v6,last)=>v6?Buffer.from('20010db800000000000000000000000'+last,'hex'):Buffer.from([192,0,2,Number(last)]);
function make(v6,integrity,encryption,mutation='valid') {
 const source=ip(v6,'2'),destination=ip(v6,'1');
 let udp=Buffer.alloc(8+payload.length);udp.writeUInt16BE(41000);udp.writeUInt16BE(40001,2);udp.writeUInt16BE(udp.length,4);payload.copy(udp,8);
 let checksum=check(sum(source)+sum(destination)+17+udp.length+sum(udp));udp.writeUInt16BE(checksum||65535,6);
 if(mutation==='udp-checksum')udp[udp.length-1]^=1;
 if(mutation==='udp-length')udp.writeUInt16BE(udp.length+1,4);
 if(mutation==='udp-short')udp.writeUInt16BE(8,4);
 if(mutation==='zero-checksum')udp.writeUInt16BE(0,6);
 if(mutation==='selector')udp.writeUInt16BE(40000,2);
 const block=encryption==='aes-cbc'?16:encryption==='des-ede3-cbc'?8:4;
 const padding=(block-(udp.length+2)%block)%block;
 const trailer=Buffer.concat([udp,Buffer.from(Array.from({length:padding},(_,i)=>i+1)),Buffer.from([padding,17])]);
 if(mutation==='padding' && padding)trailer[udp.length]=99;
 if(mutation==='next-header')trailer[trailer.length-1]=6;
 const iv=encryption==='null'?Buffer.alloc(0):Buffer.from(Array.from({length:block},(_,i)=>i));
 let ciphertext=trailer;
 if(encryption!=='null') {const key=encryption==='aes-cbc'?ck:Buffer.concat([ck,ck.subarray(0,8)]);const cipher=createCipheriv(encryption==='aes-cbc'?'aes-128-cbc':'des-ede3-cbc',key,iv);cipher.setAutoPadding(false);ciphertext=Buffer.concat([cipher.update(trailer),cipher.final()]);}
 const prefix=Buffer.alloc(8);prefix.writeUInt32BE(2000);prefix.writeUInt32BE(mutation==='valid'?1:1000,4);
 const body=Buffer.concat([prefix,iv,ciphertext]);
 const key=integrity==='hmac-sha-1-96'?Buffer.concat([ik,Buffer.alloc(4)]):ik;
 const icv=createHmac(integrity==='hmac-sha-1-96'?'sha1':'md5',key).update(body).digest().subarray(0,12);
 const esp=Buffer.concat([body,icv]);let packet;
 if(v6) {const head=Buffer.alloc(40);head[0]=0x60;head.writeUInt16BE(esp.length,4);head[6]=50;head[7]=64;source.copy(head,8);destination.copy(head,24);packet=Buffer.concat([head,esp]);}
 else {const head=Buffer.alloc(20);head[0]=0x45;head.writeUInt16BE(20+esp.length,2);head.writeUInt16BE(mutation==='fragment'?0x2000:0x4000,6);head[8]=64;head[9]=50;source.copy(head,12);destination.copy(head,16);head.writeUInt16BE(check(sum(head)),10);packet=Buffer.concat([head,esp]);}
 if(mutation==='outer-checksum')packet[10]^=1;
 if(mutation==='icv')packet[packet.length-1]^=1;
 if(mutation==='trailing')packet=Buffer.concat([packet,Buffer.from([0])]);
 return {id:`${v6?'ipv6':'ipv4'}-${integrity}-${encryption}-${mutation}`,ipv6:v6,integrity,encryption,accepted:mutation==='valid'||(!v6&&mutation==='zero-checksum'),packetHex:packet.toString('hex')};
}
const cases=[];
for(const v6 of [false,true])for(const integrity of ['hmac-sha-1-96','hmac-md5-96'])for(const encryption of ['aes-cbc','des-ede3-cbc','null'])cases.push(make(v6,integrity,encryption));
for(const v6 of [false,true])for(const mutation of ['udp-checksum','udp-length','udp-short','zero-checksum','selector','padding','next-header','icv','trailing',...(v6?[]:['outer-checksum','fragment'])])cases.push(make(v6,'hmac-sha-1-96','null',mutation));
console.log(JSON.stringify({version:1,scope:'Synthetic IMS transport ESP; Node/OpenSSL independent oracle',ckHex:ck.toString('hex'),ikHex:ik.toString('hex'),payloadHex:payload.toString('hex'),cases},null,2));
