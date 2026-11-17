using Microsoft.UI.Xaml;
using System;

namespace TableToolbox
{
    public static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.Start(p => new App());
        }
    }
}
