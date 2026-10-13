using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace SaiTl
{
  // 已验证可用的 SAI2 内存只读访问原样保留（OpenProcess VM_READ / VirtualQueryEx 扫描 / 逐行拼接）。
  public static class SN
  {
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a,bool i,int pid);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h,IntPtr a,byte[] b,int s,out int r);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern int GetLastError();
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, int len);
    [StructLayout(LayoutKind.Sequential)]
    public struct MBI { public IntPtr BaseAddress; public IntPtr AllocationBase; public uint AllocationProtect; public IntPtr RegionSize; public uint State; public uint Protect; public uint Type; }
  }

  public class Canvas
  {
    public const uint R = 0x0410;
    public IntPtr ph;
    public long TileMap;
    public int Pid;
    public int W,H,Cx,Cy;
    // 候选 tilemap：供 GUI 手动选择真实画布，避免自动挑到调色板/便签本。
    public class Candidate
    {
      public long TileMap; public int W,H,Cx,Cy,Score;
      public override string ToString(){ return W+"x"+H+"px (内容分 "+Score+")"; }
    }
    // 扫描全部候选并评分，返回按内容分从高到低排序的列表（Scan() 仍可自动回退）。
    public List<Candidate> ScanList(){
      var list=new List<Candidate>();
      try{
        const uint MEM_COMMIT=0x1000;
        byte[] chunk=new byte[1<<20];
        long addr=0;
        while(addr < (1L<<47)){
          SN.MBI m;
          if(SN.VirtualQueryEx(ph,new IntPtr(addr),out m,Marshal.SizeOf(typeof(SN.MBI)))==IntPtr.Zero) break;
          long sz=m.RegionSize.ToInt64();
          if(sz<=0){ addr+=0x1000; continue; }
          if(m.State==MEM_COMMIT){
            long protect=m.Protect&0xFF;
            bool readable = protect==0x04||protect==0x08||protect==0x02||protect==0x20||protect==0x40||protect==0x80;
            if(readable){
              for(long pos=addr; pos<addr+sz;){
                int toRead=(int)Math.Min(chunk.Length, addr+sz-pos);
                if(!RDB(pos,chunk,toRead)){ pos+=0x1000; continue; }
                for(int o=0;o+0x20<=toRead;o+=8){
                  int w=BitConverter.ToInt32(chunk,o+0x10);
                  int h=BitConverter.ToInt32(chunk,o+0x14);
                  int cx=BitConverter.ToInt32(chunk,o+0x18);
                  int cy=BitConverter.ToInt32(chunk,o+0x1c);
                  if(!(cx>=1&&cx<=64&&cy>=1&&cy<=64)) continue;
                  if(!(w>0&&h>0&&w<=cx*256&&h<=cy*256)) continue;
                  if(w<64||h<64) continue; if(w>8192||h>8192) continue;
                  // The candidate offset is relative to the current chunk, not
                  // the beginning of the whole virtual-memory region.
                  long tm=pos+o;
                  long tree=RD(tm+0x08);
                  if(tree==0) continue;
                  long saved=TileMap; TileMap=tm;
                  byte[] f=Grab();
                  TileMap=saved;
                  if(f==null) continue;
                  list.Add(new Candidate(){ TileMap=tm, W=w, H=h, Cx=cx, Cy=cy, Score=Score(f) });
                }
                pos+=toRead;
              }
            }
          }
          addr+=sz; if(addr<=0) break;
        }
        list.Sort((a,b)=> b.Score.CompareTo(a.Score));
        if(list.Count>12) list.RemoveRange(12,list.Count-12);
        return list;
      }catch{ return list; }
    }
    public bool Open(){
      try{ int pid=0; foreach(var p in Process.GetProcessesByName("sai2")) pid=p.Id; if(pid==0) return false; Pid=pid; ph=SN.OpenProcess(R,false,pid); return ph!=IntPtr.Zero; }catch{ return false; }
    }
    public void Close(){ try{ if(ph!=IntPtr.Zero){ SN.CloseHandle(ph); ph=IntPtr.Zero; } }catch{} }
    long RD(long a){ try{ byte[] b=new byte[8]; int r; if(!SN.ReadProcessMemory(ph,new IntPtr(a),b,8,out r)||r!=8) return 0; return BitConverter.ToInt64(b,0); }catch{ return 0; } }
    int RI(long a){ try{ byte[] b=new byte[4]; int r; if(!SN.ReadProcessMemory(ph,new IntPtr(a),b,4,out r)||r!=4) return 0; return BitConverter.ToInt32(b,0); }catch{ return 0; } }
    bool RDB(long a,byte[] b,int n){ try{ int r; return SN.ReadProcessMemory(ph,new IntPtr(a),b,n,out r)&&r==n; }catch{ return false; } }
    // 读取整幅画布，失败或超过 64MB 安全上限返回 null（逐行拼接，保留防中间黑修复）。
    public byte[] Grab(){
      try{
        W=RI(TileMap+0x10); H=RI(TileMap+0x14); Cx=RI(TileMap+0x18); Cy=RI(TileMap+0x1c); long tree=RD(TileMap+0x08);
        if(!(Cx>=1&&Cx<=64&&Cy>=1&&Cy<=64&&W>0&&H>0&&W<=Cx*256&&H<=Cy*256)) return null;
        if(tree==0) return null;
        int tw=Cx*256; long totalL=(long)tw*Cy*256*4; if(totalL>67108864) return null;
        int total=(int)totalL; byte[] full=new byte[total]; byte[] tile=new byte[262144];
        for(int y=0;y<Cy;y++){ long rp=RD(tree+y*8); if(rp==0) return null;
          for(int x=0;x<Cx;x++){ long tp=RD(rp+x*8); if(tp==0) return null;
            if(!RDB(tp,tile,262144)) return null;
            for(int r=0;r<256;r++){ Array.Copy(tile,r*1024,full,(y*256+r)*tw*4+x*256*4,1024); } } }
        return full;
      }catch{ return null; }
    }
    static int Score(byte[] f){
      try{
        long content=0, colored=0;
        for(int i=0;i<f.Length;i+=64){ byte b=f[i],g=f[i+1],r=f[i+2];
          if(r>30||g>30||b>30) content++;
          if(r!=g||g!=b||r!=b) colored++; }
        return (int)(colored*100+content);
      }catch{ return 0; }
    }
    // 已提交可读区域扫描定位活动画布 tilemap，返回最佳地址或 0（不依赖固定偏移）。
    public long Scan(){
      try{
        const uint MEM_COMMIT=0x1000;
        long best=0; int bestScore= -1;
        byte[] chunk=new byte[1<<20];
        long addr=0;
        while(addr < (1L<<47)){
          SN.MBI m;
          if(SN.VirtualQueryEx(ph,new IntPtr(addr),out m,Marshal.SizeOf(typeof(SN.MBI)))==IntPtr.Zero) break;
          long sz=m.RegionSize.ToInt64();
          if(sz<=0){ addr+=0x1000; continue; }
          if(m.State==MEM_COMMIT){
            long protect=m.Protect&0xFF;
            bool readable = protect==0x04||protect==0x08||protect==0x02||protect==0x20||protect==0x40||protect==0x80;
            if(readable){
              for(long pos=addr; pos<addr+sz;){
                int toRead=(int)Math.Min(chunk.Length, addr+sz-pos);
                if(!RDB(pos,chunk,toRead)){ pos+=0x1000; continue; }
                for(int o=0;o+0x20<=toRead;o+=8){
                  int w=BitConverter.ToInt32(chunk,o+0x10);
                  int h=BitConverter.ToInt32(chunk,o+0x14);
                  int cx=BitConverter.ToInt32(chunk,o+0x18);
                  int cy=BitConverter.ToInt32(chunk,o+0x1c);
                  if(!(cx>=1&&cx<=64&&cy>=1&&cy<=64)) continue;
                  if(!(w>0&&h>0&&w<=cx*256&&h<=cy*256)) continue;
                  if(w<64||h<64) continue; if(w>8192||h>8192) continue;
                  // The candidate offset is relative to the current chunk, not
                  // the beginning of the whole virtual-memory region.
                  long tm=pos+o;
                  long tree=RD(tm+0x08);
                  if(tree==0) continue;
                  long saved=TileMap; TileMap=tm;
                  byte[] f=Grab();
                  TileMap=saved;
                  if(f==null) continue;
                  int sc=Score(f);
                  if(sc>bestScore){ bestScore=sc; best=tm; }
                }
                pos+=toRead;
              }
            }
          }
          addr+=sz; if(addr<=0) break;
        }
        return best;
      }catch{ return 0; }
    }
    // FNV-1a 风格哈希：隔 4 字节采样，用于画布变化检测。
    public static ulong Hash(byte[] b){ unchecked{ ulong h=14695981039346656037UL; for(int i=0;i<b.Length;i+=4){ h^=b[i]; h*=1099511628211UL; } return h; } }
    // 读取 SAI2 主窗口标题，从中解析画布名（真实标题，不猜测内存结构）。
    // 从完整窗口标题解析出干净的画布名。
    // 典型标题: "<画布名> - PaintTool SAI Ver.2 (64bit) Preview.2024.08.14 1024x1024 - 250%"
    // 取 "-" 之前的部分，去掉未保存标记与常见数字块。
    public string ParseCanvasName(string title){
      if(string.IsNullOrEmpty(title)) return "canvas";
      string t=title;
      int dash=t.IndexOf('-');
      if(dash>0) t=t.Substring(0,dash).Trim();
      t=t.Trim().TrimStart('*','＊',' ').Trim();
      if(t.Length==0) return "canvas";
      var parts=new System.Collections.Generic.List<string>();
      foreach(var tok in t.Split(' ')){
        string s=tok.Trim();
        if(s.Length==0) continue;
        if(System.Text.RegularExpressions.Regex.IsMatch(s,@"^\d+[xX]\d+$")) continue;
        if(System.Text.RegularExpressions.Regex.IsMatch(s,@"^\d+%$")) continue;
        parts.Add(s);
      }
      string name=string.Join(" ",parts).Trim();
      return string.IsNullOrEmpty(name)?"canvas":name;
    }

    public string ReadTitle(){
      try{
        using(var pr=Process.GetProcessById(Pid)){ string t=pr.MainWindowTitle; return string.IsNullOrEmpty(t)?null:t; }
      }catch{ return null; }
    }
  }
}
