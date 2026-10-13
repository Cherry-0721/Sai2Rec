using System;
using System.Collections.Generic;
using System.IO;

namespace SaiTl
{
  // Keep only the frame index and current decoded frame in memory.
  public class TimelineSampler : IDisposable
  {
    class FrameIndex
    {
      public long Timestamp;
      public long PayloadOffset;
      public int PayloadLength;
      public long Position;
    }

    public string Dir;
    public List<Tuple<uint,long,byte[]>> Records;
    public int Width,Height,Cx,Cy;
    public long TotalActiveMs;
    public int FrameCount;

    readonly List<FrameIndex> _frames=new List<FrameIndex>();
    FileStream _timeline;
    BinaryReader _reader;
    int _decodedIndex=-1;
    byte[] _decodedFrame;
    int _pitch,_rows;

    public bool Load(string dir){
      Dispose();
      Dir=dir;
      var meta=TimelineStore.ParseMeta(dir);
      if(meta==null) { Width=0; Height=0; Cx=1; }
      else { Width=meta.Width; Height=meta.Height; Cx=(meta.Width+255)/256; }
      string tl=Path.Combine(dir,"timeline.dat");
      if(!File.Exists(tl)) return false;

      try{
        _timeline=new FileStream(tl,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        _reader=new BinaryReader(_timeline);
        int firstDataLen=0;
        while(_timeline.Position+16<=_timeline.Length){
          uint type=_reader.ReadUInt32();
          long ts=_reader.ReadInt64();
          int length=_reader.ReadInt32();
          if(length<0 || length>(1<<30) || _timeline.Position+length>_timeline.Length) break;
          long payloadOffset=_timeline.Position;
          if(type==TimelineStore.TYPE_FRAME){
            _frames.Add(new FrameIndex(){ Timestamp=ts, PayloadOffset=payloadOffset, PayloadLength=length });
            if(firstDataLen==0) firstDataLen=length;
          }
          _timeline.Position=payloadOffset+length;
        }
        if(_frames.Count==0){ Dispose(); return false; }
        FrameCount=_frames.Count;

        if(meta!=null && meta.Width>0 && meta.Height>0){
          Width=meta.Width; Height=meta.Height; Cx=(Width+255)/256; Cy=(Height+255)/256;
          _pitch=Cx*256*4; _rows=Cy*256;
        } else {
          _pitch=0; _rows=0;
          for(int cx=1;cx<=64;cx++){
            int tw=cx*256; if(firstDataLen%(tw*4)!=0) continue;
            int rows=firstDataLen/(tw*4);
            for(int cy=1;cy<=64;cy++) if(cy*256==rows){ Cx=cx; Cy=cy; _pitch=tw*4; _rows=rows; break; }
            if(_pitch!=0) break;
          }
          if(_pitch==0){ Dispose(); return false; }
          Width=Cx*256; Height=Cy*256;
        }

        long idle=meta!=null&&meta.IdleThresholdMs>0?meta.IdleThresholdMs:30000;
        TotalActiveMs=0;
        long position=0;
        _frames[0].Position=0;
        for(int i=1;i<_frames.Count;i++){
          long gap=_frames[i].Timestamp-_frames[i-1].Timestamp;
          if(gap<=idle && gap>0) TotalActiveMs+=gap;
          if(gap<=0 || gap>idle) gap=30;
          position+=gap;
          _frames[i].Position=position;
        }
        _decodedIndex=-1;
        _decodedFrame=null;
        return true;
      }catch{
        Dispose();
        return false;
      }
    }

    byte[] ReadPayload(FrameIndex frame){
      _timeline.Position=frame.PayloadOffset;
      byte[] data=_reader.ReadBytes(frame.PayloadLength);
      return data.Length==frame.PayloadLength?data:null;
    }

    bool DecodeThrough(int targetIndex){
      if(targetIndex<0 || targetIndex>=_frames.Count) return false;
      if(targetIndex<_decodedIndex){ _decodedIndex=-1; _decodedFrame=null; }
      int tw=Cx*256;
      for(int i=_decodedIndex+1;i<=targetIndex;i++){
        byte[] data=ReadPayload(_frames[i]);
        if(data==null) return false;
        if(DeltaCodec.IsDelta(data) && _decodedFrame==null) return false;
        byte[] next=DeltaCodec.Decode(_decodedFrame,data,tw);
        if(next==null) next=new byte[_pitch*_rows];
        _decodedFrame=next;
        _decodedIndex=i;
      }
      return _decodedFrame!=null;
    }

    public byte[] FrameAt(long sourceMs){
      if(_frames.Count==0) return null;
      long maxPosition=_frames[_frames.Count-1].Position;
      long t=sourceMs; if(t<0)t=0; if(t>maxPosition)t=maxPosition;
      int lo=0,hi=_frames.Count-1;
      while(lo<hi){
        int mid=lo+(hi-lo)/2;
        if(_frames[mid].Position<t) lo=mid+1; else hi=mid;
      }
      int index=lo;
      if(index>0){
        long before=t-_frames[index-1].Position;
        long after=_frames[index].Position-t;
        if(before<after) index--;
      }
      return DecodeThrough(index)?_decodedFrame:null;
    }

    public void Dispose(){
      try{ if(_reader!=null) _reader.Close(); }catch{}
      try{ if(_timeline!=null) _timeline.Close(); }catch{}
      _reader=null; _timeline=null; _frames.Clear(); _decodedFrame=null; _decodedIndex=-1;
    }
  }
}
