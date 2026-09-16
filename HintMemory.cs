using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
public static class HintMemory {
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint a,bool b,int p);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr r);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr r);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualProtectEx(IntPtr p,IntPtr a,UIntPtr n,uint v,out uint old);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool FlushInstructionCache(IntPtr p,IntPtr a,UIntPtr n);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr p);
 static readonly long[] rvas={0x3fc770,0x3fbeb0};
 static readonly byte[][] original={
  new byte[]{0x40,0x57,0x48,0x83,0xec,0x20,0x80,0x3d,0xc7,0xcc,0x72,0x01,0x00},
  new byte[]{0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20,0x80,0x3d,0x87}
 };
 static byte[] State(int site,int mode){
  var b=(byte[])original[site].Clone();
  if(mode==1 || (site==0 && mode==2)){b[0]=0xb0;b[1]=(byte)(mode==1?1:0);b[2]=0xc3;}
  return b;
 }
 static void Check(bool ok){if(!ok)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
 static byte[] Read(IntPtr h,IntPtr a){var b=new byte[13];UIntPtr n;Check(ReadProcessMemory(h,a,b,(UIntPtr)13,out n));if(n.ToUInt64()!=13)throw new Exception("内存读取不完整。");return b;}
 static void Write(IntPtr h,IntPtr a,byte[] b){
  uint old;Check(VirtualProtectEx(h,a,(UIntPtr)13,0x40,out old));
  try{UIntPtr n;Check(WriteProcessMemory(h,a,b,(UIntPtr)13,out n));if(n.ToUInt64()!=13)throw new Exception("内存写入不完整。");Check(FlushInstructionCache(h,a,(UIntPtr)13));if(!Read(h,a).SequenceEqual(b))throw new Exception("写入验证失败。");}
  finally{uint unused;Check(VirtualProtectEx(h,a,(UIntPtr)13,old,out unused));}
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
   if(hash!="be1a8c04fb5507617818054d5521245f60be2f780a8c2eb0a1d3cf488a670094")throw new Exception("版本不匹配：仅支持本次提供的社区版 DLL，未作修改。");
   var h=OpenProcess(0x438,false,p.Id);Check(h!=IntPtr.Zero);
   try{
    var addresses=rvas.Select(r=>new IntPtr(m.BaseAddress.ToInt64()+r)).ToArray();
    var previous=new byte[2][];
    for(int i=0;i<2;i++){
     previous[i]=Read(h,addresses[i]);int site=i;
     if(!Enumerable.Range(0,3).Any(j=>previous[site].SequenceEqual(State(site,j))))throw new Exception("补丁点 "+(i+1)+" 内容不符，未作修改。请重启游戏并关闭其他修改器。");
    }
    int attempted=-1;
    try{
     for(int i=0;i<2;i++){attempted=i;var desired=State(i,mode);if(!previous[i].SequenceEqual(desired))Write(h,addresses[i],desired);}
    }catch(Exception ex){
     bool restored=true;
     for(int i=attempted;i>=0;i--){try{Write(h,addresses[i],previous[i]);}catch{restored=false;}}
     throw new Exception(ex.Message+(restored?" 已回退本次修改。":" 回退未完成，请立即退出并重新启动游戏。"));
    }
    return new[]{"已恢复原始代码；重新进入关卡以刷新已缓存的显示状态。","已启用两处补丁。重新进入关卡，再用游戏内引导线按钮切换。","已禁用引导线使用许可，并恢复临时解锁判断；重新进入关卡生效。"}[mode];
   }finally{CloseHandle(h);}
  }
 }
}