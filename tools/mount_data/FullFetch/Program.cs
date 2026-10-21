using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using TACTSharp;

if(args.Length<3)throw new ArgumentException("FullFetch lock targets output [--stdio]");
var protocol=Console.Out;Console.SetOut(Console.Error);
var lockPath=Path.GetFullPath(args[0]);var root=Path.GetFullPath(args[2]);
using var source=JsonDocument.Parse(File.ReadAllText(lockPath));var info=source.RootElement;
var version=info.GetProperty("Version").GetString()!;
if(info.GetProperty("Game").GetString()!="World of Warcraft Forever" || info.GetProperty("Product").GetString()!="wow_classic_beta" || !version.StartsWith("1.60."))throw new InvalidDataException("Wrong source");
var config=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(lockPath)!,"build-config.txt"));
if(Convert.ToHexStringLower(MD5.HashData(config))!=info.GetProperty("BuildConfig").GetString() || !System.Text.Encoding.UTF8.GetString(config).Contains(info.GetProperty("BuildName").GetString()!) || !info.GetProperty("BuildName").GetString()!.Contains("Forever"))throw new InvalidDataException("Wrong source config");
Directory.CreateDirectory(root);Directory.CreateDirectory(Path.Combine(root,"files"));Directory.CreateDirectory(Path.Combine(root,"file-receipts"));
var publicKeys=Path.Combine(Path.GetDirectoryName(lockPath)!,"TACT-keys.txt");if(File.Exists(publicKeys))foreach(var line in File.ReadAllLines(publicKeys)){var fields=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);if(fields.Length>=2)KeyService.SetKey(Convert.ToUInt64(fields[0],16),Convert.FromHexString(fields[1]));}
var build=new BuildInstance();build.Settings.Product="wow_classic_beta";build.Settings.Region=info.GetProperty("Region").GetString()!;
build.Settings.CacheDir=Path.Combine(Path.GetDirectoryName(lockPath)!,"cache");build.Settings.ForceHTTP1=true;
build.cdn.ProductDirectory=info.GetProperty("CDN").GetProperty("Path").GetString()!;
build.cdn.SetCDNs(info.GetProperty("CDN").GetProperty("Servers").GetString()!.Split(' ').Where(x=>Uri.TryCreate(x,UriKind.Absolute,out _)).Select(x=>new Uri(x).Host).Distinct().ToArray());
var old=new Dictionary<uint,JsonElement>();var oldPath=Path.Combine(root,"receipts.json");
if(File.Exists(oldPath))foreach(var receipt in JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(oldPath))!)old.TryAdd(receipt.GetProperty("FileDataID").GetUInt32(),receipt);
var fileLocks=new ConcurrentDictionary<uint,object>();var listfile=new Listfile();
object Fetch(Target target){
 uint id=target.FileDataID;
 if(id==0){var entries=build.Root!.GetEntriesByLookup(new Jenkins96().ComputeHash(target.Name));if(entries.Count>0)id=entries[0].fileDataID;else{if(!listfile.Initialized)listfile.Initialize(build.cdn,build.Settings);id=listfile.GetFDID(target.Name);}}
 if(id==0||!build.Root!.FileExists(id))throw new FileNotFoundException("Not in pinned build: "+target.Name);
 lock(fileLocks.GetOrAdd(id,_=>new object())){
  var file=Path.Combine(root,"files",id.ToString());var receiptPath=Path.Combine(root,"file-receipts",id+".json");
  var entries=build.Root.GetEntriesByFDID(id);var ckey=Convert.ToHexStringLower(entries[0].md5.AsSpan());
  byte[]? bytes=null;
  if(File.Exists(file)){
   if(!File.Exists(receiptPath))throw new IOException("Unverified existing output preserved: "+file);
   using var r=JsonDocument.Parse(File.ReadAllText(receiptPath));bytes=File.ReadAllBytes(file);
   if(r.RootElement.GetProperty("Version").GetString()!=version||Convert.ToHexStringLower(SHA256.HashData(bytes))!=r.RootElement.GetProperty("SHA256").GetString())throw new InvalidDataException("Existing output checksum mismatch");
  }else if(old.TryGetValue(id,out var previous)&&previous.GetProperty("Version").GetString()==version){
   var candidate=Path.Combine(root,previous.GetProperty("Output").GetString()!);if(File.Exists(candidate)){var data=File.ReadAllBytes(candidate);if(Convert.ToHexStringLower(SHA256.HashData(data))==previous.GetProperty("SHA256").GetString())bytes=data;}
  }
  if(bytes==null){
   if(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace<20L*1024*1024*1024)throw new IOException("Less than 20 GiB free; acquisition stopped");
   bytes=build.OpenFileByFDID(id);
  }
  if(bytes.Length==0||Convert.ToHexStringLower(MD5.HashData(bytes))!=ckey)throw new InvalidDataException("Decoded content does not match Blizzard content key: "+id);
  var receipt=new{FileDataID=id,Name=target.Name,Product="wow_classic_beta",Version=version,Bytes=bytes.Length,SHA256=Convert.ToHexStringLower(SHA256.HashData(bytes)),ContentKey=ckey,Output="files/"+id};
  if(!File.Exists(file)){File.WriteAllBytes(file+".partial",bytes);File.Move(file+".partial",file);}
  File.WriteAllText(receiptPath+".partial",JsonSerializer.Serialize(receipt));File.Move(receiptPath+".partial",receiptPath,true);
  return new{ok=true,requestId=target.RequestId,id,path=file,receipt};
 }
}
try{
 build.LoadConfigs(info.GetProperty("BuildConfig").GetString()!,info.GetProperty("CDNConfig").GetString()!);build.Load();
 if(args.Contains("--stdio")){
  protocol.WriteLine("WWF "+JsonSerializer.Serialize(new{ready=true,pid=Environment.ProcessId,version}));protocol.Flush();
  var pending=new List<Task>();var limit=new SemaphoreSlim(8);string? line;while((line=Console.ReadLine())!=null){var target=JsonSerializer.Deserialize<Target>(line)!;pending.Add(Task.Run(()=>{limit.Wait();object answer;try{answer=Fetch(target);}catch(Exception e){answer=new{ok=false,requestId=target.RequestId,error=e.Message};}finally{limit.Release();}lock(protocol){protocol.WriteLine("WWF "+JsonSerializer.Serialize(answer));protocol.Flush();}}));}Task.WaitAll(pending.ToArray());
 }else{
  var targets=JsonSerializer.Deserialize<List<Target>>(File.ReadAllText(args[1]))!.DistinctBy(x=>x.FileDataID==0?x.Name:x.FileDataID.ToString()).ToList();
  var done=0;var failed=new ConcurrentBag<object>();
  Parallel.ForEach(targets,new ParallelOptions{MaxDegreeOfParallelism=8},target=>{
   Exception? error=null;for(var attempt=0;attempt<3;attempt++){try{Fetch(target);error=null;break;}catch(Exception e){error=e;if(e.Message.Contains("preserved")||e.Message.Contains("20 GiB")||e.Message.Contains("Not in pinned"))break;Thread.Sleep(500*(attempt+1));}}
   if(error!=null)failed.Add(new{target.FileDataID,target.Name,error=error.Message});
   var count=Interlocked.Increment(ref done);if(count%50==0||count==targets.Count){lock(protocol){protocol.WriteLine("WWF "+JsonSerializer.Serialize(new{done=count,total=targets.Count,failed=failed.Count}));protocol.Flush();}}
  });
  File.WriteAllText(args[1]+".failures.json",JsonSerializer.Serialize(failed,new JsonSerializerOptions{WriteIndented=true}));
  if(!failed.IsEmpty)Environment.ExitCode=2;
 }
}finally{build.cdn.Dispose();}
record Target(uint FileDataID=0,string Name="",string? RequestId=null);
