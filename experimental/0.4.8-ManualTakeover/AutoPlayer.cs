using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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
 bool manualControl,resumeRequested,f7WasDown,f7Queued;
 int landingIndex=-1; float landingClock;
 int alignmentIndex=-1;
 V3 lastGeometry; bool haveGeometry; long geometryMs,teleportUntil; readonly Stopwatch geometryWatch=Stopwatch.StartNew();
 Stopwatch airborneWatch; bool worldLayoutValid; bool playerBound;
 volatile bool invalidated; long levelNative,soundNative,sceneNameRef;
 volatile bool stop; volatile string status="尚未识别关卡。"; bool held; int next; float previous;
 readonly List<float> times=new List<float>(); readonly HashSet<long> visited=new HashSet<long>();
 readonly Dictionary<float,long> pointObjects=new Dictionary<float,long>();
 readonly Dictionary<long,bool> targetTypes=new Dictionary<long,bool>();
 readonly StringBuilder log=new StringBuilder(); readonly object sync=new object();
 string stage="idle"; readonly Dictionary<long,int> owners=new Dictionary<long,int>();
 int reads; Stopwatch scanClock; public string Status {get{return status;}}
 public int Count {get{return times.Count;}} public bool Running {get{return worker!=null&&worker.IsAlive;}}
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
   if(!float.IsNaN(t)&&!float.IsInfinity(t)&&t>0.005f&&t<3600){times.Add(t);pointObjects[t]=obj;if(!owners.ContainsKey(owner))owners[owner]=0;owners[owner]++;}
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
  Disconnect();invalidated=true;playerBound=false;player=0;level=0;times.Clear();visited.Clear();targetTypes.Clear();owners.Clear();pointObjects.Clear();reads=0;stage="attach";Log("SCAN BEGIN build=0.4.8");
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
   stage="optional player";player=TryQ(TryQ(TryQ(module+0x1a0e260)+0xb8)+8);
   Log("Player at scan="+player.ToString("X")+" type="+Name(player));
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
   if(times.Count<2)throw new Exception("未识别到足够的引导点；请导出日志，不会自动按键。");
   for(int i=1;i<times.Count;i++)if(times[i]-times[i-1]<0.04f)throw new Exception("时间点过密，实验版暂不支持，请导出日志。");
   Log("TIMES "+string.Join(",",times.Select(x=>x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
   levelNative=Q(level+0x10);soundNative=Q(sound+0x10);sceneNameRef=Q(level+0x180);
   if(!Ptr(levelNative)||!Ptr(soundNative))throw new Exception("关卡或音乐对象已卸载，请重新进入关卡后识别。");
   invalidated=false;Log("SELECTION nativeLevel="+levelNative.ToString("X")+" nativeSound="+soundNative.ToString("X")+" sceneRef="+sceneNameRef.ToString("X"));
   status="识别到 "+times.Count+" 个候选时间点（实验模式，需实测）。";Log(status);return status;
  }catch(Exception ex){times.Clear();status=ex.Message;Log("SCAN ERROR stage="+stage+" "+ex.ToString());throw;}
  finally{scanClock=null;}
 }
 float Clock(){stage="music clock";float t=(F(sound+0xac)-F(sound+0x60)+F(level+0x114))*F(level+0x28);if(float.IsNaN(t)||float.IsInfinity(t)||t< -120||t>7200)throw new Exception("游戏时间异常。");return t;}
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
 void Key(bool up){
  var input=new INPUT[]{new INPUT{type=1,scan=0x39,flags=(uint)(8|(up?2:0))}};
  uint n=SendInput(1,input,40);if(n!=1)throw new Exception("发送空格键失败；请检查游戏与工具权限是否一致。");held=!up;
 }
 bool SelectionValid(){
  if(invalidated||handle==IntPtr.Zero||times.Count<2)return false;
  try{
   if(process.HasExited||(playerBound&&(Q(Q(Q(module+0x1a0e260)+0xb8)+8)!=player||Q(player+0x30)!=level))||Q(manager+0x58)!=eventRoot||Q(level+0x10)!=levelNative||Q(sound+0x10)!=soundNative||Q(level+0x80)!=sound||Q(level+0x180)!=sceneNameRef){
    invalidated=true;status="关卡数据已变化或卸载，旧时间表已禁用，请重新识别。";Log("INVALIDATED selection changed");return false;
   }
   return true;
  }catch(Exception ex){invalidated=true;status="关卡数据暂不可读，旧时间表已禁用，请重新识别。";Log("INVALIDATED "+ex.Message);return false;}
 }
 bool BindPlayer(){
  if(playerBound)return true;
  long candidate=TryQ(TryQ(TryQ(module+0x1a0e260)+0xb8)+8);
  if(!Ptr(candidate)||Name(candidate)!="GameCharacter"||TryQ(candidate+0x30)!=level)return false;
  // Wait until actual gameplay before binding, so menu/player creation may finish safely.
  if(B(candidate+0x9c)==0||B(level+0x1a9)==0)return false;
  player=candidate;playerBound=true;Log("PLAYER BOUND "+player.ToString("X")+" level="+level.ToString("X"));return true;
 }
 public bool PollSelection(){if(Running||invalidated||times.Count<2)return false;return !SelectionValid();}
 bool IsStraightLanding(float clock,int index,V3 pos,V3 goal,V3 d,double len,double forward,double lateral){
  if(!SkipStraightLandingMarkers||landingIndex!=index||clock<landingClock||clock-landingClock>0.35f||index+1>=times.Count)return false;
  if(forward< -0.5||Math.Abs(lateral)>0.4||Math.Abs(goal.y-pos.y)>0.8)return false;
  float gap=times[index+1]-times[index];if(gap<0.08f||gap>1.5f)return false;
  long obj;if(!pointObjects.TryGetValue(times[index+1],out obj)||Q(obj+0x18)!=level)return false;
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
  if(clock<target-0.5f)return false;
  if(clock>target+0.5f)throw new Exception("等待当前位置与引导点对齐超时，已停止，请导出日志。");
  long hero=Q(player+0xc8),tr=Q(hero+0x170),native=Q(tr+0x10),obj;
  if(Name(tr)!="Transform"||ComponentTransform(hero)!=native)throw new Exception("角色坐标引用不一致。");
  if(!pointObjects.TryGetValue(times[index],out obj))throw new Exception("缺少对应引导点。");
  long hint=ComponentTransform(obj);
  if(I(hint+0x20)!=2||Name(Q(hint+0x28))!="Transform")throw new Exception("引导点坐标类型不匹配。");
  V3 pos=World(native),goal=World(hint),d=ReadV(Read(player+0x44,12),0);
  long now=geometryWatch.ElapsedMilliseconds;
  float observedSpeed=F(player+0x128);
  if(float.IsNaN(observedSpeed)||observedSpeed<=0||observedSpeed>100)throw new Exception("移动速度异常。");
  if(haveGeometry){
   double dt=Math.Max(0,(now-geometryMs)/1000.0),jx=pos.x-lastGeometry.x,jz=pos.z-lastGeometry.z;
   double moved=Math.Sqrt(jx*jx+jz*jz),limit=observedSpeed*Math.Sqrt(2)*dt+3.0;
   if(moved>limit){teleportUntil=now+100;Log("TELEPORT SUSPECT index="+index+" displacement="+moved+" expectedLimit="+limit+" from="+lastGeometry+" to="+pos);}
  }
  lastGeometry=pos;geometryMs=now;haveGeometry=true;
  if(now<teleportUntil)return false;
  double len=Math.Sqrt(d.x*d.x+d.z*d.z);
  if(len<0.1||Math.Abs(d.y)>0.1)throw new Exception("落地后的运动方向暂不受支持，请导出日志。");
  // A lower platform can be visible before the character leaves the current one.
  // Wait within the existing time window instead of stopping on height alone.
  if(Math.Abs(goal.y-pos.y)>2)return false;
  double dx=goal.x-pos.x,dz=goal.z-pos.z;
  double forward=(dx*d.x+dz*d.z)/len,lateral=(dx*d.z-dz*d.x)/len;
  float speed=F(player+0x128);
  if(float.IsNaN(speed)||speed<=0||speed>100)throw new Exception("移动速度异常。");
  // Ordinary diagonal movement: configurable input lead; does not shift the time window.
  double lead=Math.Min(0.4,speed*Math.Sqrt(2)*InputLeadMilliseconds/1000.0);
  if(Math.Abs(lateral)>1.0||forward< -0.7){
   if(alignmentIndex!=index){alignmentIndex=index;Log("ALIGNMENT WAIT index="+index+" clock="+clock+" deadline="+(target+0.5f)+" forward="+forward+" lateral="+lateral);}
   return false;
  }
  if(alignmentIndex==index){Log("ALIGNMENT READY index="+index+" clock="+clock+" forward="+forward+" lateral="+lateral);alignmentIndex=-1;}
  if(forward>lead)return false;
  if(B(player+0x108)==0||B(player+0x10b)==0||B(player+0x28)!=0)throw new Exception("到达转向位置但角色暂不能转向，已停止。");
  if(IsStraightLanding(clock,index,pos,goal,d,len,forward,lateral)){
   Log("LANDING PASS index="+index+" time="+times[index]+" clock="+clock+" forward="+forward+" lateral="+lateral+" reason=next_point_straight_after_landing");
   next=index+1;landingIndex=-1;alignmentIndex=-1;haveGeometry=false;return false;
  }
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
  manualControl=false;resumeRequested=false;f7Queued=false;f7WasDown=(GetAsyncKeyState(0x76)&0x8000)!=0;
  landingIndex=-1;alignmentIndex=-1;airborneWatch=null;haveGeometry=false;teleportUntil=0;stop=false;next=0;previous=Clock();while(next<times.Count&&times[next]+offsetMs/1000f<previous-0.04f)next++;
  worker=new Thread(()=>Loop(offsetMs)){IsBackground=true};worker.Start();
 }
 void PollF7(){bool down=(GetAsyncKeyState(0x76)&0x8000)!=0;if(down&&!f7WasDown&&Foreground())f7Queued=true;f7WasDown=down;}
 bool TryResume(float clock,int offsetMs){
  try{
   if(B(player+0x10b)==0||B(player+0x28)!=0||B(player+0x108)==0){Log("RESUME REFUSED not ready on ground");return false;}
   long hero=Q(player+0xc8),tr=Q(hero+0x170),native=Q(tr+0x10);
   if(Name(tr)!="Transform"||ComponentTransform(hero)!=native)return false;
   V3 pos=World(native),d=ReadV(Read(player+0x44,12),0);double len=Math.Sqrt(d.x*d.x+d.z*d.z);
   if(len<0.1||Math.Abs(d.y)>0.1)return false;
   // Find the earliest time-compatible point ahead on the current direction, not merely nearest in space.
   for(int i=0;i<times.Count;i++){
    float target=times[i]+offsetMs/1000f;if(target<clock-0.08f)continue;if(target>clock+2f)break;
    long obj;if(!pointObjects.TryGetValue(times[i],out obj)||Q(obj+0x18)!=level)continue;
    long ht=ComponentTransform(obj);if(I(ht+0x20)!=2||Name(Q(ht+0x28))!="Transform")continue;
    V3 goal=World(ht);double dx=goal.x-pos.x,dz=goal.z-pos.z;
    double forward=(dx*d.x+dz*d.z)/len,lateral=(dx*d.z-dz*d.x)/len;
    if(Math.Abs(goal.y-pos.y)>0.8||forward<0.5||Math.Abs(lateral)>0.35)continue;
    float speed=F(player+0x128);if(float.IsNaN(speed)||speed<=0||speed>100)return false;
    if(forward>speed*Math.Sqrt(2)*2.5)continue;
    next=i;previous=clock;landingIndex=-1;alignmentIndex=-1;airborneWatch=null;haveGeometry=false;teleportUntil=0;
    Log("RESUME MATCH index="+i+" clock="+clock+" target="+target+" forward="+forward+" lateral="+lateral);return true;
   }
   Log("RESUME REFUSED no upcoming aligned point clock="+clock+" hero="+pos);return false;
  }catch(Exception ex){Log("RESUME REFUSED "+ex.Message);return false;}
 }
 void Loop(int offsetMs){
  Log("START offsetMs="+offsetMs+" skipStraightLanding="+SkipStraightLandingMarkers+" inputLeadMs="+InputLeadMilliseconds);status="等待游戏前台并开始/继续关卡；F8 停止。";
  var heartbeat=Stopwatch.StartNew();long lastSample=-1000;string lastGate=null;
  try{
   while(!stop){
    if((GetAsyncKeyState(0x77)&0x8000)!=0){status="已按 F8 停止。";break;}
    if(process.HasExited){status="游戏已退出。";break;}
    if(!SelectionValid())break;
    PollF7();
    if(f7Queued){
     f7Queued=false;
     if(!manualControl){manualControl=true;resumeRequested=false;Log("MANUAL BEGIN next="+next);status="手动接管：自行收集钻石，回主路后按 F7 尝试恢复；F8 停止。";}
     else{resumeRequested=true;Log("RESUME REQUEST next="+next);}
    }
    if(!BindPlayer()){
     status="时间表已准备，等待手动开始并绑定玩家；F8 停止。";
     if(heartbeat.ElapsedMilliseconds-lastSample>=1000){Log("WAIT BIND level="+level.ToString("X")+" currentPlayer="+TryQ(TryQ(TryQ(module+0x1a0e260)+0xb8)+8).ToString("X"));lastSample=heartbeat.ElapsedMilliseconds;}
     Thread.Sleep(10);continue;
    }
    float t=Clock();
    if(t<previous-0.2f){landingIndex=-1;haveGeometry=false;teleportUntil=0;airborneWatch=null;next=0;while(next<times.Count&&times[next]+offsetMs/1000f<t-0.04f)next++;Log("Timeline reset at "+t);}
    previous=t;
    bool fg=Foreground();byte ps=B(player+0x9c),pp=B(player+0x9d),ls=B(level+0x1a9),lp=B(level+0x1ab);
    string gate=!fg?"游戏不在前台":ps==0?"角色 started=0":pp!=0?"角色 paused="+pp:ls==0?"关卡 started=0":lp!=0?"关卡 paused="+lp:"ready";
    if(gate!=lastGate||heartbeat.ElapsedMilliseconds-lastSample>=1000){
     Log("STATE foreground="+fg+" playerStarted="+ps+" playerPaused="+pp+" levelStarted="+ls+" levelPaused="+lp+" inputEnabled="+B(player+0x108)+" soundPaused="+B(sound+0xa8)+" musicTime="+F(sound+0xac)+" clock="+t+" next="+next+" target="+(next<times.Count?(times[next]+offsetMs/1000f).ToString():"end")+" gate="+gate);
     Motion("state",next);lastGate=gate;lastSample=heartbeat.ElapsedMilliseconds;
    }
    if(manualControl){
     if(resumeRequested&&gate=="ready"){
      resumeRequested=false;
      if(TryResume(t,offsetMs)){manualControl=false;status="已匹配后续路线，自动游玩恢复；F7 手动接管。";}
      else status="仍为手动：未匹配到前方主路线。调整位置和方向，再按 F7。";
     }
     if(manualControl){Thread.Sleep(5);continue;}
    }
    if(gate!="ready"){status="等待："+gate+"；游戏时间 "+t.ToString("F3")+"；F8 停止。";Thread.Sleep(5);continue;}
    status="运行中："+next+" / "+times.Count+"；游戏时间 "+t.ToString("F3")+"；F8 停止。";
    if(next>=times.Count){status="本次时间点已执行完毕；可重试或停止。";Thread.Sleep(10);continue;}
    float target=times[next]+offsetMs/1000f;
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
     while(pressWatch.ElapsedMilliseconds<40&&!stop&&Foreground()&&(GetAsyncKeyState(0x77)&0x8000)==0){PollF7();if(f7Queued)break;Thread.Sleep(1);}
     Key(true);
     Log("KEYUP index="+next+" heldMs="+pressWatch.ElapsedMilliseconds);
     Motion("after",next);
     Log("Key "+next+" gameTime="+t+" target="+target);next++;
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
