using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;

public sealed class AutoPlayer : IDisposable {
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint a,bool b,int c);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr r);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr p);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll")] static extern short GetAsyncKeyState(int k);
 [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint n,INPUT[] input,int size);
 [StructLayout(LayoutKind.Explicit,Size=40)] struct INPUT {
  [FieldOffset(0)] public uint type;
  [FieldOffset(8)] public ushort vk;
  [FieldOffset(10)] public ushort scan;
  [FieldOffset(12)] public uint flags;
 }
 IntPtr handle; Process process; long module,player,level,sound,manager,eventRoot; Thread worker;
 public int InputLeadMilliseconds { get; set; }
 public bool SkipStraightLandingMarkers { get; set; }
 public bool SkipTerminalStraightMarker { get; set; }
 int landingIndex=-1; float landingClock;
 int alignmentIndex=-1;
 V3 lastGeometry; bool haveGeometry; long geometryMs,teleportUntil; int teleportSkipIndex=-1; readonly Stopwatch geometryWatch=Stopwatch.StartNew();
 Stopwatch airborneWatch; bool worldLayoutValid; bool playerBound;
 volatile bool invalidated; long levelNative,soundNative,sceneNameRef;
 volatile bool stop,held,hasExecutedPoint; volatile string status="尚未识别关卡。"; bool bridgeTurn,hasScheduleProgress,scanHotkeyHeld,startHotkeyHeld; volatile int next; float previous,clockBias;
 readonly List<float> times=new List<float>(); readonly HashSet<long> visited=new HashSet<long>();
 readonly Dictionary<float,long> pointObjects=new Dictionary<float,long>();
 readonly Dictionary<float,List<long>> verticalAlternates=new Dictionary<float,List<long>>();
 readonly Dictionary<float,V3> recordedFlightPoints=new Dictionary<float,V3>();
 // Verified manual flight, sampled at each actual left-click on a grounded platform.
 // Apply only when the two source markers match this level's exact geometry.
 static readonly float[,] recordedFlightRoute=new float[,] {
  { 79.64482f, 44.94211f, -15.7949982f, 675.706543f },
  { 80.08098f, 48.5408478f, -15.9950008f, 679.4075f },
  { 80.92300f, 41.70341f, -17.495f, 686.5523f },
  { 81.28041f, 44.53099f, -17.6950016f, 689.585f },
  { 81.68629f, 41.0865555f, -17.895f, 693.029f },
  { 82.86149f, 50.8542366f, -22.1950016f, 703.0009f },
  { 83.27947f, 47.5126572f, -22.4250011f, 706.5476f },
  { 84.12150f, 54.5556068f, -23.915f, 713.692444f },
  { 84.50919f, 51.26544f, -24.1150017f, 716.9822f },
  { 84.88477f, 54.2472153f, -24.315f, 720.169f },
  { 85.67228f, 47.6669235f, -25.7650013f, 726.851257f },
  { 86.07815f, 51.0085f, -25.965f, 730.295166f },
  { 86.46584f, 47.82123f, -26.165f, 733.5849f },
  { 87.28969f, 54.5044327f, -27.795f, 740.575439f },
  { 87.68951f, 51.2142944f, -27.995f, 743.968f },
  { 88.08932f, 54.50437f, -28.1950016f, 747.360535f },
  { 88.88288f, 47.76984f, -29.715f, 754.0941f },
  { 89.71885f, 54.7613831f, -31.225f, 761.187561f },
  { 90.10654f, 51.47121f, -31.425f, 764.4773f },
  { 90.50030f, 54.6070328f, -31.625f, 767.818359f },
  { 90.93645f, 51.2140121f, -31.825f, 771.5193f },
  { 91.71185f, 57.69141f, -32.9095f, 778.097534f },
  { 92.09953f, 54.60693f, -32.9095f, 781.384949f },
  { 92.47511f, 57.7942162f, -32.9095f, 784.5695f },
  { 92.87492f, 54.606926f, -32.9095f, 787.9602f },
  { 93.30503f, 58.15405f, -32.9095f, 791.607239f },
  { 93.69273f, 54.96673f, -32.9095f, 794.8949f },
  { 94.08042f, 58.15406f, -32.9095f, 798.182556f },
 };
 readonly Dictionary<long,bool> targetTypes=new Dictionary<long,bool>();
 readonly StringBuilder log=new StringBuilder(); readonly object sync=new object();
 string stage="idle"; readonly Dictionary<long,int> owners=new Dictionary<long,int>();
 int reads; Stopwatch scanClock; public string Status {get{return status;}}
 public int Count {get{return times.Count;}} public bool Running {get{return worker!=null&&worker.IsAlive;}}
 public bool InputHeld {get{return held;}}
 public int CurrentPoint {get{return next;}}
 public int DisplayedPoint {get{return hasExecutedPoint?next:0;}}
 void Log(string s){lock(sync){
  if(log.Length>1800000){int cut=log.ToString().IndexOf('\n',600000);if(cut>=0){log.Remove(0,cut+1);log.Insert(0,"[Earlier log entries trimmed; latest events retained]\r\n");}}
  log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff")+" "+s);
 }}
 static bool Ptr(long p){return p>=65536&&p<0x800000000000L;}
 byte[] Read(long p,int n){
  if(!Ptr(p)||n<1||n>65536)throw new Exception("无效游戏地址：0x"+p.ToString("X")+"，阶段："+stage);
  if(scanClock!=null&&(++reads>500000||scanClock.ElapsedMilliseconds>15000))throw new Exception("识别达到读取上限。");
  var b=new byte[n];UIntPtr r;
  if(!ReadProcessMemory(handle,new IntPtr(p),b,(UIntPtr)n,out r)||r.ToUInt64()!=(ulong)n)throw new Exception("读取失败：0x"+p.ToString("X")+"，阶段："+stage+"，Win32="+Marshal.GetLastWin32Error());return b;
 }
 long Q(long p){return BitConverter.ToInt64(Read(p,8),0);} int I(long p){return BitConverter.ToInt32(Read(p,4),0);}
 float F(long p){return BitConverter.ToSingle(Read(p,4),0);} byte B(long p){return Read(p,1)[0];}
 long TryQ(long p){try{return Q(p);}catch{return 0;}}
 string Name(long p){
  try{long k=Q(p);if(Q(k+0x78)!=k)return null;return Text(Q(k+0x10));}catch{return null;}
 }
 // Some skins use a concrete GameCharacter subclass.  It keeps the inherited
 // gameplay layout, but an exact type-name check would otherwise leave the
 // scheduler permanently waiting without ever sending an input.
 bool IsSupportedPlayer(long p){
  string n=Name(p);return n=="GameCharacter"||n=="GameCharacterClassic";
 }
 long CurrentPlayer(){return TryQ(TryQ(TryQ(module+0x1a0e260)+0xb8)+8);}
 string Text(long p){var bytes=new List<byte>();for(int j=0;j<192;j++){byte c=B(p+j);if(c==0)return Encoding.UTF8.GetString(bytes.ToArray());bytes.Add(c);}return null;}
 bool IsHintPoint(long k){
  bool result;if(targetTypes.TryGetValue(k,out result))return result;
  result=false;
  try{
   long table=Q(k+0x98);
   for(int i=0;i<256;i++){
    long mi=Q(table+i*8);if(!Ptr(mi)||Q(mi+0x20)!=k)break;
    if(Q(mi)==module+0x3fe030){result=true;break;}
   }
  }catch{}
  targetTypes[k]=result;return result;
 }
 readonly Queue<long> pending=new Queue<long>();
 void Walk(long root,int unused){
  pending.Clear();pending.Enqueue(root);
  while(pending.Count>0){
   if(scanClock.ElapsedMilliseconds>15000||reads>490000||visited.Count>=12000||pending.Count>50000)throw new Exception("引导点扫描未完成，已拒绝使用部分时间表，请导出日志。");
   Visit(pending.Dequeue());
  }
  if(scanClock.ElapsedMilliseconds>15000||reads>490000)throw new Exception("引导点扫描达到上限，已拒绝使用部分时间表。");
  Log("TRAVERSAL COMPLETE objects="+visited.Count+" reads="+reads+" points="+times.Count);
 }
 void Visit(long obj){
  if(!Ptr(obj)||!visited.Add(obj))return;
  string name=Name(obj);if(name==null)return;
  long k=Q(obj);
  if(IsHintPoint(k)){
   float t=F(obj+0x4c);long owner=Q(obj+0x18);
   Log("Hint point type="+name+" time="+t.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" owner="+owner.ToString("X")+" ownerType="+Name(owner)+" matchesPlayerLevel="+(owner==level));
   if(!float.IsNaN(t)&&!float.IsInfinity(t)&&t>0.005f&&t<3600){long old;if(pointObjects.TryGetValue(t,out old)&&old!=obj){V3 a=World(ComponentTransform(old)),b=World(ComponentTransform(obj));double dx=a.x-b.x,dz=a.z-b.z,dy=a.y-b.y;if(Math.Sqrt(dx*dx+dz*dz)<0.5&&Math.Abs(dy)>5&&TryQ(old+0x18)==owner){List<long> alternatives;if(!verticalAlternates.TryGetValue(t,out alternatives)){alternatives=new List<long>{old};verticalAlternates[t]=alternatives;}alternatives.Add(obj);Log("VERTICAL ALTERNATE time="+t.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" first="+a+" second="+b);return;}if(Math.Abs(dx)+Math.Abs(dy)+Math.Abs(dz)>0.05){Log("AMBIGUOUS SAME TIME time="+t.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" first="+old.ToString("X")+" pos="+a+" second="+obj.ToString("X")+" pos="+b+" firstOwner="+TryQ(old+0x18).ToString("X")+" secondOwner="+owner.ToString("X")+" collected="+times.Count);throw new Exception("同时间引导点位置不同（"+t.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" 秒），已停止自动选择；请导出日志。");}return;}times.Add(t);pointObjects[t]=obj;if(!owners.ContainsKey(owner))owners[owner]=0;owners[owner]++;}
   return;
  }
  if(name.EndsWith("[]")){
   int n=I(obj+0x18);if(n<0||n>12000)throw new Exception("引导点列表长度异常。");
   for(int i=0;i<n;i++)pending.Enqueue(Q(obj+0x20+i*8));
   return;
  }
  // Find inherited delegate target / invocation-list fields by their runtime metadata.
  for(int parent=0;parent<5&&Ptr(k);parent++){
   long table=TryQ(k+0x80);if(Ptr(table))for(int i=0;i<128;i++){
    try{
     long entry=table+i*32;if(Q(entry+16)!=k)break;
     uint token=(uint)I(entry+28);if((token&0xff000000)!=0x04000000)break;
     string fn=Text(Q(entry));int offset=I(entry+24);
     if(offset<0x10||offset>0x200)continue;
     if(fn=="m_target"||fn=="_target"||fn=="target"||fn=="delegates"||fn=="_invocationList"||fn=="invocationList"){
      Log("Traverse "+name+"."+fn+" offset="+offset.ToString("X"));pending.Enqueue(Q(obj+offset));
     }
    }catch{break;}
   }
   k=TryQ(k+0x58);
  }
 }
 long Singleton(long rva){long mi=Q(module+rva);long klass=Q(Q(Q(mi+0x20)+0xc0));return Q(Q(klass+0xb8));}
 public string Scan(){
  if(Running)throw new Exception("请先停止自动游玩。");
  Disconnect();invalidated=true;playerBound=false;player=0;level=0;clockBias=0;hasScheduleProgress=false;hasExecutedPoint=false;next=0;times.Clear();visited.Clear();targetTypes.Clear();owners.Clear();pointObjects.Clear();verticalAlternates.Clear();recordedFlightPoints.Clear();reads=0;stage="attach";Log("SCAN BEGIN build=0.4.36-test");
  if(IntPtr.Size!=8)throw new Exception("需要 64 位 PowerShell。");
  var ps=Process.GetProcessesByName("Dancing Line");if(ps.Length!=1)throw new Exception("请只打开一个社区版游戏。");process=ps[0];
  var m=process.Modules.Cast<ProcessModule>().FirstOrDefault(x=>x.ModuleName.Equals("GameAssembly.dll",StringComparison.OrdinalIgnoreCase));
  if(m==null)throw new Exception("游戏尚未加载。");
  string hash;using(var f=File.OpenRead(m.FileName))using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();
  if(hash!="be1a8c04fb5507617818054d5521245f60be2f780a8c2eb0a1d3cf488a670094")throw new Exception("游戏版本不匹配。");
  module=m.BaseAddress.ToInt64();handle=OpenProcess(0x410,false,process.Id);
  if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  try{
   worldLayoutValid=false;
   var unityModule=process.Modules.Cast<ProcessModule>().FirstOrDefault(x=>x.ModuleName.Equals("UnityPlayer.dll",StringComparison.OrdinalIgnoreCase));
   if(unityModule!=null){using(var f=File.OpenRead(unityModule.FileName))using(var sha=SHA256.Create())worldLayoutValid=BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant()=="42e16d65341f4036ca6959e5dad65ae48fb94f464ec77c680e71c3fed5ac2ce2";}
   Log("WORLD layoutHashMatches="+worldLayoutValid);
   scanClock=Stopwatch.StartNew();
   stage="optional player";player=CurrentPlayer();
   Log("Player at scan="+player.ToString("X")+" type="+Name(player)+" playerLevel="+(IsSupportedPlayer(player)?TryQ(player+0x30):0).ToString("X"));
   stage="hint manager";manager=Singleton(0x1a1bf70);Log(stage+"="+manager.ToString("X")+" type="+Name(manager));
   stage="hint subscribers";eventRoot=Q(manager+0x58);Log(stage+"="+eventRoot.ToString("X"));
   Walk(eventRoot,0);
   stage="validate snapshot";
   if(Q(manager+0x58)!=eventRoot)throw new Exception("识别期间关卡或引导点列表变化，请重新识别。");
   foreach(var pair in owners)Log("OWNER "+pair.Key.ToString("X")+" type="+Name(pair.Key)+" points="+pair.Value);
   if(owners.Count!=1)throw new Exception("引导点所属关卡不唯一或尚未就绪，请等待加载完成再识别。");
   level=owners.Keys.First();
   if(!Ptr(level)||Name(level)!="LevelBase")throw new Exception("引导点尚未关联到有效关卡；请导出日志，无需故意死亡。");
   stage="hint owner soundtrack";sound=Q(level+0x80);
   if(!Ptr(sound))throw new Exception("关卡音乐尚未加载，请稍后重新识别。");
   Log("LEVEL SOURCE hint owner="+level.ToString("X")+" playerLevel="+(Ptr(player)?TryQ(player+0x30):0).ToString("X"));
   times.Sort();
   for(int i=times.Count-1;i>0;i--)if(Math.Abs(times[i]-times[i-1])<0.001f)times.RemoveAt(i);
   // Some custom levels leave a stale marker at the level start. It can be
   // timestamped only one frame after a real corner, even though the two
   // transforms are far apart. Keep genuine close double-turns, but discard
   // only this physically impossible time/position combination.
   if(worldLayoutValid)for(int i=times.Count-1;i>0;i--){
    float gap=times[i]-times[i-1];if(gap>=0.08f)continue;
    long earlier,later;if(!pointObjects.TryGetValue(times[i-1],out earlier)||!pointObjects.TryGetValue(times[i],out later))continue;
    try{
     V3 a=World(ComponentTransform(earlier)),b=World(ComponentTransform(later));
     double dx=a.x-b.x,dy=a.y-b.y,dz=a.z-b.z,span=Math.Sqrt(dx*dx+dy*dy+dz*dz);
     if(span>5.0){Log("DROP DENSE REMOTE time="+times[i].ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" previous="+times[i-1].ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" gap="+gap.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" span="+span);pointObjects.Remove(times[i]);times.RemoveAt(i);}
    }catch(Exception ex){Log("DENSE POINT GEOMETRY unavailable "+ex.Message);}
   }
   DropOpeningStartMarker();
   DropThirdLevelOriginPoint();
   ApplyRecordedFlightRoute();
   ApplyOpeningCorner();
   if(times.Count<2)throw new Exception("未识别到足够的引导点；请导出日志，不会自动按键。");
   // A nominal 0.04-second spacing may be represented as 0.03999996 in
   // single precision.  Treat only genuinely tighter pairs as unsupported.
   for(int i=1;i<times.Count;i++)if(times[i]-times[i-1]<0.03f)throw new Exception("时间点过密，实验版暂不支持，请导出日志。");
   Log("TIMES "+string.Join(",",times.Select(x=>x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
   levelNative=Q(level+0x10);soundNative=Q(sound+0x10);sceneNameRef=Q(level+0x180);
   if(!Ptr(levelNative)||!Ptr(soundNative))throw new Exception("关卡或音乐对象已卸载，请重新进入关卡后识别。");
   invalidated=false;Log("SELECTION nativeLevel="+levelNative.ToString("X")+" nativeSound="+soundNative.ToString("X")+" sceneRef="+sceneNameRef.ToString("X"));
   status="识别到 "+times.Count+" 个候选时间点（实验模式，需实测）。";Log(status);return status;
  }catch(Exception ex){times.Clear();status=ex.Message;Log("SCAN ERROR stage="+stage+" "+ex.ToString());throw;}
  finally{scanClock=null;}
 }
 void DropOpeningStartMarker(){
  if(!worldLayoutValid||times.Count<4||Math.Abs(times[0]-0.05144478f)>0.002f||
     Math.Abs(times[1]-2.380846f)>0.002f||Math.Abs(times[2]-2.711727f)>0.002f)return;
  float start=times[0];long obj;if(!pointObjects.TryGetValue(start,out obj))return;
  V3 p=World(ComponentTransform(obj));
  if(Math.Abs(p.x-0.4369972f)>0.15f||Math.Abs(p.y-0.85256f)>0.15f||Math.Abs(p.z-0.4365234f)>0.15f)return;
  Log("DROP OPENING START MARKER time="+start.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" pos="+p);
  pointObjects.Remove(start);times.RemoveAt(0);
 }
 void DropThirdLevelOriginPoint(){
  // This level's 2.749s hint stays at the origin, behind the running character.
  // Gate on its opening timestamps and measured coordinates to avoid other routes.
  if(!worldLayoutValid||times.Count<5||Math.Abs(times[0]-1.04532886f)>0.002f||Math.Abs(times[1]-1.53598642f)>0.002f||Math.Abs(times[2]-2.03732419f)>0.002f)return;
  float stale=2.74931979f;long obj;
  if(!pointObjects.TryGetValue(stale,out obj))return;
  V3 p=World(ComponentTransform(obj));
  if(Math.Abs(p.x-13.15)>0.15||Math.Abs(p.y-1.05)>0.15||Math.Abs(p.z)>0.15)return;
  Log("DROP VERIFIED ORIGIN POINT time="+stale.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" pos="+p);
  pointObjects.Remove(stale);times.Remove(stale);
 }
 void ApplyRecordedFlightRoute(){
  recordedFlightPoints.Clear();
  // The delegate snapshot varies: the inactive lower route's 84.849 marker
  // may be present or absent. Identify this route by the upper exit and the
  // first verified native point after the recorded section instead.
  long exit;
  if(!worldLayoutValid||!pointObjects.TryGetValue(78.44917f,out exit))return;
  V3 upper=World(ComponentTransform(exit));
  long resumedHint;
  if(!pointObjects.TryGetValue(94.84914f,out resumedHint))return;
  V3 resumed=World(ComponentTransform(resumedHint));
  if(Math.Abs(upper.x-54.7233f)>0.5||Math.Abs(upper.z-665.6633f)>0.5||
     Math.Abs(resumed.x-51.329f)>0.5||Math.Abs(resumed.y+33.35f)>1||Math.Abs(resumed.z-804.822f)>0.5)return;
  List<long> layers;
  bool upperAvailable=Math.Abs(upper.y+11.95f)<1;
  if(verticalAlternates.TryGetValue(78.44917f,out layers))upperAvailable=upperAvailable||layers.Any(p=>Math.Abs(World(ComponentTransform(p)).y+11.95f)<1);
  if(!upperAvailable)throw new Exception("飞行路线的上层出口引导点未加载完整，请重新识别，不会使用缺失的时间表。");
  for(int i=times.Count-1;i>=0;i--)if(times[i]>78.44917f&&times[i]<=94.1f)times.RemoveAt(i);
  for(int i=0;i<recordedFlightRoute.GetLength(0);i++){
   float t=recordedFlightRoute[i,0];V3 pos=new V3(recordedFlightRoute[i,1],recordedFlightRoute[i,2],recordedFlightRoute[i,3]);
   times.Add(t);recordedFlightPoints.Add(t,pos);
  }
  times.Sort();
  Log("RECORDED FLIGHT ROUTE points="+recordedFlightPoints.Count+"; native route resumes at 94.84914 goal="+resumed);
 }
 void ApplyOpeningCorner(){
  // The first visible marker of this level is the SECOND corner. Starting
  // along +X,+Z and then turning to -X,+Z, the missing corner is the
  // intersection of x=z with x+z=first.x+first.z.
  if(!worldLayoutValid||times.Count<2||Math.Abs(times[0]-0.9395832f)>0.002f)return;
  long obj;if(!pointObjects.TryGetValue(times[0],out obj))return;
  V3 first=World(ComponentTransform(obj));
  if(Math.Abs(first.x+1.025305f)>0.3f||Math.Abs(first.z-7.972629f)>0.3f||Math.Abs(first.y-0.1f)>0.5f)return;
  float corner=(first.x+first.z)*0.5f;
  if(corner<3f||corner>4f)throw new Exception("开局转向几何不一致，请导出日志。");
  float t=0.4095f;
  times.Add(t);times.Sort();recordedFlightPoints.Add(t,new V3(corner,first.y,corner));
  Log("OPENING CORNER restored time="+t+" position="+recordedFlightPoints[t]+" firstNative="+first);
 }
 long PointAtHeight(float time,V3 playerPosition){
  long chosen;if(!pointObjects.TryGetValue(time,out chosen))throw new Exception("缺少对应引导点。");
  List<long> alternatives;if(!verticalAlternates.TryGetValue(time,out alternatives))return chosen;
  double closest=double.MaxValue,second=double.MaxValue;long best=0;
  foreach(long candidate in alternatives){
   V3 location=World(ComponentTransform(candidate));double gap=Math.Abs(location.y-playerPosition.y);
   if(gap<closest){second=closest;closest=gap;best=candidate;}else if(gap<second)second=gap;
  }
  if(closest>5||second-closest<2)throw new Exception("角色高度无法确定当前引导点所在层，已停止；请导出日志。");
  return best;
 }
 float RawClock(){stage="music clock";float t=(F(sound+0xac)-F(sound+0x60)+F(level+0x114))*F(level+0x28);if(float.IsNaN(t)||float.IsInfinity(t)||t< -120||t>7200)throw new Exception("游戏时间异常。");return t;}
 float Clock(){return RawClock()+clockBias;}
 string Vec(long address){var b=Read(address,12);return string.Join(",",new[]{BitConverter.ToSingle(b,0),BitConverter.ToSingle(b,4),BitConverter.ToSingle(b,8)}.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).ToArray());}
 struct V3 {
  public float x,y,z;
  public V3(float a,float b,float c){x=a;y=b;z=c;}
  public override string ToString(){return x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+","+y.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+","+z.ToString("R",System.Globalization.CultureInfo.InvariantCulture);}
 }
 V3 ReadV(byte[] b,int o){return new V3(BitConverter.ToSingle(b,o),BitConverter.ToSingle(b,o+4),BitConverter.ToSingle(b,o+8));}
 V3 World(long native){
  long data=Q(native+0x38);int idx=I(native+0x40);
  long trs=Q(data+0x18),parents=Q(data+0x20);
  if(idx<0||idx>1000000)throw new Exception("Transform index invalid");
  V3 v=ReadV(Read(trs+idx*48L,12),0);int parent=I(parents+idx*4L);var seen=new HashSet<int>();
  while(parent>=0){
   if(parent>1000000||seen.Count>=128||!seen.Add(parent))throw new Exception("Transform parent chain invalid");
   byte[] b=Read(trs+parent*48L,48);V3 t=ReadV(b,0),q=ReadV(b,16),scale=ReadV(b,32);float w=BitConverter.ToSingle(b,28);
   float norm=q.x*q.x+q.y*q.y+q.z*q.z+w*w;
   if(float.IsNaN(norm)||Math.Abs(norm-1)>0.05f)throw new Exception("Transform quaternion invalid");
   float x=v.x*scale.x,y=v.y*scale.y,z=v.z*scale.z;
   float cx=2*(q.y*z-q.z*y),cy=2*(q.z*x-q.x*z),cz=2*(q.x*y-q.y*x);
   v=new V3(t.x+x+w*cx+q.y*cz-q.z*cy,t.y+y+w*cy+q.z*cx-q.x*cz,t.z+z+w*cz+q.x*cy-q.y*cx);
   parent=I(parents+parent*4L);
  }
  if(Q(native+0x38)!=data||I(native+0x40)!=idx)throw new Exception("Transform changed while sampling");
  if(float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z)||Math.Abs(v.x)>1000000||Math.Abs(v.y)>1000000||Math.Abs(v.z)>1000000)throw new Exception("World coordinates invalid");
  return v;
 }
 long ComponentTransform(long component){return Q(Q(Q(Q(component+0x10)+0x30)+0x30)+8);}
 void WorldMotion(string phase,int index){
  if(!worldLayoutValid)return;
  try{
   long hero=Q(player+0xc8),tr=Q(hero+0x170),native=Q(tr+0x10);
   if(Name(tr)!="Transform"||ComponentTransform(hero)!=native)throw new Exception("Hero transform paths disagree");
   long obj;if(index<0||index>=times.Count||!pointObjects.TryGetValue(times[index],out obj))return;
   long hint=ComponentTransform(obj);
   if(I(hint+0x20)!=2||Name(Q(hint+0x28))!="Transform")throw new Exception("Hint Transform type not confirmed");
   V3 pos=World(native),goal=World(hint),d=ReadV(Read(player+0x44,12),0);
   double len=Math.Sqrt(d.x*d.x+d.z*d.z);if(len<0.001)throw new Exception("Movement vector unavailable");
   double dx=goal.x-pos.x,dy=goal.y-pos.y,dz=goal.z-pos.z;
   Log("WORLD "+phase+" index="+index+" hero="+pos+" hint="+goal+" distance="+Math.Sqrt(dx*dx+dy*dy+dz*dz)+" forward="+((dx*d.x+dz*d.z)/len)+" lateral="+((dx*d.z-dz*d.x)/len));
  }catch(Exception ex){Log("WORLD unavailable "+ex.Message);}
 }
 void Motion(string phase,int index){
  WorldMotion(phase,index);
  try{
   string point="none";long obj;
   if(index>=0&&index<times.Count&&pointObjects.TryGetValue(times[index],out obj))point="hintRawVector20="+Vec(obj+0x20)+" hintTime="+F(obj+0x4c)+" timeTolerance="+F(obj+0x50)+" distanceTolerance="+F(obj+0x54);
   Log("MOTION "+phase+" index="+index+" direction="+I(player+0x104)+" moveVector="+Vec(player+0x44)+" grounded="+B(player+0x10b)+" jumping="+B(player+0x28)+" inputEnabled="+B(player+0x108)+" unusedTmpPoss="+Vec(player+0x12c)+" playerTime="+F(player+0x138)+" playerLatency="+F(player+0x38)+" targetLatency="+F(player+0x3c)+" speed="+F(player+0x128)+" musicTime="+F(sound+0xac)+" musicLatency="+F(sound+0x60)+" "+point);
  }catch(Exception ex){Log("MOTION unavailable "+ex.Message);}
 }
 bool Foreground(){uint id;GetWindowThreadProcessId(GetForegroundWindow(),out id);return id==(uint)process.Id;}
 // F6/F7 are polled by the Windows Forms timer. Edge detection avoids repeat
 // actions while a key remains held.
 public bool ConsumeScanHotkey(){
  bool down=(GetAsyncKeyState(0x75)&0x8000)!=0;
  bool pressed=down&&!scanHotkeyHeld;scanHotkeyHeld=down;return pressed;
 }
 public bool ConsumeStartHotkey(){
  bool down=(GetAsyncKeyState(0x76)&0x8000)!=0;
  bool pressed=down&&!startHotkeyHeld;startHotkeyHeld=down;return pressed;
 }
 void Key(bool up){
  var input=new INPUT[]{new INPUT{type=1,scan=0x39,flags=(uint)(8|(up?2:0))}};
  uint n=SendInput(1,input,40);if(n!=1)throw new Exception("发送空格键失败；请检查游戏与工具权限是否一致。");held=!up;
 }
 bool SelectionValid(){
  if(invalidated||handle==IntPtr.Zero||times.Count<2)return false;
  try{
   if(process.HasExited||(playerBound&&(CurrentPlayer()!=player||Q(player+0x30)!=level))||Q(manager+0x58)!=eventRoot||Q(level+0x10)!=levelNative||Q(sound+0x10)!=soundNative||Q(level+0x80)!=sound||Q(level+0x180)!=sceneNameRef){
    invalidated=true;status="关卡数据已变化或卸载，旧时间表已禁用，请重新识别。";Log("INVALIDATED selection changed");return false;
   }
   return true;
  }catch(Exception ex){invalidated=true;status="关卡数据暂不可读，旧时间表已禁用，请重新识别。";Log("INVALIDATED "+ex.Message);return false;}
 }
 bool BindPlayer(){
  if(playerBound)return true;
  long candidate=CurrentPlayer();
  if(!Ptr(candidate)||!IsSupportedPlayer(candidate)||TryQ(candidate+0x30)!=level)return false;
  // Wait until actual gameplay before binding, so menu/player creation may finish safely.
  if(B(candidate+0x9c)==0||B(level+0x1a9)==0)return false;
  float raw=RawClock(),characterTime=F(candidate+0x138),difference=characterTime-raw;
  // Most levels share the soundtrack clock. A few start movement roughly one
  // second before it, which otherwise makes the first turn impossible.
  if(!float.IsNaN(characterTime)&&!float.IsInfinity(characterTime)&&Math.Abs(difference)>=0.5f&&Math.Abs(difference)<=2.0f){
   clockBias=difference;Log("CLOCK CALIBRATION raw="+raw+" playerTime="+characterTime+" bias="+clockBias);
  }else{clockBias=0;Log("CLOCK CALIBRATION not needed raw="+raw+" playerTime="+characterTime+" difference="+difference);}
  player=candidate;playerBound=true;Log("PLAYER BOUND "+player.ToString("X")+" level="+level.ToString("X"));return true;
 }
 // When the player has deliberately left the scheduled route (for example to
 // collect a diamond) the soundtrack clock alone is no longer a trustworthy
 // resume cursor.  Re-anchor against the displayed route instead: find the
 // closest real hint transform, then use its successor if that marker has
 // already passed behind the current movement direction.
 bool TryResumeAnchor(out int selected){
  selected=-1;
  if(!worldLayoutValid||!playerBound||times.Count<2)return false;
  try{
   long hero=Q(player+0xc8),tr=Q(hero+0x170),native=Q(tr+0x10);
   if(Name(tr)!="Transform"||ComponentTransform(hero)!=native)throw new Exception("角色坐标引用不一致。");
   V3 pos=World(native),d=ReadV(Read(player+0x44,12),0);
   double len=Math.Sqrt(d.x*d.x+d.z*d.z);
   if(len<0.1)throw new Exception("运动方向暂不可用。");
   int best=-1;double bestScore=Double.MaxValue,bestDistance=0,bestForward=0,bestLateral=0;
   for(int i=0;i<times.Count;i++){
    long obj;if(!pointObjects.TryGetValue(times[i],out obj)||Q(obj+0x18)!=level)continue;
    long hint=ComponentTransform(obj);
    if(I(hint+0x20)!=2||Name(Q(hint+0x28))!="Transform")continue;
    V3 goal=World(hint);
    if(Math.Abs(goal.y-pos.y)>2.0)continue;
    double dx=goal.x-pos.x,dz=goal.z-pos.z;
    double distance=Math.Sqrt(dx*dx+dz*dz);
    double forward=(dx*d.x+dz*d.z)/len,lateral=(dx*d.z-dz*d.x)/len;
    // A marker slightly behind is still a useful anchor, but prefer an
    // equally close marker on the currently travelled branch.
    double score=distance+(forward<-0.35?2.0:0.0);
    if(score<bestScore){best=i;bestScore=score;bestDistance=distance;bestForward=forward;bestLateral=lateral;}
   }
   // If no actual route marker is reasonably close, retain the old cursor.
   // This is safer than guessing across a scene change or a different route.
   if(best<0||bestDistance>45.0){Log("RESUME ANCHOR unavailable nearest="+best+" distance="+bestDistance);return false;}
   selected=best;
   if(bestForward<-0.35&&selected+1<times.Count)selected++;
   Log("RESUME ANCHOR nearest="+best+" next="+selected+" distance="+bestDistance+" forward="+bestForward+" lateral="+bestLateral+" hero="+pos);
   return true;
  }catch(Exception ex){Log("RESUME ANCHOR unavailable "+ex.Message);return false;}
 }
 public bool PollSelection(){if(Running||invalidated||times.Count<2)return false;return !SelectionValid();}
 bool IsStraightLanding(float clock,int index,V3 pos,V3 goal,V3 d,double len,double forward,double lateral){
  if(!SkipStraightLandingMarkers||landingIndex!=index||clock<landingClock||clock-landingClock>0.35f||index+1>=times.Count)return false;
  if(recordedFlightPoints.ContainsKey(times[index])||recordedFlightPoints.ContainsKey(times[index+1]))return false;
  if(forward< -0.5||Math.Abs(lateral)>0.4||Math.Abs(goal.y-pos.y)>0.8)return false;
  float gap=times[index+1]-times[index];if(gap<0.08f||gap>1.5f)return false;
  long obj;obj=PointAtHeight(times[index+1],pos);if(Q(obj+0x18)!=level)return false;
  long tr=ComponentTransform(obj);
  if(I(tr+0x20)!=2||Name(Q(tr+0x28))!="Transform")return false;
  V3 follow=World(tr);double sx=follow.x-goal.x,sz=follow.z-goal.z,span=Math.Sqrt(sx*sx+sz*sz);
  if(span<0.8||Math.Abs(follow.y-goal.y)>0.8)return false;
  double cos=(sx*d.x+sz*d.z)/(span*len);
  double fx=follow.x-pos.x,fz=follow.z-pos.z;
  double ahead=(fx*d.x+fz*d.z)/len,side=(fx*d.z-fz*d.x)/len;
  Log("LANDING CHECK index="+index+" cosine="+cos+" nextAhead="+ahead+" nextLateral="+side+" gap="+gap);
  return cos>=0.995&&ahead>0.8&&Math.Abs(side)<=0.4;
 }
 bool PositionDue(float clock,float target,int index){
  if(B(player+0x10b)==0||B(player+0x28)!=0){
   if(airborneWatch==null){airborneWatch=Stopwatch.StartNew();Log("AIRBORNE WAIT index="+index+" clock="+clock);}
   if(airborneWatch.ElapsedMilliseconds>15000)throw new Exception("等待落地超过 15 秒，已停止，请导出日志。");
   return false;
  }
 if(airborneWatch!=null){landingIndex=airborneWatch.ElapsedMilliseconds>=100?index:-1;landingClock=clock;Log("LANDED index="+index+" clock="+clock+" waitMs="+airborneWatch.ElapsedMilliseconds);airborneWatch=null;}
  bool calibratedRoute=clockBias>=0.5f;
  bool beforeHintWindow=clock<target-0.5f;
  if(beforeHintWindow&&!calibratedRoute)return false;
  long hero=Q(player+0xc8),tr=Q(hero+0x170),native=Q(tr+0x10),obj;
  if(Name(tr)!="Transform"||ComponentTransform(hero)!=native)throw new Exception("角色坐标引用不一致。");
  V3 pos=World(native),goal;
  if(!recordedFlightPoints.TryGetValue(times[index],out goal)){
   obj=PointAtHeight(times[index],pos);long hint=ComponentTransform(obj);
   if(I(hint+0x20)!=2||Name(Q(hint+0x28))!="Transform")throw new Exception("引导点坐标类型不匹配。");
   goal=World(hint);
  }
  V3 d=ReadV(Read(player+0x44,12),0);
  long now=geometryWatch.ElapsedMilliseconds;
  float observedSpeed=F(player+0x128);
  if(float.IsNaN(observedSpeed)||observedSpeed<=0||observedSpeed>100)throw new Exception("移动速度异常。");
  if(haveGeometry){
   double dt=Math.Max(0,(now-geometryMs)/1000.0),jx=pos.x-lastGeometry.x,jz=pos.z-lastGeometry.z;
   double moved=Math.Sqrt(jx*jx+jz*jz),limit=observedSpeed*Math.Sqrt(2)*dt+3.0;
   if(moved>limit){teleportUntil=now+100;teleportSkipIndex=index;Log("TELEPORT SUSPECT index="+index+" displacement="+moved+" expectedLimit="+limit+" from="+lastGeometry+" to="+pos);}
  }
  lastGeometry=pos;geometryMs=now;haveGeometry=true;
  if(now<teleportUntil)return false;
  double len=Math.Sqrt(d.x*d.x+d.z*d.z);
  if(len<0.1||Math.Abs(d.y)>0.1)throw new Exception("落地后的运动方向暂不受支持，请导出日志。");
  // A lower platform can be visible before the character leaves the current one.
  // Wait within the existing time window instead of stopping on height alone.
  // This level's turn at 70.749s is recorded on the lower platform. The
  // character must turn at its horizontal corner before reaching that height.
  bool verifiedLowerCorner=index+1<times.Count&&times.Count>95&&Math.Abs(times[0]-2.380846f)<0.002f&&
   Math.Abs(times[index]-70.74983f)<0.002f&&Math.Abs(times[index+1]-72.09189f)<0.002f&&
   Math.Abs(goal.x+8.013403f)<0.2f&&Math.Abs(goal.y+22.800001f)<0.2f&&
   Math.Abs(goal.z-600.3323f)<0.2f&&Math.Abs(pos.y+19.9f)<0.6f;
  if(Math.Abs(goal.y-pos.y)>2&&!verifiedLowerCorner)return false;
  double dx=goal.x-pos.x,dz=goal.z-pos.z;
  double forward=(dx*d.x+dz*d.z)/len,lateral=(dx*d.z-dz*d.x)/len;
  // A teleport can consume its own marker automatically. After the scene
  // move, that old marker is often directly behind the character; retaining
  // it would make the alignment guard wait until timeout. Only skip it when
  // the teleport was just observed and it remains on the old route axis.
  if(teleportSkipIndex==index){
   teleportSkipIndex=-1;
   if(forward<-0.7&&Math.Abs(lateral)<=0.8){
    next=index+1;landingIndex=-1;alignmentIndex=-1;haveGeometry=false;
    Log("TELEPORT PASS index="+index+" next="+next+" forward="+forward+" lateral="+lateral);
    return false;
   }
  }
  // A final displayed box can be a finish-line decoration rather than an
  // input. If the last point remains on the current straight path, consume it
  // without pressing so the character continues into the goal.
  bool verifiedStraightFinish=times.Count==314&&Math.Abs(times[0]-0.9864139f)<0.002f&&
   Math.Abs(times[178]-62.75572f)<0.002f&&Math.Abs(times[179]-63.62782f)<0.002f&&
   Math.Abs(times[times.Count-1]-106.5869f)<0.002f;
  if((SkipTerminalStraightMarker||verifiedStraightFinish)&&index==times.Count-1&&clock>=target-0.2f&&forward>=0&&Math.Abs(lateral)<=0.4&&Math.Abs(goal.y-pos.y)<=1.0){
   next=index+1;landingIndex=-1;alignmentIndex=-1;haveGeometry=false;
   Log("TERMINAL STRAIGHT PASS index="+index+" forward="+forward+" lateral="+lateral+" verifiedRoute="+verifiedStraightFinish);
   return false;
  }
  // This level family begins movement before the hint timestamp stream.  When
  // that one-second start-clock calibration was confirmed, keep using world
  // geometry for this route instead of arbitrarily limiting the correction to
  // its first few markers.
  // Some custom route pieces contain an unmarked second bend.  If the next
  // known guide lies far to one side but its projection is the immediate
  // corner ahead, press once without consuming that next guide point.
  bool bridge=calibratedRoute&&beforeHintWindow&&Math.Abs(goal.y-pos.y)<=1.0&&
   Math.Abs(lateral)>=12.0&&forward>=0&&forward<=0.65;
  if(bridge){
   bridgeTurn=true;Log("CALIBRATED BRIDGE TURN index="+index+" clock="+clock+" target="+target+" forward="+forward+" lateral="+lateral);
   alignmentIndex=-1;return true;
  }
  // A few custom levels start with a corner before the soundtrack's first
  // normal hint timing.  The first marker is then intentionally to the side
  // of the starting segment, so ordinary alignment must not treat it as a
  // malformed route.  Keep this exception narrowly limited to that first,
  // very early cross-turn and require the character to be at its corner.
  bool initialCrossTurn=calibratedRoute&&!beforeHintWindow&&clock<=target+2.0f&&
   Math.Abs(goal.y-pos.y)<=1.0&&Math.Abs(lateral)>=2.0&&Math.Abs(forward)<=0.65;
  if(initialCrossTurn){
   Log("CALIBRATED CROSS TURN index="+index+" clock="+clock+" target="+target+" forward="+forward+" lateral="+lateral);
   alignmentIndex=-1;return true;
  }
  float deadline=target+(calibratedRoute?2.0f:0.5f);
  if(clock>deadline)throw new Exception("等待当前位置与引导点对齐超时，已停止，请导出日志。");
  float speed=F(player+0x128);
  if(float.IsNaN(speed)||speed<=0||speed>100)throw new Exception("移动速度异常。");
  // Ordinary diagonal movement: configurable input lead; does not shift the time window.
  double lead=Math.Min(0.4,speed*Math.Sqrt(2)*InputLeadMilliseconds/1000.0);
  // Small boundary allowance only for the current point within 0.5s of landing.
  bool recentLanding=landingIndex==index&&clock>=landingClock&&clock-landingClock<=0.5f;
  // The 62.755s box is a landing marker. The next box continues along the
  // current +X,+Z heading; turning at landing sends the character off the path.
  if(recentLanding&&index+1<times.Count&&times.Count>179&&
     Math.Abs(times[0]-0.9864139f)<0.002f&&Math.Abs(times[index]-62.75572f)<0.002f&&
     Math.Abs(times[index+1]-63.62782f)<0.002f&&
     Math.Abs(goal.x-13.08001f)<0.2f&&Math.Abs(goal.y+63.416f)<0.2f&&Math.Abs(goal.z-532.50006f)<0.2f&&
     forward>=-1.5&&forward<0&&Math.Abs(lateral)<0.4&&Math.Abs(goal.y-pos.y)<0.8){
   long followPoint=PointAtHeight(times[index+1],pos);
   V3 follow=World(ComponentTransform(followPoint));
   double fx=follow.x-goal.x,fz=follow.z-goal.z,span=Math.Sqrt(fx*fx+fz*fz);
   if(Math.Abs(follow.x-20.48000f)<0.2f&&Math.Abs(follow.y+63.416f)<0.2f&&Math.Abs(follow.z-539.9f)<0.2f&&
      span>5&&((fx*d.x+fz*d.z)/(span*len))>0.995){
    Log("VERIFIED LANDING PASS index="+index+" clock="+clock+" forward="+forward+" next="+(index+1));
    next=index+1;landingIndex=-1;alignmentIndex=-1;haveGeometry=false;return false;
   }
  }
  double lateralLimit=recentLanding?1.05:1.0;
  if(Math.Abs(lateral)>lateralLimit||forward< -0.7){
   if(alignmentIndex!=index){alignmentIndex=index;Log("ALIGNMENT WAIT index="+index+" clock="+clock+" deadline="+deadline+" calibratedRoute="+calibratedRoute+" forward="+forward+" lateral="+lateral);}
   return false;
  }
  if(alignmentIndex==index){Log("ALIGNMENT READY index="+index+" clock="+clock+" forward="+forward+" lateral="+lateral);alignmentIndex=-1;}
  if(forward>lead)return false;
  if(B(player+0x108)==0||B(player+0x10b)==0||B(player+0x28)!=0)throw new Exception("到达转向位置但角色暂不能转向，已停止。");
  if(IsStraightLanding(clock,index,pos,goal,d,len,forward,lateral)){
   Log("LANDING PASS index="+index+" time="+times[index]+" clock="+clock+" forward="+forward+" lateral="+lateral+" reason=next_point_straight_after_landing");
   next=index+1;landingIndex=-1;alignmentIndex=-1;haveGeometry=false;return false;
  }
  if(Math.Abs(lateral)>1.0)Log("LANDING EDGE ALLOW index="+index+" lateral="+lateral+" limit="+lateralLimit+" sinceLanding="+(clock-landingClock));
  Log("POSITION TRIGGER index="+index+" clock="+clock+" target="+target+" forward="+forward+" lateral="+lateral+" lead="+lead);
  return true;
 }
 public void Start(int offsetMs){
  if(Running)throw new Exception("自动游玩已经运行。");
  if(!worldLayoutValid)throw new Exception("位置模式需要匹配的 UnityPlayer 版本，请先识别。");
  if(invalidated||times.Count<2||handle==IntPtr.Zero)throw new Exception("请先识别当前关卡。");
  if(InputLeadMilliseconds<0||InputLeadMilliseconds>40)throw new Exception("按键提前量必须在 0 至 40 毫秒之间。");
  if(offsetMs< -500||offsetMs>100)throw new Exception("偏移超出范围。");
  if(!SelectionValid())throw new Exception(status);
  landingIndex=-1;alignmentIndex=-1;airborneWatch=null;haveGeometry=false;teleportUntil=0;teleportSkipIndex=-1;bridgeTurn=false;stop=false;
  bool resumeExisting=hasScheduleProgress&&playerBound&&next>=0&&next<times.Count;
  if(!resumeExisting)hasExecutedPoint=false;
  previous=Clock();
  if(resumeExisting){
   int anchored;
   if(TryResumeAnchor(out anchored)){next=anchored;Log("RESUME POSITION ANCHORED next="+next+" clock="+previous);}
   else Log("RESUME SAVED INDEX next="+next+" clock="+previous);
  }
  else{next=0;while(next<times.Count&&times[next]+offsetMs/1000f<previous-0.04f)next++;Log("START INDEX BY CLOCK next="+next+" clock="+previous);}
  hasScheduleProgress=true;
  worker=new Thread(()=>Loop(offsetMs)){IsBackground=true};worker.Start();
 }
 void Loop(int offsetMs){
  Log("START offsetMs="+offsetMs+" skipStraightLanding="+SkipStraightLandingMarkers+" skipTerminalStraight="+SkipTerminalStraightMarker+" inputLeadMs="+InputLeadMilliseconds);status="等待游戏前台并开始/继续关卡；F8 停止。";
  var heartbeat=Stopwatch.StartNew();long lastSample=-1000;string lastGate=null;
  try{
   while(!stop){
    if((GetAsyncKeyState(0x77)&0x8000)!=0){status="已按 F8 停止。";break;}
    if(process.HasExited){status="游戏已退出。";break;}
    if(!SelectionValid())break;
    if(!BindPlayer()){
     status="时间表已准备，等待手动开始并绑定玩家；F8 停止。";
     if(heartbeat.ElapsedMilliseconds-lastSample>=1000){long probe=CurrentPlayer();Log("WAIT BIND level="+level.ToString("X")+" currentPlayer="+probe.ToString("X")+" type="+Name(probe)+" playerLevel="+(IsSupportedPlayer(probe)?TryQ(probe+0x30):0).ToString("X"));lastSample=heartbeat.ElapsedMilliseconds;}
     Thread.Sleep(10);continue;
    }
    float t=Clock();
    if(t<previous-0.2f){landingIndex=-1;haveGeometry=false;teleportUntil=0;teleportSkipIndex=-1;airborneWatch=null;hasExecutedPoint=false;next=0;while(next<times.Count&&times[next]+offsetMs/1000f<t-0.04f)next++;Log("Timeline reset at "+t);}
    previous=t;
    bool fg=Foreground();byte ps=B(player+0x9c),pp=B(player+0x9d),ls=B(level+0x1a9),lp=B(level+0x1ab);
    string gate=!fg?"游戏不在前台":ps==0?"角色 started=0":pp!=0?"角色 paused="+pp:ls==0?"关卡 started=0":lp!=0?"关卡 paused="+lp:"ready";
    if(gate!=lastGate||heartbeat.ElapsedMilliseconds-lastSample>=1000){
     Log("STATE foreground="+fg+" playerStarted="+ps+" playerPaused="+pp+" levelStarted="+ls+" levelPaused="+lp+" inputEnabled="+B(player+0x108)+" soundPaused="+B(sound+0xa8)+" musicTime="+F(sound+0xac)+" clock="+t+" next="+next+" target="+(next<times.Count?(times[next]+offsetMs/1000f).ToString():"end")+" gate="+gate);
     Motion("state",next);lastGate=gate;lastSample=heartbeat.ElapsedMilliseconds;
    }
    if(gate!="ready"){status="等待："+gate+"；游戏时间 "+t.ToString("F3")+"；F8 停止。";Thread.Sleep(5);continue;}
    status="运行中："+next+" / "+times.Count+"；游戏时间 "+t.ToString("F3")+"；F8 停止。";
    if(next>=times.Count){status="本次时间点已执行完毕；可重试或停止。";Thread.Sleep(10);continue;}
    float target=times[next]+offsetMs/1000f;
    bridgeTurn=false;
    if(PositionDue(t,target,next)){
     // Recheck foreground immediately before the key-down.
     if(!Foreground())continue;
     var pressWatch=Stopwatch.StartNew();
     Log("KEYDOWN request index="+next+" clock="+t+" target="+target);
     // PositionDue sampled coordinates immediately before this input. Avoid extra pre-input reads.
     if(!SelectionValid()||!Foreground())break;
     Key(false);
     pressWatch.Restart();
     // Longer pulse to span more than one 60 Hz frame; stop/focus checked while held.
     while(pressWatch.ElapsedMilliseconds<40&&!stop&&Foreground()&&(GetAsyncKeyState(0x77)&0x8000)==0)Thread.Sleep(1);
     Key(true);
     Log("KEYUP index="+next+" heldMs="+pressWatch.ElapsedMilliseconds);
     Motion("after",next);
     bool keepCurrent=bridgeTurn;bridgeTurn=false;
     Log("Key "+next+" gameTime="+t+" target="+target+(keepCurrent?" bridge=keep_current":""));
     if(!keepCurrent)next++;else{landingIndex=-1;alignmentIndex=-1;haveGeometry=false;}
     hasExecutedPoint=true;
     status="自动游玩："+next+" / "+times.Count+"；F8 停止。";
    }
    Thread.Sleep(1);
   }
  }catch(Exception ex){status=ex.Message;Log("RUN ERROR "+ex.Message);}
  finally{if(held){try{Key(true);}catch{}}stop=true;Log("STOP "+status);}
 }
 public void Stop(){stop=true;if(worker!=null&&worker.IsAlive)worker.Join(1000);if(held){try{Key(true);}catch{}}status="自动游玩已停止。";}
 public void SaveLog(string path){lock(sync){File.WriteAllText(path,log.ToString(),Encoding.UTF8);}}
 void Disconnect(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}if(process!=null){process.Dispose();process=null;}}
 public void Dispose(){Stop();Disconnect();}
}

