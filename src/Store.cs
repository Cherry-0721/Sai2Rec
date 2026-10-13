using System;
using System.Collections.Generic;
using System.IO;

namespace SaiTl
{
  // 画布身份：优先用内存扫描得到的 tilemap 地址作初始 seed 生成稳定 CanvasId，
  // 并结合宽高。由于 SAI2 未暴露公开内部 ID，重命名画布后无法保证跨进程仍指向
  // 同一逻辑画布 —— 这是已知限制。程序启动时对每个 recordings/ 子目录校验 metadata，
  // 只对符合当前扫到画布特征（宽高一致）的目录继续追加。
  public static class CanvasIdentity
  {
    public static string MakeId(long tilemap, int w, int h, string name)
    {
      ulong hsh = 14695981039346656037UL;
      foreach (uint x in new uint[]{ (uint)((ulong)tilemap & 0xFFFFFFFFu), (uint)((ulong)tilemap >> 32), (uint)w, (uint)h })
      {
        byte[] b = BitConverter.GetBytes(x);
        for (int i=0;i<b.Length;i++){ hsh ^= b[i]; hsh *= 1099511628211UL; }
      }
      if (string.IsNullOrEmpty(name)) name = "canvas";
      foreach (char c in name) { hsh ^= (byte)(c & 0xFF); hsh *= 1099511628211UL; }
      return hsh.ToString("x16");
    }
    public static string SafeName(string name)
    {
      if (string.IsNullOrEmpty(name)) return "canvas";
      string n = "";
      foreach (char c in name){ if (Array.IndexOf(Path.GetInvalidFileNameChars(), c)>=0) n += "_"; else n += c; }
      if (n.Length==0) n="canvas";
      return n;
    }
  }

  // timeline.dat 记录格式（追加式、自描述、可安全恢复）：
  //   [u32 type][u64 ts(Unix ms)][u32 payloadLen][payload]
  //   type: 1=CANVAS_META, 2=FRAME, 3=SESSION
  //   恢复时顺序扫描，丢弃尾部因崩溃产生的残缺记录。
  public static class TimelineStore
  {
    public const uint TYPE_CANVAS_META = 1;
    public const uint TYPE_FRAME = 2;
    public const uint TYPE_SESSION = 3;

    public static void WriteMeta(string dir, CanvasMeta meta)
    {
      var sb = new System.Text.StringBuilder();
      sb.Append("{\n\"canvasId\": \"").Append(JsonEscape(meta.CanvasId)).Append("\",\n");
      sb.Append("\"canvasName\": \"").Append(JsonEscape(meta.CanvasName)).Append("\",\n");
      sb.Append("\"width\": ").Append(meta.Width).Append(",\n");
      sb.Append("\"height\": ").Append(meta.Height).Append(",\n");
      sb.Append("\"createdAt\": ").Append(meta.CreatedAt).Append(",\n");
      sb.Append("\"lastRecordedAt\": ").Append(meta.LastRecordedAt).Append(",\n");
      sb.Append("\"idleThresholdMs\": ").Append(meta.IdleThresholdMs).Append(",\n");
      sb.Append("\"version\": ").Append(meta.Version).Append(",\n");
      sb.Append("\"totalActiveMs\": ").Append(meta.TotalActiveMs).Append(",\n");
      sb.Append("\"frameCount\": ").Append(meta.FrameCount).Append(",\n");
      sb.Append("\"sessions\": [\n");
      for (int i=0;i<meta.Sessions.Count;i++){
        var s = meta.Sessions[i];
        sb.Append("  {\"sessionId\": ").Append(s.SessionId)
          .Append(", \"startTime\": ").Append(s.StartTime)
          .Append(", \"endTime\": ").Append(s.EndTime)
          .Append(", \"activeDurationMs\": ").Append(s.ActiveDurationMs).Append("}");
        if (i<meta.Sessions.Count-1) sb.Append(",");
        sb.Append("\n");
      }
      sb.Append("]\n}\n");
      string tmp = Path.Combine(dir, "metadata.json.tmp");
      File.WriteAllText(tmp, sb.ToString());
      if (File.Exists(Path.Combine(dir,"metadata.json"))) File.Delete(Path.Combine(dir,"metadata.json"));
      File.Move(tmp, Path.Combine(dir,"metadata.json"));
    }

