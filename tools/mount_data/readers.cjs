/* Adapter for the pinned MIT wow.export readers. GUI startup is never executed. */
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {DatabaseSync}=require('node:sqlite');
function createReaders(appRoot,indexPath,getRaw,outputRoot){
 const manifest=JSON.parse(fs.readFileSync(path.join(appRoot,'package.json'),'utf8'));
 const source=fs.readFileSync(path.join(appRoot,'src/app.js'),'utf8');const end=source.indexOf('\n// src/app.js\n');
 if(manifest.version!=='0.2.19'||manifest.name!=='wow.export'||end<0)throw Error('Wrong reader bundle');
 const db=new DatabaseSync(indexPath,{readOnly:true});
 const byId=db.prepare('SELECT name FROM files WHERE id=?');const byName=db.prepare('SELECT id FROM files WHERE name=?');
 const listfile={getByID:id=>byId.get(Number(id))?.name,getByFilename:name=>byName.get(name.toLowerCase().replaceAll('\\','/'))?.id,
  getByIDOrUnknown(id,ext){return this.getByID(id)||`unknown/${id}${ext}`;},formatUnknownFile:(id,ext='')=>`unknown/${id}${ext}`};
 const sandbox={require,Buffer,console,setTimeout,clearTimeout,setImmediate,clearImmediate,
  process:{platform:process.platform,execPath:path.join(appRoot,'wow.export.exe'),env:{...process.env,APPDATA:process.env.APPDATA||path.join(require('node:os').homedir(),'AppData','Roaming')},versions:process.versions},
  nw:{__dirname:appRoot,App:{dataPath:outputRoot,manifest,argv:[]}},BUILD_RELEASE:true};sandbox.global=sandbox;
 vm.createContext(sandbox);vm.runInContext(source.slice(0,end),sandbox,{filename:'wow.export-readers',timeout:10000});
 const BW=sandbox.require_buffer();
 const {png}=require('./fast_png.cjs');
 sandbox.require_png_writer=()=>class FastPNGWriter{constructor(width,height){this.width=width;this.height=height;this.data=Buffer.alloc(width*height*4);}getPixelData(){return this.data;}getBuffer(){return new BW(png(this.width,this.height,this.data));}async write(file){return this.getBuffer().writeToFile(file);}};
 const config={exportDirectory:outputRoot,pathFormat:'posix',overwriteFiles:false,modelsExportTextures:true,modelsExportAlpha:true,
  enableSharedTextures:false,enableSharedChildren:false,modelsExportAnimations:false,modelsExportUV2:false,removePathSpaces:false};
 const casc={getFile:async id=>new BW(await getRaw(Number(id))),getFileByName:async name=>{const id=listfile.getByFilename(name);if(!id)throw Error('Unresolved filename '+name);return new BW(await getRaw(id));},getBuildName:()=> '1.60.1.70425'};
 const core={view:{config,casc},events:{on(){},emit(){}},setProgress(){}};
 sandbox.require_core=()=>core;sandbox.require_listfile=()=>listfile;sandbox.require_log=()=>({write(){}});
 return {sandbox,BW,core,listfile,close:()=>db.close(),buffer:bytes=>new BW(bytes),
  ADT:sandbox.require_ADTLoader(),WDT:sandbox.require_WDTLoader(),BLP:sandbox.require_blp()};
}
module.exports={createReaders};
