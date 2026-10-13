using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SaiTl
{
  public class SessionRec
  {
    public long SessionId;
    public long StartTime;   // Unix ms
    public long EndTime;     // Unix ms
    public long ActiveDurationMs;
    public SessionRec() { }
    public SessionRec(long id, long start, long end, long active){ SessionId=id; StartTime=start; EndTime=end; ActiveDurationMs=active; }
  }

  public class CanvasMeta
  {
    public string CanvasId="";
    public string CanvasName="";
    public int Width,Height;
    public long CreatedAt=0;
    public long LastRecordedAt=0;
    public long IdleThresholdMs=30000;
    public int Version=1;
    public long TotalActiveMs=0;
    public long FrameCount=0;
    public List<SessionRec> Sessions = new List<SessionRec>();

    public static CanvasMeta Parse(string json)
    {
      CanvasMeta m = new CanvasMeta();
      // 轻量 JSON 解析：仅为本工程自行生成且受控的 metadata。
      var lines = json.Replace("\r","").Split('\n');
      string curSessionId=null, curStart=null, curEnd=null, curActive=null;
      bool inSessions=false;
      foreach (var raw in lines){
        string l=raw.Trim();
        if (l.Length==0) continue;
        if (l=="sessions:["){ inSessions=true; continue; }
        if (inSessions){
          if (l.StartsWith("]}")) { inSessions=false; if(curSessionId!=null) FinalizeSession(m,curSessionId,curStart,curEnd,curActive); curSessionId=null; curStart=null; curEnd=null; curActive=null; continue; }
          if (l.StartsWith("{")) { if(curSessionId!=null) FinalizeSession(m,curSessionId,curStart,curEnd,curActive); curSessionId=null; curStart=null; curEnd=null; curActive=null; }
          if (l.StartsWith("\"sessionId\":")) curSessionId = Extract(l);
          else if (l.StartsWith("\"startTime\":")) curStart = Extract(l);
          else if (l.StartsWith("\"endTime\":")) curEnd = Extract(l);
          else if (l.StartsWith("\"activeDurationMs\":")) curActive = Extract(l);
          else if (l.StartsWith("\"activeDurationMs\":") || l.StartsWith("\"activeDuration\":") || l.StartsWith("\"startTime\":") || l.StartsWith("\"endTime\":") ) { }
          continue;
        }
        if (l.StartsWith("\"canvasId\":")) m.CanvasId=Extract(l);
        else if (l.StartsWith("\"canvasName\":")) m.CanvasName=Extract(l);
        else if (l.StartsWith("\"width\":")) m.Width=Num(l);
        else if (l.StartsWith("\"height\":")) m.Height=Num(l);
        else if (l.StartsWith("\"createdAt\":")) m.CreatedAt=Num(l);
        else if (l.StartsWith("\"lastRecordedAt\":")) m.LastRecordedAt=Num(l);
        else if (l.StartsWith("\"idleThresholdMs\":")) m.IdleThresholdMs=Num(l);
        else if (l.StartsWith("\"version\":")) m.Version=Num(l);
        else if (l.StartsWith("\"totalActiveMs\":")) m.TotalActiveMs=Num(l);
        else if (l.StartsWith("\"frameCount\":")) m.FrameCount=Num(l);
        else if (l=="sessions:["){ inSessions=true; }
      }
      if (curSessionId!=null) FinalizeSession(m,curSessionId,curStart,curEnd,curActive);
      return m;
    }
    static void FinalizeSession(CanvasMeta m,string id,string s,string e,string a){
      long sid=id!=null?SafeInt(id):m.Sessions.Count+1;
      long st=s!=null?SafeInt(s):0, en=e!=null?SafeInt(e):0, ac=a!=null?SafeInt(a):0;
      m.Sessions.Add(new SessionRec(sid,st,en,ac));
    }
    static long SafeInt(string v){ long r; return long.TryParse(v,out r)?r:0; }
    static int Num(string l){ int r; return int.TryParse(Extract(l),out r)?r:0; }
    static string Extract(string l){
      int i=l.IndexOf(':'); if(i<0) return "";
      string v=l.Substring(i+1).Trim();
      v=v.TrimEnd(',',' '); v=v.Trim('"');
      return v;
    }
  }
}
