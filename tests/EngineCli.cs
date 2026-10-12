using System;
using System.IO;
using System.Linq;
using System.Threading;
using QingZip;
class EngineCli {
 static int Main(string[] args) {
  try {
   if(args[0]=="inspect") {var c=ArchiveEngine.Inspect(args[1],args.Length>2?int.Parse(args[2]):0);Console.WriteLine("OK "+c.Items.Count+" "+c.TotalSize);}
   else if(args[0]=="extract") Console.WriteLine(ArchiveEngine.Extract(args[1],args[2],0,CancellationToken.None,null));
   else if(args[0]=="create") ArchiveEngine.Create(args.Skip(2),args[1],false,CancellationToken.None,null);
   else if(args[0]=="store") ArchiveEngine.Create(args.Skip(2),args[1],true,CancellationToken.None,null);
   else if(args[0]=="cancel-extract") {var cts=new CancellationTokenSource();ArchiveEngine.Extract(args[1],args[2],0,cts.Token,p=>cts.Cancel());}
   else if(args[0]=="cancel-create") {var cts=new CancellationTokenSource();ArchiveEngine.Create(args.Skip(2),args[1],false,cts.Token,p=>cts.Cancel());}
   else throw new ArgumentException("Unknown test operation");
   return 0;
  }catch(OperationCanceledException){Console.Error.WriteLine("CANCELLED");return 3;}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 2;}
 }
}
