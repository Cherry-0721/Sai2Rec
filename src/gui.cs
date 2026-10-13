using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;

namespace SaiTl
{
  public class MainForm : Form
  {
    CanvasRecorder rec;
    Label lblCanvas,lblStatus,lblActive,lblWall,lblVideo,lblData,lblOutput;
    ComboBox cbDuration,cbFps;
    ComboBox cbCanvas;
    System.Collections.Generic.List<Canvas.Candidate> _candList=null;
    Button btnConnect,btnStart,btnStop,btnExport,btnClear;

    public MainForm(){
      Text="Sai2Rec";
      Width=680; Height=460; StartPosition=FormStartPosition.CenterScreen;
      rec=new CanvasRecorder();
      var f10=new Font("Microsoft YaHei",10);
      var pnl=new FlowLayoutPanel(){ Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown, Padding=new Padding(14) };
      Controls.Add(pnl);
      lblCanvas=new Label(){ AutoSize=true, Text="画布: 未连接", Font=f10 };
      lblStatus=new Label(){ AutoSize=true, Text="状态: 空闲", Font=f10 };
      lblActive=new Label(){ AutoSize=true, Text="有效绘画时间: 00:00:00", Font=f10 };
      lblWall=new Label(){ AutoSize=true, Text="实际录制时间: 00:00:00", Font=f10 };
      lblVideo=new Label(){ AutoSize=true, Text="导出视频: 0s / 0fps", Font=f10 };
      lblData=new Label(){ AutoSize=true, Text="录制数据大小: 0 B", Font=f10 };
      lblOutput=new Label(){ AutoSize=true, Text="输出: (尚未生成)", Font=f10 };

      pnl.Controls.Add(lblCanvas); pnl.Controls.Add(lblStatus);
      pnl.Controls.Add(lblActive); pnl.Controls.Add(lblWall); pnl.Controls.Add(lblVideo); pnl.Controls.Add(lblData);
      pnl.Controls.Add(lblOutput);

      var rowDur=new FlowLayoutPanel(){ FlowDirection=FlowDirection.LeftToRight, AutoSize=true };
      cbDuration=new ComboBox(){ DropDownStyle=ComboBoxStyle.DropDown, Width=80, Font=f10 };
      foreach(var s in new[]{"10","20","30","40","50","60"}) cbDuration.Items.Add(s);
      cbDuration.Text="30";
      rowDur.Controls.Add(new Label(){ Text="Timelapse 时长(s):", AutoSize=true, Font=f10 });
      rowDur.Controls.Add(cbDuration);
      pnl.Controls.Add(rowDur);

      var rowFps=new FlowLayoutPanel(){ FlowDirection=FlowDirection.LeftToRight, AutoSize=true };
      cbFps=new ComboBox(){ DropDownStyle=ComboBoxStyle.DropDown, Width=80, Font=f10 };
      foreach(var s in new[]{"15","24","30","60"}) cbFps.Items.Add(s);
      cbFps.Text="30";
      rowFps.Controls.Add(new Label(){ Text="输出 FPS:", AutoSize=true, Font=f10 });
      rowFps.Controls.Add(cbFps);
      pnl.Controls.Add(rowFps);

      var rowCanvas=new FlowLayoutPanel(){ FlowDirection=FlowDirection.LeftToRight, AutoSize=true };
      cbCanvas=new ComboBox(){ DropDownStyle=ComboBoxStyle.DropDown, Width=320, Font=f10 };
      cbCanvas.Items.Add("(连接后列出候选画布)");
      cbCanvas.SelectedIndex=0;
      rowCanvas.Controls.Add(new Label(){ Text="画布候选:", AutoSize=true, Font=f10 });
      rowCanvas.Controls.Add(cbCanvas);
      pnl.Controls.Add(rowCanvas);

      var rowBtn=new FlowLayoutPanel(){ FlowDirection=FlowDirection.LeftToRight, AutoSize=true };
      btnConnect=new Button(){ Text="连接画布", Width=110, Height=44, Font=new Font("Microsoft YaHei",11) };
      btnStart=new Button(){ Text="Start", Width=120, Height=44, Font=new Font("Microsoft YaHei",11) };
      btnStop=new Button(){ Text="Stop",  Width=120, Height=44, Font=new Font("Microsoft YaHei",11), Enabled=false };
      btnExport=new Button(){ Text="导出 MP4", Width=130, Height=44, Font=new Font("Microsoft YaHei",11), Enabled=false };
      btnClear=new Button(){ Text="清除缓存", Width=110, Height=44, Font=new Font("Microsoft YaHei",11) };
      btnConnect.Click+=(s,e)=>ConnectCanvas();
      btnStart.Click+=(s,e)=>StartRec();
      btnStop.Click+=(s,e)=>StopRec();
      btnExport.Click+=(s,e)=>Export();
      btnClear.Click+=(s,e)=>ClearRec();
      rowBtn.Controls.Add(btnClear); rowBtn.Controls.Add(btnConnect); rowBtn.Controls.Add(btnStart); rowBtn.Controls.Add(btnStop); rowBtn.Controls.Add(btnExport);
      pnl.Controls.Add(rowBtn);

      Shown+=(s,e)=>{ LoadMetaIntoUi(); };
    }

