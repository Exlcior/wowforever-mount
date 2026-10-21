const fs=require('node:fs'),path=require('node:path');
function writeJson(file,value){fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file+'.partial',JSON.stringify(value));fs.renameSync(file+'.partial',file);}
function writeMesh(file,positions,uv,indices,normals=null){
 if(!indices.length)return false;
 if(!normals){normals=new Float64Array(positions.length);for(let i=0;i<indices.length;i+=3){const a=indices[i]*3,b=indices[i+1]*3,c=indices[i+2]*3;const x=positions[b]-positions[a],y=positions[b+1]-positions[a+1],z=positions[b+2]-positions[a+2];const u=positions[c]-positions[a],v=positions[c+1]-positions[a+1],w=positions[c+2]-positions[a+2];const n=[y*w-z*v,z*u-x*w,x*v-y*u];for(const j of [a,b,c])for(let k=0;k<3;k++)normals[j+k]+=n[k];}}
 const used=new Map(),verts=[];const remap=[];
 for(const source of indices){let idx=used.get(source);if(idx===undefined){idx=verts.length;used.set(source,idx);verts.push(source);}remap.push(idx);}
 const out=Buffer.alloc(12+verts.length*32+remap.length*4);out.writeUInt32LE(0x31465757,0);out.writeUInt32LE(verts.length,4);out.writeUInt32LE(remap.length,8);let p=12;
 for(const source of verts){const i=source*3,j=source*2;const len=Math.hypot(normals[i],normals[i+1],normals[i+2])||1;const row=[positions[i],positions[i+1],positions[i+2],normals[i]/len,normals[i+1]/len,normals[i+2]/len,Number.isFinite(uv[j])?uv[j]:0,Number.isFinite(uv[j+1])?uv[j+1]:0];for(const value of row){if(!Number.isFinite(value))throw Error('Non-finite mesh');out.writeFloatLE(value,p);p+=4;}}
 for(const index of remap){out.writeUInt32LE(index,p);p+=4;}
 fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file+'.partial',out);fs.renameSync(file+'.partial',file);return {vertices:verts.length,triangles:indices.length/3};
}
function terrain(adt){
 if(adt.version!==18||adt.chunks.filter(Boolean).length!==256)throw Error('Incomplete ADT');
 const pos=[],uv=[],indices=[],lookup=new Map(),heights=[],UNIT=100/24,grid=UNIT/2;
 const maxX=Math.round(adt.chunks[0].position[0]/grid)*grid,maxY=Math.round(adt.chunks[0].position[1]/grid)*grid;
 const center=[maxX-800/3,maxY-800/3,0];let maxZ=-Infinity;
 for(const c of adt.chunks){const remap=[];let i=0;
  for(let row=0;row<17;row++)for(let col=0;col<(row%2?8:9);col++){
   const gx=Math.round((c.position[0]-row*UNIT/2)/grid),gy=Math.round((c.position[1]-(col+(row%2?0.5:0))*UNIT)/grid);
   const key=gx+','+gy,z=c.position[2]+c.vertices[i++];let index=lookup.get(key);
   if(index===undefined){index=pos.length/3;lookup.set(key,index);pos.push(gx*grid-center[0],gy*grid-center[1],z);uv.push((maxY-gy*grid)/(1600/3),(maxX-gx*grid)/(1600/3));heights.push([z]);}else heights[index].push(z);
   maxZ=Math.max(maxZ,z);remap.push(index);
  }
  for(let row=0;row<8;row++)for(let col=0;col<8;col++){
   const hole=c.flags&65536?(c.holesHighRes[row]>>col)&1:(c.holesLowRes>>(Math.floor(col/2)+Math.floor(row/2)*4))&1;if(hole)continue;
   const n=row*17+9+col;for(const v of [n,n-9,n+8,n,n-8,n-9,n,n+9,n-8,n,n+8,n+9])indices.push(remap[v]);
  }
 }
 for(let i=0;i<heights.length;i++){const h=heights[i];if(Math.max(...h)-Math.min(...h)>0.001)throw Error('Discontinuous terrain');pos[i*3+2]=h.reduce((a,b)=>a+b,0)/h.length;}
 return {positions:pos,uv,indices,center,spawnHeight:maxZ+80};
}
function sourceGeometry(vertices,normals,uv){const p=[],n=[];for(let i=0;i<vertices.length;i+=3){p.push(vertices[i+2],vertices[i],vertices[i+1]);if(normals)n.push(normals[i+2],normals[i],normals[i+1]);}return{positions:p,normals:n,uv};}
module.exports={writeJson,writeMesh,terrain,sourceGeometry};
