const fs=require('node:fs'),path=require('node:path');const{createReaders}=require('./readers.cjs');const{FetchSession}=require('./fetch_session.cjs');const{writeJson}=require('./geometry.cjs');
const[project,work,...requested]=process.argv.slice(2);
(async()=>{const session=await new FetchSession(project,work).start();let r;try{
 r=createReaders(path.join(project,'source/tools/wow.export'),path.join(work,'listfile.sqlite'),id=>session.fetch(id),work);
 const names=requested.length?requested:['GroundEffectDoodad','GroundEffectTexture','LiquidType','ChrRaces','CreatureDisplayInfo','CreatureDisplayInfoExtra','CreatureModelData','TextureFileData'];
 const result=[];
 for(const name of names){try{
  const id=r.listfile.getByFilename(`dbfilesclient/${name.toLowerCase()}.db2`);const schema=fs.readFileSync(path.join(project,'source/wow_metadata/definitions',name+'.dbd'));
  r.core.view.casc.cache={getFile:async()=>r.buffer(schema),storeFile:async()=>{}};
  r.core.view.casc.getVirtualFileByName=async()=>r.buffer(await session.fetch(id));
  const Reader=r.sandbox.require_WDCReader();const db=new Reader(name+'.db2');await db.parse();
  const rows=[...db.getAllRows().values()];writeJson(path.join(work,'tables',name+'.json'),rows);result.push({name,records:rows.length});
 }catch(e){result.push({name,error:e.message});}}
 writeJson(path.join(work,'tables-result.json'),result);console.log(JSON.stringify(result));
}finally{r?.close();await session.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
