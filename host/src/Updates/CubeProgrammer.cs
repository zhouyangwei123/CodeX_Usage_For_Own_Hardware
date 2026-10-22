using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CodexToolsHost.Updates
{
    public sealed class CubeProgrammer
    {
        private readonly string _executable;
        public CubeProgrammer(string executable)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("请选择已安装的 STM32_Programmer_CLI.exe。");
            _executable = Path.GetFullPath(executable);
        }
        internal static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                quoted.Append(c);
                slashes = 0;
            }
            return quoted.Append('\\', slashes * 2).Append('"').ToString();
        }
        public string Run(string[] arguments, int timeoutMs)
        {
            var start = new ProcessStartInfo(_executable, string.Join(" ",arguments.Select(Quote))) {
                UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true,
                RedirectStandardError=true, StandardOutputEncoding=Encoding.UTF8,
                StandardErrorEncoding=Encoding.UTF8 };
            using (var process=Process.Start(start))
            {
                Task<string> stdout=process.StandardOutput.ReadToEndAsync();
                Task<string> stderr=process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    if(!process.WaitForExit(5000))
                        throw new TimeoutException("烧录工具未能停止，请检查设备连接后关闭烧录工具。");
                    throw new TimeoutException("STM32烧录工具操作超时；设备可通过KEY1上电恢复。");
                }
                if(!Task.WaitAll(new Task[]{stdout,stderr},5000))
                    throw new TimeoutException("烧录工具输出未结束。");
                string output=stdout.GetAwaiter().GetResult()+"\n"+stderr.GetAwaiter().GetResult();
                if(process.ExitCode!=0 || Regex.IsMatch(output,@"(?im)^\s*Error\b"))
                    throw new IOException("STM32烧录工具失败：\n"+output.Substring(Math.Max(0,output.Length-1800)));
                return output;
            }
        }
    }
}