    static string JsonEscape(string s){ return s.Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","\\r").Replace("\\","\\\\"); }

    public static CanvasMeta ParseMeta(string dir)
    {
      string p = Path.Combine(dir,"metadata.json");
      if (!File.Exists(p)) return null;
      try { return CanvasMeta.Parse(File.ReadAllText(p)); } catch { return null; }
    }

    public static void Append(string timelinePath, uint type, long ts, byte[] payload)
    {
      using (var fs = new FileStream(timelinePath, FileMode.Append, FileAccess.Write, FileShare.Read))
      {
        fs.Write(BitConverter.GetBytes(type),0,4);
        fs.Write(BitConverter.GetBytes(ts),0,8);
        fs.Write(BitConverter.GetBytes(payload.Length),0,4);
        fs.Write(payload,0,payload.Length);
        fs.Flush();
      }
    }

    // 顺序读取所有完整记录；尾部残缺/越界即止（崩溃残留尾部被安全忽略）。
    public static List<Tuple<uint,long,byte[]>> ReadAll(string timelinePath)
    {
      var list = new List<Tuple<uint,long,byte[]>>();
      if (!File.Exists(timelinePath)) return list;
      using (var fs = new FileStream(timelinePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
      using (var br = new BinaryReader(fs))
      {
        while (fs.Position+16 <= fs.Length){
          uint t = br.ReadUInt32(); long ts = br.ReadInt64(); int n = br.ReadInt32();
          if (n<0 || n>(1<<30) || fs.Position+n > fs.Length) break;
          byte[] p = br.ReadBytes(n);
          list.Add(Tuple.Create(t,ts,p));
        }
      }
      return list;
    }
  }

  // 无损 delta：对相邻 BGRA 画布保存变化矩形 + 逐扫描线 RLE。
  // 约定：tw = 每行像素数；行字节宽 pitch = tw*4；总字节 = H*tw*4。
  // 格式：[u32 0x44454C54 "DELT"][u32 dw][u32 dh][u32 minX][u32 minY]
  //       [每行 u32 same(该行相对前帧相同的前缀字节数)][该行剩余 changed 字节]
  public class DeltaCodec
  {
    public static bool IsDelta(byte[] d){ return d!=null && d.Length>=4 && BitConverter.ToUInt32(d,0)==0x44454C54u; }

    public static byte[] EncodeDelta(byte[] prev, byte[] cur, int tw)
    {
      int pitch = tw*4;
      if (prev==null || cur==null || prev.Length!=cur.Length || prev.Length==0) return cur; // 无参照 → 完整帧
      int rows = prev.Length / pitch; if (rows<=0) return cur;
      int cols = tw; // 像素列数
      int minX=int.MaxValue,maxX=-1,minY=int.MaxValue,maxY=-1;
      for (int y=0;y<rows;y++){
        int rb=y*pitch;
        for (int x=0;x<cols;x++){
          int o=rb+x*4;
          if (prev[o]!=cur[o] || prev[o+1]!=cur[o+1] || prev[o+2]!=cur[o+2] || prev[o+3]!=cur[o+3]){
            if (x<minX) minX=x; if (x>maxX) maxX=x;
            if (y<minY) minY=y; if (y>maxY) maxY=y;
          }
        }
      }
      if (maxX<0){ /* 无变化，兜底存完整帧 */ return cur; }
      int dw = maxX-minX+1, dh = maxY-minY+1;
      using (var ms = new MemoryStream()){
        ms.Write(BitConverter.GetBytes((uint)0x44454C54u),0,4);
        ms.Write(BitConverter.GetBytes((uint)dw),0,4);
        ms.Write(BitConverter.GetBytes((uint)dh),0,4);
        ms.Write(BitConverter.GetBytes((uint)minX),0,4);
        ms.Write(BitConverter.GetBytes((uint)minY),0,4);
        int rowLen = dw*4;
        for (int y=0;y<dh;y++){
          int rb=(minY+y)*pitch; int start=minX*4;
          int same=0;
          while (same<rowLen && prev[rb+start+same]==cur[rb+start+same]) same++;
          ms.Write(BitConverter.GetBytes((uint)same),0,4);
          for (int k=same;k<rowLen;k++) ms.WriteByte(cur[rb+start+k]);
        }
        return ms.ToArray();
      }
    }

    public static byte[] Decode(byte[] prev, byte[] data, int tw)
    {
      int pitch = tw*4;
      if (!IsDelta(data)) return DecodeFull(data, tw);
      int dw=(int)BitConverter.ToUInt32(data,4), dh=(int)BitConverter.ToUInt32(data,8);
      int minX=(int)BitConverter.ToUInt32(data,12), minY=(int)BitConverter.ToUInt32(data,16);
      byte[] outArr = (byte[])prev.Clone();
      int pos=20; int rowLen=dw*4;
      for (int y=0;y<dh;y++){
        int same=(int)BitConverter.ToUInt32(data,pos); pos+=4;
        int rb=(minY+y)*pitch, start=minX*4;
        for (int k=same;k<rowLen;k++) outArr[rb+start+k]=data[pos++];
      }
      return outArr;
    }

    // 完整帧（keyframe 或 delta 无参照时）：这里直接无损存原始 BGRA 全图，
    // 第一版优先正确性。后续可换成 PNG/defate 进一步压缩（工程说明允许）。
    public static byte[] CompressFull(byte[] full){ return full; }
    public static byte[] DecodeFull(byte[] data, int tw){ return data; }
  }
}
