using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace QingZip {
    public sealed class ZipItem {
        public string Name; public long Size, PackedSize; public uint Crc, Attributes;
        public ushort Flags, Method; public bool IsDirectory;
    }
    public sealed class ZipCatalog {
        public List<ZipItem> Items = new List<ZipItem>();
        public long TotalSize { get { return Items.Sum(x => x.Size); } }
    }
    public sealed class WorkProgress {
        public int Percent; public string Message;
        public WorkProgress(int percent, string message) { Percent = percent; Message = message; }
    }
    public static class ArchiveEngine {
        public const long SizeLimit = 4L * 1024 * 1024 * 1024;
        const int EntryLimit = 100000;
        static readonly uint[] CrcTable = MakeCrcTable();
        static uint[] MakeCrcTable() {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint v = i; for (int n=0;n<8;n++) v=(v&1)!=0 ? 0xedb88320U^(v>>1) : v>>1; t[i]=v; }
            return t;
        }
        static uint UpdateCrc(uint v, byte[] bytes, int count) { for(int i=0;i<count;i++) v=CrcTable[(v^bytes[i])&255]^(v>>8); return v; }
        static byte[] ReadExactly(BinaryReader r, int n) { byte[] b=r.ReadBytes(n); if(b.Length!=n) throw new InvalidDataException("ZIP 文件不完整。"); return b; }
        static long AsLong(ulong n) { if(n>long.MaxValue) throw new InvalidDataException("ZIP 长度字段无效。"); return (long)n; }
        static string DecodeName(byte[] name, ushort flags, int codePage, byte[] extra) {
            if ((flags & 2048)!=0) return new UTF8Encoding(false,true).GetString(name);
            // Info-ZIP Unicode Path extra field, validated against the legacy name.
            for(int p=0;p+4<=extra.Length;) {
                int tag=BitConverter.ToUInt16(extra,p),length=BitConverter.ToUInt16(extra,p+2);p+=4;
                if(length>extra.Length-p) throw new InvalidDataException("ZIP 扩展字段损坏。");
                if(tag==0x7075 && length>=5 && extra[p]==1 && BitConverter.ToUInt32(extra,p+1)==(UpdateCrc(uint.MaxValue,name,name.Length)^uint.MaxValue))
                    return new UTF8Encoding(false,true).GetString(extra,p+5,length-5);
                p+=length;
            }
            if(codePage!=0) return Encoding.GetEncoding(codePage).GetString(name);
            try { return new UTF8Encoding(false,true).GetString(name); } catch(DecoderFallbackException) { return Encoding.GetEncoding(936).GetString(name); }
        }
        public static ZipCatalog Inspect(string path, int codePage) {
            using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) return ReadCatalog(f,codePage);
        }
        static ZipCatalog ReadCatalog(Stream f, int codePage) {
            if(f.Length<22) throw new InvalidDataException("文件不是完整的 ZIP 压缩包。");
            var r=new BinaryReader(f,Encoding.UTF8,true);
            int tailLength=(int)Math.Min(f.Length,65557); f.Position=f.Length-tailLength; byte[] tail=ReadExactly(r,tailLength);
            int end=-1;
            for(int p=tail.Length-22;p>=0;p--) if(BitConverter.ToUInt32(tail,p)==0x06054b50U && p+22+BitConverter.ToUInt16(tail,p+20)==tail.Length) { end=p; break; }
            if(end<0) throw new InvalidDataException("找不到 ZIP 目录。文件可能损坏或格式不受支持。");
            long endPosition=f.Length-tailLength+end;
            if(BitConverter.ToUInt16(tail,end+4)!=0 || BitConverter.ToUInt16(tail,end+6)!=0) throw new NotSupportedException("当前版本不支持分卷 ZIP。");
            ulong count=BitConverter.ToUInt16(tail,end+10), directorySize=BitConverter.ToUInt32(tail,end+12), offset=BitConverter.ToUInt32(tail,end+16);
            if(count==65535 || directorySize==uint.MaxValue || offset==uint.MaxValue) {
                if(endPosition<20) throw new InvalidDataException("ZIP64 目录缺失。");
                f.Position=endPosition-20;
                if(r.ReadUInt32()!=0x07064b50U || r.ReadUInt32()!=0) throw new InvalidDataException("ZIP64 定位信息无效。");
                long zip64Position=AsLong(r.ReadUInt64()); if(r.ReadUInt32()!=1 || zip64Position<0 || zip64Position>f.Length-56) throw new NotSupportedException("不支持分卷或无效的 ZIP64。");
                f.Position=zip64Position;
                if(r.ReadUInt32()!=0x06064b50U || r.ReadUInt64()<44) throw new InvalidDataException("ZIP64 目录无效。");
                r.ReadUInt16();r.ReadUInt16();if(r.ReadUInt32()!=0 || r.ReadUInt32()!=0) throw new NotSupportedException("不支持分卷 ZIP64。");
                ulong diskCount=r.ReadUInt64();count=r.ReadUInt64();if(diskCount!=count) throw new InvalidDataException("ZIP64 条目数无效。");
                directorySize=r.ReadUInt64();offset=r.ReadUInt64();
            } else if(BitConverter.ToUInt16(tail,end+8)!=count) throw new NotSupportedException("不支持分卷 ZIP。");
            long start=AsLong(offset), directoryBytes=AsLong(directorySize);
            if(count>EntryLimit) throw new NotSupportedException("压缩包条目超过 100,000 个。");
            if(start<0 || directoryBytes<0 || start>endPosition || directoryBytes>endPosition-start) throw new InvalidDataException("ZIP 目录范围无效。");
            f.Position=start; long directoryEnd=start+directoryBytes, expanded=0;
            var catalog=new ZipCatalog(); var paths=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase); var declared=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(ulong i=0;i<count;i++) {
                if(f.Position>directoryEnd-46 || r.ReadUInt32()!=0x02014b50U) throw new InvalidDataException("ZIP 条目目录损坏。");
                r.ReadUInt16();r.ReadUInt16();ushort flags=r.ReadUInt16(), method=r.ReadUInt16();r.ReadUInt32();uint crc=r.ReadUInt32();
                ulong packed=r.ReadUInt32(), size=r.ReadUInt32();ushort nameLen=r.ReadUInt16(), extraLen=r.ReadUInt16(), commentLen=r.ReadUInt16();
                ushort disk=r.ReadUInt16();r.ReadUInt16();uint attrs=r.ReadUInt32();uint localOffset=r.ReadUInt32();
                if(f.Position+(long)nameLen+extraLen+commentLen>directoryEnd) throw new InvalidDataException("ZIP 文件名范围无效。");
                byte[] nameBytes=ReadExactly(r,nameLen), extra=ReadExactly(r,extraLen);ReadExactly(r,commentLen);
                if(size==uint.MaxValue || packed==uint.MaxValue || localOffset==uint.MaxValue || disk==65535) {
                    bool got=false;
                    for(int p=0;p+4<=extra.Length;) { int tag=BitConverter.ToUInt16(extra,p), len=BitConverter.ToUInt16(extra,p+2);p+=4;if(p+len>extra.Length) throw new InvalidDataException("ZIP 扩展字段损坏。");
                        if(tag==1) { int q=p; if(size==uint.MaxValue) { if(q+8>p+len) throw new InvalidDataException("ZIP64 长度缺失。");size=BitConverter.ToUInt64(extra,q);q+=8; }
                            if(packed==uint.MaxValue) { if(q+8>p+len) throw new InvalidDataException("ZIP64 长度缺失。");packed=BitConverter.ToUInt64(extra,q);q+=8; }
                            if(localOffset==uint.MaxValue) { if(q+8>p+len) throw new InvalidDataException("ZIP64 偏移缺失。");q+=8; }
                            if(disk==65535) { if(q+4>p+len || BitConverter.ToUInt32(extra,q)!=0) throw new NotSupportedException("不支持分卷 ZIP64。");disk=0; } got=true;break; }
                        p+=len;
                    }
                    if(!got) throw new InvalidDataException("ZIP64 扩展字段缺失。");
                }
                if(disk!=0) throw new NotSupportedException("不支持分卷 ZIP。");
                if((flags&1)!=0 || (flags&64)!=0) throw new NotSupportedException("当前版本不支持加密 ZIP，请先使用原软件解密。");
                if(method!=0 && method!=8) throw new NotSupportedException("此 ZIP 使用了暂不支持的压缩方法（"+method+"）。支持存储和 Deflate。");
                if(((attrs>>16)&0xf000)==0xa000) throw new InvalidDataException("为避免写入异常位置，不解压符号链接条目。");
                string name=DecodeName(nameBytes,flags,codePage,extra);bool isDirectory=name.EndsWith("/")||name.EndsWith("\\");
                string relative=ValidateName(name,isDirectory);
                if(!declared.Add(relative)) throw new InvalidDataException("ZIP 包含重复或大小写冲突的路径："+name);
                string[] parts=relative.Split('/');string partPath="";
                for(int j=0;j<parts.Length;j++) { partPath+=(j==0?"":"/")+parts[j]; bool isDir=j<parts.Length-1||isDirectory;bool previous;
                    if(paths.TryGetValue(partPath,out previous) && previous!=isDir) throw new InvalidDataException("ZIP 文件与文件夹路径冲突："+name);
                    paths[partPath]=isDir;
                }
                long length=AsLong(size);if(length>SizeLimit-expanded) throw new NotSupportedException("此版本每次最多处理 4 GB 的原始数据。");expanded+=length;
                if(isDirectory && length!=0) throw new InvalidDataException("目录条目的长度无效。");
                catalog.Items.Add(new ZipItem {Name=name,Size=length,PackedSize=AsLong(packed),Crc=crc,Flags=flags,Method=method,Attributes=attrs,IsDirectory=isDirectory});
            }
            return catalog;
        }
        public static string ValidateName(string name, bool directory) {
            if(string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("ZIP 包含空路径。");
            string normalized=name.Replace('\\','/');if(normalized.StartsWith("/") || normalized.Contains(":")) throw new InvalidDataException("ZIP 包含绝对路径或异常路径："+name);
            if(directory) normalized=normalized.TrimEnd('/');
            foreach(string part in normalized.Split('/')) {
                if(part.Length==0 || part=="." || part==".." || part.EndsWith(".") || part.EndsWith(" ")) throw new InvalidDataException("ZIP 包含不安全的路径："+name);
                if(part.IndexOfAny(new[]{'<','>','"','|','?','*'})>=0 || part.Any(c=>c<32)) throw new InvalidDataException("文件名无法在 Windows 上使用："+name);
                string device=part.Split('.')[0].ToUpperInvariant();
                if(device=="CON"||device=="PRN"||device=="AUX"||device=="NUL"||device=="CLOCK$" || (device.Length==4 && (device.StartsWith("COM")||device.StartsWith("LPT")) && (device[3]>='1'&&device[3]<='9' || "¹²³".Contains(device[3])))) throw new InvalidDataException("ZIP 包含系统保留文件名："+name);
            }
            return normalized;
        }
        static void CheckNoLinks(string path) {
            string current=Path.GetFullPath(path);
            while(!string.IsNullOrEmpty(current)) { if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("目标或源路径包含链接，请选择普通文件夹。"); current=Path.GetDirectoryName(current); }
        }
        static void Report(Action<WorkProgress> progress,long done,long total,string message) { if(progress!=null) progress(new WorkProgress(total==0?0:(int)Math.Min(99,done*100/total),message)); }
        static void RemoveOwnedStage(string stage) {
            if(!Directory.Exists(stage)) return;
            foreach(string child in Directory.GetFileSystemEntries(stage)) { FileAttributes a=File.GetAttributes(child);if((a&FileAttributes.Directory)!=0) { if((a&FileAttributes.ReparsePoint)!=0) Directory.Delete(child,false);else RemoveOwnedStage(child); }else { File.SetAttributes(child,FileAttributes.Normal);File.Delete(child); } }
            Directory.Delete(stage,false);
        }
        public static string Extract(string archivePath,string destination,int codePage,CancellationToken token,Action<WorkProgress> progress) {
            destination=Path.GetFullPath(destination);string parent=Path.GetDirectoryName(destination);CheckNoLinks(parent);
            if(!Directory.Exists(parent)) throw new DirectoryNotFoundException("目标文件夹不存在。");
            if(Directory.Exists(destination)||File.Exists(destination)) throw new IOException("目标已存在，请选择一个新的解压文件夹。");
            string stage=destination+".partial-"+Guid.NewGuid().ToString("N");bool committed=false;
            try {
                using(var f=new FileStream(archivePath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    ZipCatalog catalog=ReadCatalog(f,codePage);f.Position=0;token.ThrowIfCancellationRequested();Directory.CreateDirectory(stage);CheckNoLinks(stage);
                    using(var zip=new ZipArchive(f,ZipArchiveMode.Read,true,Encoding.GetEncoding(codePage==0?936:codePage))) {
                        if(zip.Entries.Count!=catalog.Items.Count) throw new InvalidDataException("ZIP 条目数量不一致。");
                        byte[] buffer=new byte[65536];long done=0;
                        for(int i=0;i<catalog.Items.Count;i++) { token.ThrowIfCancellationRequested();ZipItem item=catalog.Items[i];
                            string relative=ValidateName(item.Name,item.IsDirectory), target=Path.GetFullPath(Path.Combine(stage,relative.Replace('/',Path.DirectorySeparatorChar)));
                            if(!target.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || target.Length>=248) throw new InvalidDataException("条目路径超出目标或过长："+item.Name);
                            if(item.IsDirectory) { Directory.CreateDirectory(target);CheckNoLinks(target);continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));CheckNoLinks(Path.GetDirectoryName(target));
                            var entry=zip.Entries[i];if(entry.Length!=item.Size) throw new InvalidDataException("ZIP 长度信息不一致。");
                            uint crc=uint.MaxValue;long written=0;
                            using(var input=entry.Open()) using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                                int n;while((n=input.Read(buffer,0,buffer.Length))>0) { token.ThrowIfCancellationRequested();if(n>item.Size-written) throw new InvalidDataException("解压数据超出声明长度。");output.Write(buffer,0,n);written+=n;done+=n;crc=UpdateCrc(crc,buffer,n);Report(progress,done,catalog.TotalSize,"正在解压："+item.Name); }
                            }
                            if(written!=item.Size || (crc^uint.MaxValue)!=item.Crc) throw new InvalidDataException("完整性校验失败："+item.Name);
                            try { File.SetLastWriteTime(target,entry.LastWriteTime.LocalDateTime); }catch(ArgumentException) { }
                        }
                    }
                }
                token.ThrowIfCancellationRequested();Directory.Move(stage,destination);committed=true;if(progress!=null) progress(new WorkProgress(100,"解压完成"));return destination;
            }finally { if(!committed) RemoveOwnedStage(stage); }
        }
        sealed class SourceItem { public string FullPath,Name;public bool Directory;public long Size; }
        static void Collect(string full,string name,List<SourceItem> items,string destination) {
            if(items.Count>=EntryLimit) throw new NotSupportedException("文件数量超过 100,000 个。");
            if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0) throw new IOException("请移除链接文件或链接文件夹："+full);
            bool dir=Directory.Exists(full);ValidateName(name,dir);
            if(dir) { items.Add(new SourceItem {FullPath=full,Name=name.TrimEnd('/')+"/",Directory=true});foreach(string child in Directory.GetFileSystemEntries(full)) { if(string.Equals(Path.GetFullPath(child),destination,StringComparison.OrdinalIgnoreCase)) continue;Collect(child,name.TrimEnd('/')+"/"+Path.GetFileName(child),items,destination); } }
            else { if(string.Equals(Path.GetFullPath(full),destination,StringComparison.OrdinalIgnoreCase)) throw new IOException("不能把输出 ZIP 自身作为输入。");items.Add(new SourceItem {FullPath=full,Name=name,Size=new FileInfo(full).Length}); }
        }
        public static void Create(IEnumerable<string> sources,string destination,bool store,CancellationToken token,Action<WorkProgress> progress) {
            destination=Path.GetFullPath(destination);CheckNoLinks(Path.GetDirectoryName(destination));var items=new List<SourceItem>();
            foreach(string source in sources.Distinct(StringComparer.OrdinalIgnoreCase)) { string full=Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);CheckNoLinks(full);if(!File.Exists(full)&&!Directory.Exists(full)) throw new FileNotFoundException("源文件不存在："+full);Collect(full,Path.GetFileName(full),items,destination); }
            if(items.Count==0) throw new IOException("请先选择文件或文件夹。");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var item in items) if(!names.Add(item.Name.TrimEnd('/'))) throw new IOException("输入文件或文件夹的名称重复，请分开压缩："+item.Name);
            long total=0;foreach(var item in items) { if(item.Size>SizeLimit-total) throw new NotSupportedException("此版本每次最多处理 4 GB 的原始数据。");total+=item.Size; }
            string temporary=destination+".partial-"+Guid.NewGuid().ToString("N");bool committed=false;
            try { long done=0;byte[] buffer=new byte[65536];
                using(var f=new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)) using(var zip=new ZipArchive(f,ZipArchiveMode.Create,false,Encoding.UTF8)) {
                    foreach(var item in items) { token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(item.Name,store?CompressionLevel.NoCompression:CompressionLevel.Optimal);
                        DateTime date=item.Directory?Directory.GetLastWriteTime(item.FullPath):File.GetLastWriteTime(item.FullPath);if(date.Year<1980)date=new DateTime(1980,1,1);if(date.Year>2107)date=new DateTime(2107,12,31);entry.LastWriteTime=new DateTimeOffset(date);
                        if(item.Directory)continue;
                        using(var input=new FileStream(item.FullPath,FileMode.Open,FileAccess.Read,FileShare.Read)) using(var output=entry.Open()) { int n;while((n=input.Read(buffer,0,buffer.Length))>0) { token.ThrowIfCancellationRequested();if(n>SizeLimit-done)throw new IOException("输入数据超过 4 GB。");output.Write(buffer,0,n);done+=n;Report(progress,done,total,"正在压缩："+item.Name); } }
                    }
                }
                token.ThrowIfCancellationRequested();if(File.Exists(destination)) File.Replace(temporary,destination,null);else File.Move(temporary,destination);committed=true;if(progress!=null)progress(new WorkProgress(100,"ZIP 创建完成"));
            }finally { if(!committed&&File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
