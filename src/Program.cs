using System;
using System.Threading;
using System.Windows.Forms;

namespace BatteryWatch
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\BatteryWatch_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("外设电量监视已在运行（看任务栏右下角）。",
                                    "BatteryWatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try { SetProcessDPIAware(); }
                catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
