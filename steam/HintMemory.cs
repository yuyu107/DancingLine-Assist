using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
public static class HintMemory {
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint a,bool b,int p);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr r);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr r);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualProtectEx(IntPtr p,IntPtr a,UIntPtr n,uint v,out uint old);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool FlushInstructionCache(IntPtr p,IntPtr a,UIntPtr n);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr p);
 static readonly long[] rvas={0x6b8fa0,0x6bb5be,0x6c05ee,0x6c0c3e};
 static readonly byte[][] original={
  new byte[]{0x40,0x57,0x48,0x83,0xec,0x20,0x80,0x3d,0xe8,0x5f,0x91,0x01,0x00},
  new byte[]{0x40,0x0f,0xb6,0xd7},new byte[]{0x40,0x0f,0xb6,0xd7},new byte[]{0x40,0x0f,0xb6,0xd7}
 };
 const long HintPathSettingsClassSlot=0x1ec0370;
 const long HintPathControllerStaticOffset=0x10;
 const long HintEnabledOffset=0xc1;
 static byte[] State(int site,int mode){
  var b=(byte[])original[site].Clone();
  if(mode>0&&site==0){b[0]=0x57;b[1]=0x48;b[2]=0x83;b[3]=0xec;b[4]=0x20;b[5]=0xb0;b[6]=0x01;b[7]=0x48;b[8]=0x83;b[9]=0xc4;b[10]=0x20;b[11]=0x5f;b[12]=0xc3;}
  if(mode==2&&site>0){
   b[0]=0xb2;b[1]=0x01;b[2]=0x90;b[3]=0x90;
  }
  return b;
 }
 static void Check(bool ok){if(!ok)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
 static byte[] Read(IntPtr h,IntPtr a,int size){var b=new byte[size];UIntPtr n;Check(ReadProcessMemory(h,a,b,(UIntPtr)size,out n));if(n.ToUInt64()!=(ulong)size)throw new Exception("内存读取不完整。");return b;}
 static long Q(IntPtr h,long a){return BitConverter.ToInt64(Read(h,new IntPtr(a),8),0);}
 static string Text(IntPtr h,long a){
  if(a==0)return "";var bytes=new byte[96];int n=0;
  for(;n<bytes.Length;n++){var b=Read(h,new IntPtr(a+n),1)[0];if(b==0)break;bytes[n]=b;}
  return Encoding.UTF8.GetString(bytes,0,n);
 }
 static long Controller(IntPtr h,long module){
  long klass=Q(h,module+HintPathSettingsClassSlot);
  if(klass==0||Text(h,Q(h,klass+0x10))!="HintPathSettings")throw new Exception("引导线设置类校验失败。");
  long statics=Q(h,klass+0xb8);if(statics==0)return 0;
  long controller=Q(h,statics+HintPathControllerStaticOffset);if(controller==0)return 0;
  long controllerClass=Q(h,controller);
  if(controllerClass==0||Text(h,Q(h,controllerClass+0x10))!="HintPathSettingController")throw new Exception("引导线控制器校验失败。");
  return controller;
 }
 static int GuideState(IntPtr h,long module){
  long controller=Controller(h,module);if(controller==0)return -1;
  byte value=Read(h,new IntPtr(controller+HintEnabledOffset),1)[0];
  if(value>1)throw new Exception("引导线状态值异常，未作修改。");return value;
 }
 static void Write(IntPtr h,IntPtr a,byte[] b){
  uint old;Check(VirtualProtectEx(h,a,(UIntPtr)b.Length,0x40,out old));
  try{UIntPtr n;Check(WriteProcessMemory(h,a,b,(UIntPtr)b.Length,out n));if(n.ToUInt64()!=(ulong)b.Length)throw new Exception("内存写入不完整。");Check(FlushInstructionCache(h,a,(UIntPtr)b.Length));if(!Read(h,a,b.Length).SequenceEqual(b))throw new Exception("写入验证失败。");}
  finally{uint unused;Check(VirtualProtectEx(h,a,(UIntPtr)b.Length,old,out unused));}
 }
 public static int CurrentGuideState(){
  var ps=Process.GetProcessesByName("Dancing Line");if(ps.Length!=1)return -1;
  using(var p=ps[0]){var m=p.Modules.Cast<ProcessModule>().FirstOrDefault(x=>string.Equals(x.ModuleName,"GameAssembly.dll",StringComparison.OrdinalIgnoreCase));if(m==null)return -1;
   var h=OpenProcess(0x410,false,p.Id);if(h==IntPtr.Zero)return -1;
   try{return GuideState(h,m.BaseAddress.ToInt64());}catch{return -1;}finally{CloseHandle(h);}
  }
 }
 public static string Apply(int mode){
  if(mode<0||mode>2)throw new Exception("无效操作。");
  if(IntPtr.Size!=8)throw new Exception("请使用 64 位 PowerShell。");
  var ps=Process.GetProcessesByName("Dancing Line");
  if(ps.Length!=1)throw new Exception("请只启动一个 Dancing Line 游戏，并等待进入主菜单。");
  using(var p=ps[0]){
   var m=p.Modules.Cast<ProcessModule>().FirstOrDefault(x=>string.Equals(x.ModuleName,"GameAssembly.dll",StringComparison.OrdinalIgnoreCase));
   if(m==null)throw new Exception("游戏尚未加载 GameAssembly.dll。");
   string hash;
   using(var f=File.OpenRead(m.FileName))using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();
   if(hash!="3c77806c205d92b837f152c447f9fda708ea940e755b003b2bab90587f09ce6d")throw new Exception("版本不匹配：仅支持本次提供的Steam 版 DLL，未作修改。");
   var h=OpenProcess(0x438,false,p.Id);Check(h!=IntPtr.Zero);
   try{
    var addresses=rvas.Select(r=>new IntPtr(m.BaseAddress.ToInt64()+r)).ToArray();
    var previous=new byte[rvas.Length][];
    for(int i=0;i<rvas.Length;i++){
     previous[i]=Read(h,addresses[i],original[i].Length);int site=i;
     if(!Enumerable.Range(0,3).Any(j=>previous[site].SequenceEqual(State(site,j))))throw new Exception("补丁点 "+(i+1)+" 内容不符，未作修改。请重启游戏并关闭其他修改器。");
    }
    int attempted=-1;
    try{
     for(int i=0;i<rvas.Length;i++){attempted=i;var desired=State(i,mode);if(!previous[i].SequenceEqual(desired))Write(h,addresses[i],desired);}
    }catch(Exception ex){
     bool restored=true;
     for(int i=attempted;i>=0;i--){try{Write(h,addresses[i],previous[i]);}catch{restored=false;}}
     throw new Exception(ex.Message+(restored?" 已回退本次修改。":" 回退未完成，请立即退出并重新启动游戏。"));
    }
    return new[]{"已恢复引导线原始代码；重新进入关卡生效。","已解除引导线的关卡限制。进关后请手动开启引导线。","已解除限制并尝试在进关时默认开启。若只显示开启却没有路线，请在游戏内关闭再开启一次。"}[mode];
   }finally{CloseHandle(h);}
  }
 }
}