// Separate transparent topmost window for status only. It neither renders into
// Unity nor writes game memory, so turning it off simply hides this window.
public sealed class AssistOverlay : Form {
 [StructLayout(LayoutKind.Sequential)] struct RECT {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
 const int WS_EX_TRANSPARENT=0x20,WS_EX_TOOLWINDOW=0x80,WS_EX_NOACTIVATE=0x08000000;
 const uint SWP_NOACTIVATE=0x10,SWP_SHOWWINDOW=0x40;
 readonly Label title,state,details,keys,clickIcon;
 readonly Panel progressTrack,progressFill;
 public AssistOverlay(){
  FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;
  TopMost=true;BackColor=Color.Magenta;TransparencyKey=Color.Magenta;Size=new Size(400,114);
  var panel=new Panel();panel.Dock=DockStyle.Fill;panel.BackColor=Color.FromArgb(232,12,16,23);
  title=new Label();title.SetBounds(12,7,340,22);title.ForeColor=Color.FromArgb(102,220,255);
  title.Font=new Font("Microsoft YaHei UI",10F,FontStyle.Bold);title.Text="Dancing Line Assist";title.BackColor=Color.Transparent;
  state=new Label();state.SetBounds(12,31,340,20);state.Font=new Font("Microsoft YaHei UI",9F,FontStyle.Bold);state.BackColor=Color.Transparent;
  details=new Label();details.SetBounds(12,53,376,19);details.ForeColor=Color.FromArgb(230,235,242);
  details.Font=new Font("Microsoft YaHei UI",8.5F,FontStyle.Regular);details.BackColor=Color.Transparent;
  progressTrack=new Panel();progressTrack.SetBounds(12,78,376,8);progressTrack.BackColor=Color.FromArgb(53,64,77);
  progressFill=new Panel();progressFill.SetBounds(0,0,0,8);progressFill.BackColor=Color.FromArgb(84,213,159);
  progressTrack.Controls.Add(progressFill);
  keys=new Label();keys.SetBounds(12,91,376,17);keys.ForeColor=Color.FromArgb(160,175,190);keys.Text="F6 识别   ·   F7 启动/恢复   ·   F8 停止";
  keys.Font=new Font("Microsoft YaHei UI",8F,FontStyle.Regular);keys.BackColor=Color.Transparent;
  clickIcon=new Label();clickIcon.SetBounds(354,27,32,28);clickIcon.TextAlign=ContentAlignment.MiddleCenter;
  clickIcon.Font=new Font("Microsoft YaHei UI",16F,FontStyle.Bold);clickIcon.BackColor=Color.Transparent;
  panel.Controls.Add(title);panel.Controls.Add(state);panel.Controls.Add(details);panel.Controls.Add(progressTrack);panel.Controls.Add(keys);panel.Controls.Add(clickIcon);Controls.Add(panel);
 }
 protected override CreateParams CreateParams {get{CreateParams p=base.CreateParams;p.ExStyle|=WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE;return p;}}
 public void FollowGame(IntPtr game,int point,int count,bool running,bool inputHeld){
  RECT r;
  if(game==IntPtr.Zero||GetForegroundWindow()!=game||!GetWindowRect(game,out r)){if(Visible)Hide();return;}
  int width=400,height=114,x=r.Left+18,y=r.Top+105;
  if(r.Right-r.Left<width+36)x=r.Left+8;
  if(r.Bottom-r.Top<y-r.Top+height+8)y=r.Top+8;
  if(running){state.Text="自动游玩运行中";state.ForeColor=Color.FromArgb(107,232,160);}
  else if(count>=2){state.Text="自动游玩待命";state.ForeColor=Color.FromArgb(178,190,202);}
  else{state.Text="等待识别当前关卡";state.ForeColor=Color.FromArgb(178,190,202);}
  // The click indication changes only this small icon; all text remains
  // stable so brief internal state/log changes do not flash in recordings.
  clickIcon.Text=inputHeld?"◆":"●";
  clickIcon.ForeColor=inputHeld?Color.FromArgb(255,208,76):(running?Color.FromArgb(107,232,160):Color.FromArgb(115,130,145));
  int completed=Math.Max(0,Math.Min(point,count));
  int percent=count>0?(int)Math.Round(100.0*completed/count):0;
  details.Text="自动游玩进度："+completed+" / "+count+"   "+percent+"%";
  int filled=count>0?(int)Math.Round(376.0*completed/count):0;
  if(progressFill.Width!=filled)progressFill.Width=filled;
  Rectangle wanted=new Rectangle(x,y,width,height);bool moved=Bounds!=wanted;
  if(moved)Bounds=wanted;
  if(!Visible){Show();SetWindowPos(Handle,new IntPtr(-1),x,y,width,height,SWP_NOACTIVATE|SWP_SHOWWINDOW);}
  else if(moved)SetWindowPos(Handle,new IntPtr(-1),x,y,width,height,SWP_NOACTIVATE|SWP_SHOWWINDOW);
 }
 public void Disable(){if(Visible)Hide();}
}
