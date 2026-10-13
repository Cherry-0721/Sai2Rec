using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SaiTl
{
  // 导出：duration(≤60s)×FPS(≤60) → 按有效时间轴采样重建 → 临时 BMP(ASCII) → h264_mf → 程序目录 output\MP4 → 清理。
  public class TimelapseExporter
  {
    public string FfmpegOverride;
    public string CanvasName="canvas";
    public string OutputDir=null;   // 默认程序目录下 output/；测试可覆盖到临时目录
    public bool SwapRedBlue=true;   // 红蓝交换开关：SAI2 tile 内存为 RGBA，写入 BMP(24bpp RGB) 需对调 R/B
    public Action<string,long,long> OnProgress;
    int _w,_h,_cx,_cy;

    static void WriteFrame(Stream stream, byte[] frame){
      const int chunkSize=1024*1024;
      int offset=0;
      while(offset<frame.Length){
        int count=Math.Min(chunkSize,frame.Length-offset);
        stream.Write(frame,offset,count);
        offset+=count;
      }
    }

    static byte[] PackVisibleCanvas(byte[] tiled, int sourceWidth, int width, int height){
      int sourcePitch=sourceWidth*4;
      int outputPitch=width*4;
      if(tiled==null || tiled.Length<sourcePitch*height) return null;
      if(sourcePitch==outputPitch && tiled.Length==outputPitch*height) return tiled;
      byte[] packed=new byte[outputPitch*height];
      for(int y=0;y<height;y++) Array.Copy(tiled,y*sourcePitch,packed,y*outputPitch,outputPitch);
      return packed;
    }

    string CleanFfmpeg(){
      if(!string.IsNullOrEmpty(FfmpegOverride) && File.Exists(FfmpegOverride)) return FfmpegOverride;
      string exeDir=AppDomain.CurrentDomain.BaseDirectory;
      string[] c={ Path.Combine(exeDir,"others","ffmpeg.exe"),
                   Path.Combine(exeDir,"ffmpeg.exe"),
                   Path.Combine(exeDir,"assets","ffmpeg.exe"),
                   Path.Combine(exeDir,"ffmpeg64.exe"),
                   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","xiezuo","resources","ffmpeg","ffmpeg.exe"),
                   "ffmpeg.exe" };
      foreach(var x in c) if(File.Exists(x)) return x;
      return null;
    }
    bool SaveBMP(string path, byte[] full, int w,int h,int tw){
      try{
        var bmp=new Bitmap(w,h,PixelFormat.Format24bppRgb);
        var rr=bmp.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
        byte[] row=new byte[w*3];
        for(int y=0;y<h;y++){
          for(int x=0;x<w;x++){
            int si=y*tw*4+x*4;
            if(SwapRedBlue){ row[x*3+0]=full[si+0]; row[x*3+1]=full[si+1]; row[x*3+2]=full[si+2]; }
            else { row[x*3+0]=full[si+2]; row[x*3+1]=full[si+1]; row[x*3+2]=full[si+0]; }
          }
          IntPtr dst=new IntPtr(rr.Scan0.ToInt64()+(long)y*rr.Stride); Marshal.Copy(row,0,dst,w*3);
        }
        bmp.UnlockBits(rr); bmp.Save(path,ImageFormat.Bmp); bmp.Dispose();
        return File.Exists(path);
      } catch { return false; }
    }
    public string Export(string recordingDir, long durationMs, int fps){
      string ff=CleanFfmpeg();
      if(ff==null){ try{ File.AppendAllText(Path.Combine(Path.GetTempPath(),"saitl_export.log"), "["+DateTime.Now+"] FFmpeg not found\r\n"); }catch{} return null; }
      using(var sampler=new TimelineSampler()){
      if(!sampler.Load(recordingDir)) return null;
      // Strip tile padding before piping rawvideo. This FFmpeg build limits a
      // single pipe packet to 50,000,000 bytes; a padded 3072x4096 frame is
      // larger even though the real 2894x4093 canvas is not.
      _w=sampler.Width&~1; _h=sampler.Height&~1; _cx=sampler.Cx; _cy=sampler.Cy;
      if(_w<=0||_h<=0) return null;
      long outFrames=(long)(durationMs*fps/1000.0); if(outFrames<1) outFrames=1;
      long totalActive=sampler.TotalActiveMs; if(totalActive<=0) totalActive=1;
      string outDir = string.IsNullOrEmpty(OutputDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"output") : OutputDir;
      Directory.CreateDirectory(outDir);
      string mp4tmp=Path.Combine(outDir,".saitl_"+Guid.NewGuid().ToString("N").Substring(0,8)+".mp4");
      Process pr=null;
      try{
        // Stream raw BGRA frames directly to FFmpeg. This avoids writing
        // hundreds of large temporary BMP files for every export.
        string argLine="-y -f rawvideo -pix_fmt bgra -video_size "+_w+"x"+_h+" -framerate "+fps+" -i - -c:v h264_mf -b:v 20M -maxrate 30M -bufsize 40M -pix_fmt yuv420p -an \""+mp4tmp+"\"";
        var psi=new ProcessStartInfo(ff,argLine){ UseShellExecute=false, RedirectStandardInput=true, RedirectStandardError=true, CreateNoWindow=true };
        pr=Process.Start(psi);
        // FFmpeg writes progress and diagnostics to stderr. Drain it concurrently,
        // otherwise its pipe buffer can fill while raw frames are being written.
        var errTask=Task.Factory.StartNew<string>(()=>pr.StandardError.ReadToEnd());
        long i=1;
        for(long n=1;n<=outFrames;n++){
          double outSec=(n-1.0)/fps;
          double frac = durationMs>0 ? outSec*1000.0/durationMs : 0;
          long srcMs=(long)Math.Round(frac*totalActive);
          byte[] frame=sampler.FrameAt(srcMs);
          if(frame==null){ try{pr.StandardInput.Close();}catch{} try{pr.Kill();}catch{} SafeDelete(mp4tmp); return null; }
          byte[] packed=PackVisibleCanvas(frame,_cx*256,_w,_h);
          if(packed==null){ try{pr.StandardInput.Close();}catch{} try{pr.Kill();}catch{} SafeDelete(mp4tmp); return null; }
          WriteFrame(pr.StandardInput.BaseStream,packed);
          if(OnProgress!=null) OnProgress("正在重建",n,outFrames);
          i++;
        }
        if(OnProgress!=null) OnProgress("正在编码",0,0);
        pr.StandardInput.Close(); string err=errTask.Result; pr.WaitForExit();
        if(pr.ExitCode!=0 || !File.Exists(mp4tmp)){
          try{ File.AppendAllText(Path.Combine(Path.GetTempPath(),"saitl_export.log"), "["+DateTime.Now+"] FFmpeg exit="+pr.ExitCode+"\r\n"+err+"\r\n"); }catch{}
          SafeDelete(mp4tmp); return null;
        }
        string outMp4=Path.Combine(outDir, CanvasName+"_timelapse.mp4");
        try{ if(File.Exists(outMp4)) File.Delete(outMp4); }catch{}
        try{ Directory.CreateDirectory(outDir); }catch{}
        File.Copy(mp4tmp,outMp4,true);
        // 导出成功后【不清除】录制缓存：只有用户点「清除缓存」才删（保留可反复导出）
        SafeDelete(mp4tmp);
        return outMp4;
      }catch(Exception ex){ try{ if(pr!=null) pr.Kill(); }catch{} try{ File.AppendAllText(Path.Combine(Path.GetTempPath(),"saitl_export.log"), "["+DateTime.Now+"] "+ex+"\r\n"); }catch{} SafeDelete(mp4tmp); return null; }
      }
    }
    void ClearRecordingCache(string dir){
      try{
        if(dir==null) return;
        string tl=Path.Combine(dir,"timeline.dat"); if(File.Exists(tl)) File.Delete(tl);
        string mp=Path.Combine(dir,"metadata.json"); if(File.Exists(mp)) File.Delete(mp);
      }catch{}
    }
    void Cleanup(string tmp,bool keep){ try{ if(keep) return; if(Directory.Exists(tmp)) Directory.Delete(tmp,true); }catch{} }
    void SafeDelete(string path){ try{ if(File.Exists(path)) File.Delete(path); }catch{} }
  }
}
