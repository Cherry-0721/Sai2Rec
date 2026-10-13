using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SaiTl
{
  public enum RecState { NotRecording, Recording, Idle }

  // 录制核心：后台线程按 PollMs 轮询，画布哈希变化→ACTIVE，超 idleThreshold 未变化→IDLE。
  public class CanvasRecorder
  {
    public Canvas Canvas = new Canvas();
    public string CanvasName = "canvas";
    public int IdleThresholdMs = 30000;
    public int PollMs = 120;
    public int KeyframeEvery = 120;
    public string RecordsRoot;
    public string Dir;
    public string TimelinePath;
    volatile bool _run=false;
    Thread _th;
    readonly object _lock = new object();

    public RecState State = RecState.NotRecording;
    public long TotalActiveMs = 0;
    public long SessionId = 0;
    public long DataBytes = 0;
    public long WallClockMs = 0;   // 本次会话墙钟（Start→now，含 idle），用于区分实际录制时间

    long _lastChangeMs;
    ulong _lastHash=0;
    byte[] _prev=null;
    bool _haveFirst=false;
    long _seqFrames=0;
    bool _wasIdle=false;
    public event Action OnTick;

    public long NowMs(){ return DateTime.UtcNow.Ticks/10000; }

    bool EnsureCanvas(out string err){
      err=null;
      try{
        if(Canvas.ph!=IntPtr.Zero){
          using(var old=Process.GetProcessById(Canvas.Pid)){
            if(old.HasExited) throw new Exception("old process exited");
          }
        }
      }catch{
        try{ Canvas.Close(); }catch{}
        Canvas.TileMap=0;
      }
      if(Canvas.ph==IntPtr.Zero && !Canvas.Open()){ err="SAI2 未运行"; return false; }
      if(Canvas.TileMap==0){ err="请先连接并选择真实画布"; return false; }
      try{ if(Canvas.Grab()==null){ err="读取画布失败"; return false; } }
      catch{ err="读取画布失败"; return false; }
      return true;
    }

    public bool Start(out string err){
      err=null;
      lock(_lock){
        if(_run) return true;
        if(!EnsureCanvas(out err)) return false;
        byte[] first=Canvas.Grab();
        if(first==null){ err="读取画布失败"; return false; }
        if(!ResolveRecording()){ err="程序目录不可写：无法创建 recordings/"; return false; }
        LoadOrInitMeta();
        _prev=null; _haveFirst=false; _seqFrames=0; _wasIdle=false;
        OpenSession();
        long now=NowMs(); WallClockMs=0;
        AppendFrame(now, first);
        _run=true;
        _th=new Thread(Loop); _th.IsBackground=true; _th.Start();
        return true;
      }
    }

    public void Stop(){
      lock(_lock){
        if(!_run) return;
        _run=false;
        try{ if(_th!=null) _th.Join(1500); }catch{}
        _th=null;
        CloseSession();
        SaveMeta();
      }
      try{ Canvas.Close(); }catch{}
      Canvas.ph=IntPtr.Zero;
      State=RecState.NotRecording;
    }

    public bool IsRunning(){ lock(_lock){ return _run; } }

    // 清除当前录制的缓存：删除 timeline.dat 与 metadata.json（保留目录）。
    public void ClearCache(){ try{ lock(_lock){ if(_run) return; } }catch{} if(Dir==null) return; try{ if(File.Exists(TimelinePath)) File.Delete(TimelinePath); }catch{} try{ string mp=Path.Combine(Dir,"metadata.json"); if(File.Exists(mp)) File.Delete(mp); }catch{} TotalActiveMs=0; SessionId=0; DataBytes=0; _seqFrames=0; }

    bool ResolveRecording(){
      try{
        string baseDir=AppDomain.CurrentDomain.BaseDirectory;
        string root=Path.Combine(baseDir,"others","recordings");
        Directory.CreateDirectory(root);
        RecordsRoot=root;
        string safe=CanvasIdentity.SafeName(CanvasName);
        Dir=Path.Combine(root,safe);
        Directory.CreateDirectory(Dir);
        TimelinePath = Path.Combine(Dir,"timeline.dat");
        return true;
      } catch { return false; }
    }

    CanvasMeta _meta;
    void LoadOrInitMeta(){
      _meta = TimelineStore.ParseMeta(Dir);
      if(_meta==null){
        _meta = NewMeta();
      } else {
        _meta.CanvasName=CanvasName;
        if(_meta.CreatedAt==0) _meta.CreatedAt=NowMs();
        if(_meta.Width==0) _meta.Width=Canvas.W;
        if(_meta.Height==0) _meta.Height=Canvas.H;
      }
      IdleThresholdMs = (int)(_meta.IdleThresholdMs>0 ? _meta.IdleThresholdMs : (long)IdleThresholdMs);
      TotalActiveMs = _meta.TotalActiveMs;
      if(_meta.Sessions.Count>0) SessionId = _meta.Sessions[_meta.Sessions.Count-1].SessionId;
    }
    CanvasMeta NewMeta(){
      return new CanvasMeta(){
        CanvasId = CanvasIdentity.MakeId(Canvas.TileMap, Canvas.W, Canvas.H, CanvasName),
        CanvasName = CanvasName,
        Width = Canvas.W, Height = Canvas.H,
        CreatedAt = NowMs(),
        IdleThresholdMs = IdleThresholdMs,
        Version = 2,
        TotalActiveMs = 0,
        FrameCount = 0
      };
    }
    void SaveMeta(){
      try{
        if(_meta==null) return;
        _meta.LastRecordedAt=NowMs();
        _meta.TotalActiveMs=TotalActiveMs;
        _meta.FrameCount=_meta.FrameCount>=_seqFrames?_meta.FrameCount:_seqFrames;
        TimelineStore.WriteMeta(Dir,_meta);
      } catch {}
    }
    void OpenSession(){
      _sessionOpen=true;
      _sessionStartMs=NowMs();
      long maxId=0; if(_meta!=null && _meta.Sessions.Count>0){ foreach(var s in _meta.Sessions) if(s.SessionId>maxId) maxId=s.SessionId; }
      long sid=maxId+1;
      SessionId=sid;
      _curSessionActiveMs=0;
      var p=new byte[20];
      BitConverter.GetBytes((int)1).CopyTo(p,0);
      BitConverter.GetBytes(sid).CopyTo(p,4);
      BitConverter.GetBytes(_sessionOpen?_sessionStartMs:0).CopyTo(p,12);
      TimelineStore.Append(TimelinePath, TimelineStore.TYPE_SESSION, _sessionStartMs, p);
      DataBytes = File.Exists(TimelinePath)? new FileInfo(TimelinePath).Length : 0;
    }
    void CloseSession(){
      if(!_sessionOpen) return;
      _sessionOpen=false;
      long end=NowMs();
      if(_meta==null) _meta=NewMeta();
      _meta.Sessions.Add(new SessionRec(SessionId,_sessionStartMs,end,_curSessionActiveMs));
      var p=new byte[20];
      BitConverter.GetBytes((int)0).CopyTo(p,0);
      BitConverter.GetBytes(SessionId).CopyTo(p,4);
      BitConverter.GetBytes(end).CopyTo(p,12);
      TimelineStore.Append(TimelinePath, TimelineStore.TYPE_SESSION, end, p);
      DataBytes = File.Exists(TimelinePath)? new FileInfo(TimelinePath).Length : 0;
    }

    bool _sessionOpen=false;
    long _sessionStartMs=0;
    long _curSessionActiveMs=0;
    long _lastMetaSaveMs=0;

    void AppendFrame(long ts, byte[] cur){
      byte[] payload;
      bool key = (_prev==null || !_haveFirst) || (_seqFrames>=1 && _seqFrames % KeyframeEvery==0);
      if(key){ payload=cur; }
      else { payload=DeltaCodec.EncodeDelta(_prev,cur,Canvas.Cx*256); }
      TimelineStore.Append(TimelinePath, TimelineStore.TYPE_FRAME, ts, payload);
      _prev=cur; _haveFirst=true; _seqFrames++;
      DataBytes = File.Exists(TimelinePath)? new FileInfo(TimelinePath).Length : 0;
    }

    void Loop(){
      bool firstPoll=true;
      while(_run){
        try{
          long now=NowMs();
          if(_sessionOpen) WallClockMs = now - _sessionStartMs;
          byte[] cur=Canvas.Grab();
          if(cur!=null){
            ulong h=Canvas.Hash(cur);
            if(firstPoll){ firstPoll=false; _lastHash=h; _lastChangeMs=now; _wasIdle=false; }
            else if(h!=_lastHash){
              _lastHash=h;
              long gap=now-_lastChangeMs;
              if(_wasIdle){
                _wasIdle=false;
                _lastChangeMs=now;
                AppendFrame(now,cur);
                State=RecState.Recording;
              } else {
                if(gap<=IdleThresholdMs) { TotalActiveMs+=gap; _curSessionActiveMs+=gap; }
                _lastChangeMs=now;
                AppendFrame(now,cur);
                State=RecState.Recording;
              }
            } else {
              long since=now-_lastChangeMs;
              if(State==RecState.Recording && since>IdleThresholdMs){
                State=RecState.Idle;
                _wasIdle=true;
              }
            }
          } else {
            if(State!=RecState.NotRecording) State=RecState.Idle;
          }
          if(now-_lastMetaSaveMs > 5000){ _lastMetaSaveMs=now; SaveMeta(); }
          if(OnTick!=null){ try{ OnTick(); }catch{} }
        }catch(Exception){ }
        Thread.Sleep(PollMs);
      }
    }
  }

}
