const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process'),readline=require('node:readline');
class FetchSession{
 constructor(project,work,tag='asset-fetch'){this.project=project;this.work=work;this.tag=tag;this.pending=new Map();this.next=0;this.exit=null;}
 async start(){
  const args=[path.join(this.work,'FullFetch/bin/Release/net10.0/FullFetch.dll'),path.join(this.project,'source/wow_metadata/source-lock.json'),path.join(this.work,'world-targets.json'),path.join(this.project,'source/wow_raw/1.60.1.70425'),'--stdio'];
  const log=fs.openSync(path.join(this.work,this.tag+'.stderr.log'),'a');this.child=spawn('dotnet',args,{cwd:this.project,windowsHide:true,stdio:['pipe','pipe',log]});fs.closeSync(log);
  this.identity={pid:this.child.pid,started:new Date().toISOString(),command:['dotnet',...args]};fs.writeFileSync(path.join(this.work,this.tag+'.process.json'),JSON.stringify(this.identity,null,2));
  this.exit=new Promise(resolve=>this.child.once('exit',(code)=>{for(const request of this.pending.values())request.reject(Error('Fetch worker exited: '+code));this.pending.clear();this.readyReject?.(Error('Fetch worker exited before ready: '+code));this.identity.ended=true;this.identity.exitCode=code;fs.writeFileSync(path.join(this.work,this.tag+'.process.json'),JSON.stringify(this.identity,null,2));resolve(code);}));
  await new Promise((resolve,reject)=>{
   this.readyReject=reject;this.child.once('error',reject);readline.createInterface({input:this.child.stdout}).on('line',line=>{if(!line.startsWith('WWF '))return;try{const data=JSON.parse(line.slice(4));if(data.ready){resolve(data);return;}const request=this.pending.get(data.requestId);if(request){this.pending.delete(data.requestId);data.ok?request.resolve(data):request.reject(Error(data.error));}}catch(e){reject(e);}});
  });return this;
 }
 async fetch(id,name=''){
  if(id){const file=path.join(this.project,'source/wow_raw/1.60.1.70425/files',String(id)),receipt=path.join(this.project,'source/wow_raw/1.60.1.70425/file-receipts',id+'.json');if(fs.existsSync(file)&&fs.existsSync(receipt))return fs.readFileSync(file);}
  const requestId=String(++this.next);const result=await new Promise((resolve,reject)=>{this.pending.set(requestId,{resolve,reject});this.child.stdin.write(JSON.stringify({FileDataID:id,Name:name,RequestId:requestId})+'\n');});return fs.readFileSync(result.path);
 }
 async close(){if(!this.child)return;this.child.stdin.end();const timeout=new Promise(resolve=>{this.timer=setTimeout(()=>{this.child.kill();resolve('terminated');},30000);});const code=await Promise.race([this.exit,timeout]);clearTimeout(this.timer);this.identity.ended=true;this.identity.exitCode=code;fs.writeFileSync(path.join(this.work,this.tag+'.process.json'),JSON.stringify(this.identity,null,2));}
}
module.exports={FetchSession};
