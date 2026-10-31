// Program.cs - 入口
// SRT 自动字幕生成 GUI (.NET Framework 4.8)
// 编译: csc /nologo /target:winexe /out:srt_gui.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Program.cs MainForm.cs Config.cs Pipeline.cs ProcRunner.cs

using System;
using System.Windows.Forms;

namespace SrtGui
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
