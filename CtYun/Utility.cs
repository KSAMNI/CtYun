using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CtYun
{
    internal class Utility
    {
        private static readonly object SyncRoot = new();

        public static void WriteLine(ConsoleColor consolecolor, object value)
        {
            // 多账号/多台云电脑并发输出时避免控制台颜色串台
            lock (SyncRoot)
            {
                Console.ForegroundColor = consolecolor;
                Console.WriteLine(Time() + value);
            }
        }
        private static string Time()
        {
            return "[" + DateTime.Now.ToString("HH:mm:ss.ff") + "] ";
        }
    }
}
