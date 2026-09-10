using System;
using System.Windows;
using System.Windows.Threading;
using POSPrinterInstaller.Services;

namespace POSPrinterInstaller
{
    /// <summary>
    /// Программын нэвтрэх цэг. Энд бид гэнэтийн алдаа (unhandled exception)-г
    /// барьж аваад Logs фолдер руу бичээд, хэрэглэгчид ойлгомжтой мессеж
    /// харуулна. Ингэснээр програм гэнэт хаагдахгүй.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // UI thread дээр гарсан барьгдаагүй алдааг барих
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // Background thread дээр гарсан алдааг барих
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            LogService.Instance.WriteInfo("Программ асаагдлаа (App started).");
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogService.Instance.WriteError("UI алдаа гарлаа", e.Exception);
            MessageBox.Show(
                $"Гэнэтийн алдаа гарлаа:\n{e.Exception.Message}\n\nДэлгэрэнгүй мэдээллийг Logs фолдероос харна уу.",
                "Алдаа",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogService.Instance.WriteError("Системийн алдаа гарлаа", ex);
            }
        }
    }
}