    void UI(Action a){ try{ if(IsHandleCreated) BeginInvoke(a); else a(); }catch{} }
    void SetCanvas(string t){ UI(()=>{ lblCanvas.Text=t; }); }
    void SetStatus(string t){ UI(()=>{ lblStatus.Text=t; }); }
    void SetActive(){ UI(()=>{ long ms=rec.TotalActiveMs; lblActive.Text="有效绘画时间: "+Fmt(ms); }); }
    void SetWall(){ UI(()=>{ long ms=rec.WallClockMs; lblWall.Text="实际录制时间: "+Fmt(ms); }); }
    void SetVideo(){ UI(()=>{ lblVideo.Text="导出视频: "+cbDuration.Text+"s / "+cbFps.Text+"fps"; }); }
    // 候选画布显示名：单张用窗口标题解析名；多张取标题名（候选本身难分辨，靠尺寸+地址辅助）
    string showCanvasName(Canvas.Candidate c){ return rec.CanvasName; }
    // 上次选中的画布名：从 recordings 里最近一个 metadata 的 canvasName 取得
    string LastSelectedCanvasName(){
      try{
        string last=FindLastRecording();
        if(last==null) return "";
        var m=TimelineStore.ParseMeta(last);
        return m!=null?m.CanvasName:"";
      }catch{ return ""; }
    }
    void SetData(){ UI(()=>{ lblData.Text="录制数据大小: "+Bytes(rec.DataBytes); }); }
    static string Fmt(long ms){ long s=ms/1000; long h=s/3600, m=(s%3600)/60, sec=s%60; return h.ToString("00")+":"+m.ToString("00")+":"+sec.ToString("00"); }
    static string Bytes(long b){ if(b<1024) return b+" B"; if(b<1048576) return (b/1024.0).ToString("0.0")+" KB"; return (b/1048576.0).ToString("0.0")+" MB"; }

    void LoadMetaIntoUi(){
      var root=RecordingRoot();
      if(Directory.Exists(root)){
        foreach(var d in Directory.GetDirectories(root)){
          var m=TimelineStore.ParseMeta(d); if(m!=null){ SetCanvas("已有录制: "+d); break; }
        }
      }
    }

