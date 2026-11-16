// 入口：单实例 + 托盘常驻。

using System;
using System.Threading;
using System.Windows.Forms;

namespace VbeComplete
{
    internal static class Program
    {
        private static Mutex _mutex;

        public const string ShowEventName = @"Local\VbeComplete_CSharp_v1_show";

        [STAThread]
        private static int Main(string[] args)
        {
            // 崩溃必须让用户看见：WinExe 没有控制台，异常被吞掉之后用户只看到闪退。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowFatal("界面线程", e.Exception);
            AppDomain.CurrentDomain.UnhandledException +=
                (s, e) => ShowFatal("后台线程", e.ExceptionObject as Exception);

            try { return RunMain(args); }
            catch (Exception e)
            {
                ShowFatal("启动", e);
                return 1;
            }
        }

        // 致命错误当面弹框，不落文件。
        private static void ShowFatal(string where, Exception e)
        {
            try
            {
                MessageBox.Show(
                    where + "发生未处理的错误：\r\n\r\n" + (e?.Message ?? "未知错误"),
                    "VbeComplete", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static int RunMain(string[] args)
        {
            Settings.Load();

            // 没有主窗口、也没有设置界面：不带参数启动就行（片段窗走托盘菜单 / Ctrl+Q）。
            if (args.Length > 0)
            {
                // GUI 程序没有控制台，用法说明走对话框，别往 stdout 写。
                try
                {
                    MessageBox.Show(
                        "用法：VbeComplete.exe\r\n\r\n" +
                        "  不带参数 = 正常启动，托盘常驻",
                        "VbeComplete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return 2;
            }

            // 单实例：同名互斥体已经存在就说明有一份在跑。
            bool created;
            _mutex = new Mutex(true, "VbeComplete_CSharp_v1", out created);
            if (!created)
            {
                SignalExisting();
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var app = new App();
            app.Start();

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; app.RunOnUi(app.Shutdown); };

            Application.Run(app);
            app.Shutdown();
            try { _mutex.ReleaseMutex(); } catch { }
            return 0;
        }

        private static void SignalExisting()
        {
            EventWaitHandle h;
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out h))
            {
                try { h.Set(); } catch { }
                h.Dispose();
            }
        }
    }
}
