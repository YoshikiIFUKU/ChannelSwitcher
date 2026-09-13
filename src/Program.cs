using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ChannelSwitcher
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "ChannelSwitcher_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Channel Switcher はすでに起動しています。", "Channel Switcher");
                    return;
                }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
