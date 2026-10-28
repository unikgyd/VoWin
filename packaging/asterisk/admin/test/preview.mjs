// Isolated UI preview. Never proxies to Asterisk, the management server or SIP.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
const root=new URL('../web/',import.meta.url);
let value={version:'synthetic-1',sipServer:'pbx.example.test',sipPort:5060,incomingTarget:'600',messagesEnabled:true,extensions:[
 {number:'1001',name:'桌面',enabled:true,ring:true,maxContacts:2,allowOutgoing:true,allowMessages:true,hasPassword:true},
 {number:'1002',name:'移动终端',enabled:true,ring:true,maxContacts:2,allowOutgoing:false,allowMessages:false,hasPassword:true}
]};
let revision=1;
createServer(async(req,res)=>{
 const url=new URL(req.url,'http://127.0.0.1');
 const reply=(v,status=200)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(v));};
 if(url.pathname==='/api/session'||url.pathname==='/api/login')return reply({csrf:'synthetic-preview-token'});
 if(url.pathname==='/api/logout')return reply({ok:true});
 if(url.pathname==='/api/status')return reply({running:true,contacts:[{number:'1002',address:'sip:1002@192.0.2.2:25000',state:'Avail'}]});
 if(url.pathname==='/api/connection')return reply({server:value.sipServer,port:5060,transport:'UDP',username:'synthetic-only',password:'not-a-real-password'});
 if(url.pathname==='/api/extensions'){
  if(req.method==='PUT'){
   if(process.argv.includes('--fail-save') || new URL(req.headers.referer || '/', 'http://127.0.0.1').searchParams.get('failSave')==='1')return reply({error:'合成失败：热加载未完成，原配置已恢复'},400);
   let body='';for await(const part of req)body+=part;
   const next=JSON.parse(body);value={...value,version:'synthetic-'+(++revision),extensions:next.extensions.map(({password,...row})=>({...row,hasPassword:true}))};
  }return reply(value);
 }
 const name=url.pathname==='/'?'index.html':url.pathname.slice(1);
 if(!['index.html','app.js','style.css'].includes(name)){res.writeHead(404);return res.end();}
 let b=await readFile(new URL(name,root));
 if(name==='index.html')b=Buffer.from(b.toString().replace('<main>','<main><p class="note">隔离 UI 预览 · 全部为合成账号，不连接 Asterisk、模组或 SIP</p>'));
 res.setHeader('Content-Type',{'index.html':'text/html; charset=utf-8','app.js':'text/javascript','style.css':'text/css'}[name]);res.end(b);
}).listen(5188,'127.0.0.1',()=>console.log('Synthetic PBX UI preview: http://127.0.0.1:5188'));
