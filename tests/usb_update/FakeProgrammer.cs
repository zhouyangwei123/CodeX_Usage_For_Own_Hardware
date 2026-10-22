using System;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
class FakeProgrammer {
 static int Main(string[] args) {
  Console.OutputEncoding=new UTF8Encoding(false);
  if(args.Length>0 && args[0]=="--slow"){Thread.Sleep(10000);return 0;}
  if(args.Length>0 && args[0]=="--error"){Console.WriteLine("Error: simulated failure");return 0;}
  if(args.Length>0 && args[0]=="--exit"){Console.Error.WriteLine("failed");return 2;}
  Console.WriteLine(new JavaScriptSerializer().Serialize(args));return 0;
 }
}