    // 手动连接：后台扫描定位画布，读取窗口标题作为画布信息（仅显示，不作录制目录名）
    void ConnectCanvas(){
      btnConnect.Enabled=false; SetStatus("状态: 正在连接 SAI2 并定位画布...");
      string info="画布: 未找到（请确认 SAI2 已打开画布）";
      var t=new Thread(()=>{
        try{
          if(rec.Canvas.ph==IntPtr.Zero && !rec.Canvas.Open()){ info="画布: SAI2 未运行"; }
          else{
            var cands=rec.Canvas.ScanList();
            _candList = cands;
            // 记住上次选中的画布名（画布地址每次可能变，但名字稳定，用于自动匹配）
            string lastSelName = LastSelectedCanvasName();
            UI(()=>{
              cbCanvas.Items.Clear();
              cbCanvas.Items.Add("(请选择要录制的画布)");
              string ttl=rec.Canvas.ReadTitle();
              string shown=string.IsNullOrEmpty(ttl)?"canvas":rec.Canvas.ParseCanvasName(ttl);
              for(int k=0;k<cands.Count;k++){
                var c=cands[k];
                string nm = showCanvasName(c); // 尽量用标题名；候选多张时只能都叫标题名
                cbCanvas.Items.Add(nm+"  ("+c.W+"x"+c.H+")");   // 画布地址会漂移，下拉只显示尺寸不动地址
              }
              int bestIdx=-1;
              if(!string.IsNullOrEmpty(lastSelName) && cands.Count>0){
                for(int k=0;k<cands.Count;k++) if(showCanvasName(cands[k])==lastSelName){ bestIdx=k; break; }
              }
              if(bestIdx>=0) cbCanvas.SelectedIndex=bestIdx+1;
              else if(cands.Count>0) cbCanvas.SelectedIndex=1; // 默认自动使用最高评分候选，仍可手动切换
              info="画布: "+shown+"   共 "+cands.Count+" 个候选（可下拉选择）";
              SetCanvas(info);
            });
          }
        }catch(Exception ex){ info="画布: 连接异常 - "+ex.Message; }
        SetStatus("状态: 已连接（请在下拉框选择真实画布后点击 Start）");
        UI(()=>{ btnConnect.Enabled=true; });
      });
      t.IsBackground=true; t.Start();
    }

    void StartRec(){
      if(rec.IsRunning()) return;
      // 必须先在候选下拉框里手动选择真实画布，避免自动 Scan 挑到调色板/便签本
      if(cbCanvas.SelectedIndex<=0){
        SetStatus("状态: 请先点「连接画布」");
        MessageBox.Show("请先点击「连接画布」让程序自动定位画布。若有多个候选，可再手动切换。","提示");
        return;
      }
      {
        int sel=cbCanvas.SelectedIndex;
        if(_candList==null || sel-1<0 || sel-1>=_candList.Count){
          SetStatus("状态: 画布地址解析失败"); return;
        }
        // 地址只在内存变量里传递，不写进下拉框文本（地址会漂移）
        rec.Canvas.TileMap=_candList[sel-1].TileMap;
      }
      btnStart.Enabled=false; btnStop.Enabled=true; btnExport.Enabled=false;
      SetStatus("状态: 正在启动录制...");
      var t=new Thread(()=>{
        string err;
        SetCanvas("画布: 正在连接 SAI2 并定位画布...");
        // 用窗口标题解析真实画布名，作为录制目录名（避免全部变成 canvas / 含版本号杂项）
        string ttl=rec.Canvas.ReadTitle();
        if(!string.IsNullOrEmpty(ttl)) rec.CanvasName=rec.Canvas.ParseCanvasName(ttl); else rec.CanvasName="canvas";
        if(!rec.Start(out err)){
          UI(()=>{ btnStart.Enabled=true; btnStop.Enabled=false; btnExport.Enabled=false; });
          SetStatus("状态: 启动失败 - "+err);
          SetCanvas("画布: "+err);
          return;
        }
        rec.OnTick += ()=>{ SetActive(); SetWall(); SetVideo(); SetData(); SetStatus("状态: 录制中"); };
        UI(()=>{ btnStart.Enabled=false; btnStop.Enabled=true; btnExport.Enabled=true; });
        SetStatus("状态: 录制中");
      });
      t.IsBackground=true; t.Start();
    }

    void ClearRec(){
      if(rec.IsRunning()){ MessageBox.Show("请先停止录制再清除缓存","提示"); return; }
      var r=MessageBox.Show("确定要清除当前录制缓存吗？\n会删除已录制的数据。","清除缓存",MessageBoxButtons.YesNo);
      if(r==DialogResult.Yes){ rec.ClearCache(); SetActive(); SetData(); SetStatus("状态: 缓存已清除"); }
    }
    void StopRec(){
      if(!rec.IsRunning()) { btnStop.Enabled=false; return; }
      SetStatus("状态: 正在停止...");
      rec.Stop();
      btnStart.Enabled=true; btnStop.Enabled=false; btnExport.Enabled=true;
      btnStart.Text="Start";
      SetStatus("状态: 已停止");
      SetActive(); SetWall(); SetData();
    }

    void Export(){
      long durMs; int fps;
      if(!TryReadSettings(out durMs,out fps)) return;
      btnStart.Enabled=false; btnStop.Enabled=false; btnExport.Enabled=false;
      SetStatus("状态: 正在导出 mp4...");
      // 优先用最近有数据的录制目录；关掉画布后 rec.Dir 可能仍指向旧会话，直接用它
      string dir=rec.Dir;
      if(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir,"timeline.dat"))) dir=FindLastRecording();
      if(dir==null || !File.Exists(Path.Combine(dir,"timeline.dat"))){
        SetStatus("状态: 未找到可导出的录制数据"); btnStart.Enabled=true; btnStop.Enabled=false; btnExport.Enabled=true;
        MessageBox.Show("没找到可导出的录制数据。\n可能原因：还没录过，或当前没有有效录制。","导出提示");
        return;
      }
      SetVideo();
      var exp=new TimelapseExporter(){ CanvasName=rec.CanvasName, OutputDir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"output") };
      var t=new Thread(()=>{
        string mp4=exp.Export(dir,durMs,fps);
        UI(()=>{
          btnStart.Enabled=true; btnStop.Enabled=false; btnExport.Enabled=true;
          if(mp4!=null){ lblOutput.Text="输出: "+mp4; SetStatus("状态: 完成"); MessageBox.Show("已导出:\n"+mp4,"完成"); }
          else { SetStatus("状态: 导出失败"); MessageBox.Show("导出失败。请确认安装目录完整且磁盘空间充足；详细原因记录在 %TEMP%\\saitl_export.log。","错误"); }
        });
      });
      t.IsBackground=true; t.Start();
    }

    bool TryReadSettings(out long durMs,out int fps){
      durMs=0; fps=0;
      string durText=cbDuration.Text.Trim(); string fpsText=cbFps.Text.Trim();
      int dur,f;
      if(!int.TryParse(durText,out dur)||dur<=0||dur>60){ MessageBox.Show("Timelapse 时长必须是 1~60 秒","输入错误"); return false; }
      if(!int.TryParse(fpsText,out f)||f<=0||f>60){ MessageBox.Show("FPS 必须是 1~60","输入错误"); return false; }
      durMs=(long)dur*1000; fps=f; return true;
    }

    string FindLastRecording(){
      var root=RecordingRoot();
      if(!Directory.Exists(root)) return null;
      string best=null; long max=-1;
      foreach(var d in Directory.GetDirectories(root)){
        var tl=Path.Combine(d,"timeline.dat");
        if(File.Exists(tl)){ long len=new FileInfo(tl).Length; if(len>max){max=len;best=d;} }
      }
      return best;
    }

    static string RecordingRoot(){
      return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"others","recordings");
    }
  }

  public static class Program
  {
    [STAThread]
    static int Main(){
      // 全局异常日志：崩溃时写入 %TEMP%\saitl_crash.log，方便排查闪退。
      System.AppDomain.CurrentDomain.UnhandledException += (sx,ex)=>{ try{ File.AppendAllText(Path.Combine(Path.GetTempPath(),"saitl_crash.log"), "["+DateTime.Now.ToString()+"] "+(ex.ExceptionObject!=null?ex.ExceptionObject.ToString():"null")+"\r\n"); }catch{} };
      Application.ThreadException += (sx,ex)=>{ try{ File.AppendAllText(Path.Combine(Path.GetTempPath(),"saitl_crash.log"), "[UI] "+(ex.Exception!=null?ex.Exception.ToString():"null")+"\r\n"); }catch{} };
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.Run(new MainForm());
      return 0;
    }
  }
}
