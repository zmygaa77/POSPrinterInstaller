using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Суулгахаас ӨМНӨ ажиллаж, өмнөх амжилтгүй/давхардсан суулгалтуудаас
    /// үлдсэн "хий" (жишээ нь Windows-ийн PrinterStatus = Error,
    /// PendingDeletion зогсонги төлөвт орсон) болон "(Copy 1)" гэх мэт
    /// давхардсан принтерийн бичлэгүүдийг олж устгадаг сервис.
    ///
    /// Яагаад хэрэгтэй вэ:
    /// Windows-ийн Print Spooler заримдаа принтерийг устгах/дахин нэмэх
    /// (ялангуяа USB виртуал порт өөрчлөгдөх үед) явцад бичлэгийг бүрэн
    /// цэвэрлэж чадахгүй, "PendingDeletion" төлөвт орхидог. Ийм бичлэг нь:
    ///   - WMI/GDI-ээр (Get-Printer, Win32_Printer) харагдсаар байдаг тул
    ///     апп өөрөө "Хэвлэгч нэмэгдсэн" гэж андуурч болно
    ///   - Гэвч Windows Control Panel/Settings-ийн "Принтер, сканер" UI
    ///     жагсаалтаас ХАСАГДДАГ тул хэрэглэгчид харагдахгүй
    ///   - StartDocPrinter дуудахад Windows error 1905
    ///     (ERROR_PRINTER_DELETED) буцаадаг
    /// </summary>
    public class PrinterCleanupService
    {
        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool DeletePrinter(IntPtr hPrinter);

        /// <summary>
        /// exactName-тай яг таарах, эсвэл namePrefix-ээр эхэлдэг (жишээ нь
        /// Windows-ийн автоматаар нэмдэг "(Copy 1)" гэх мэт давхардсан
        /// хувилбарууд) бүх принтерийн бичлэгийг олж устгана. Хэрэв энгийн
        /// устгалт амжилтгүй болвол (PendingDeletion зогсонги байдал) Print
        /// Spooler үйлчилгээг дахин эхлүүлж, дараа нь дахин оролдоно.
        /// </summary>
        /// <param name="exactName">Яг таарах ёстой принтерийн нэр (жишээ нь "SEWOO TECH LK-B30II")</param>
        /// <param name="namePrefix">Давхардсан хувилбаруудыг олоход ашиглах угтвар (жишээ нь "Sewoo-B30")</param>
        /// <param name="log">Явцын мессеж илгээх callback (сонголтоор)</param>
        public async Task CleanupStalePrintersAsync(string exactName, string namePrefix, Action<string>? log = null)
        {
            try
            {
                var stale = FindMatchingPrinters(exactName, namePrefix);
                if (stale.Count == 0)
                {
                    return;
                }

                log?.Invoke($"Хуучин/давхардсан {stale.Count} принтерийн бичлэг олдлоо, цэвэрлэж байна...");
                LogService.Instance.WriteInfo(
                    $"Цэвэрлэх шаардлагатай хуучин принтерийн бичлэгүүд: {string.Join(", ", stale)}");

                bool anyFailed = false;
                foreach (var name in stale)
                {
                    if (!TryDeletePrinter(name))
                    {
                        anyFailed = true;
                    }
                }

                if (anyFailed)
                {
                    log?.Invoke(
                        "Зарим бичлэгийг шууд устгаж чадсангүй (магадгүй 'PendingDeletion' " +
                        "зогсонги төлөвт орсон). Print Spooler-ийг дахин эхлүүлж байна...");
                    await RestartSpoolerAsync();
                    await Task.Delay(1500);

                    var stillStale = FindMatchingPrinters(exactName, namePrefix);
                    foreach (var name in stillStale)
                    {
                        TryDeletePrinter(name);
                    }
                }

                log?.Invoke("Хуучин принтерийн бичлэгүүдийг цэвэрлэж дууслаа.");
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Хуучин принтерийн бичлэг цэвэрлэх явцад алдаа гарлаа", ex);
            }
        }

        private List<string> FindMatchingPrinters(string exactName, string namePrefix)
        {
            var result = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Printer");
                foreach (ManagementObject printer in searcher.Get())
                {
                    var name = printer["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    bool matches = string.Equals(name, exactName, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(namePrefix) &&
                            name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase));

                    if (matches)
                    {
                        result.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("WMI-ээр хуучин принтерийн бичлэг хайх явцад алдаа гарлаа", ex);
            }

            return result;
        }

        private bool TryDeletePrinter(string printerName)
        {
            if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            {
                LogService.Instance.WriteWarning(
                    $"'{printerName}' принтерийг нээж чадсангүй (устгах гэж байхад). " +
                    $"Windows error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            try
            {
                if (!DeletePrinter(hPrinter))
                {
                    LogService.Instance.WriteWarning(
                        $"'{printerName}' принтерийг устгаж чадсангүй. Windows error: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                LogService.Instance.WriteInfo($"'{printerName}' хуучин принтерийн бичлэгийг амжилттай устгалаа.");
                return true;
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }

        /// <summary>
        /// Print Spooler (spooler) үйлчилгээг Администраторын эрхээр дахин
        /// эхлүүлнэ. "PendingDeletion" зэрэг зогсонги төлөвт орсон принтерийн
        /// бичлэгийг цэвэрлэхэд ихэвчлэн энэ л шаардлагатай байдаг.
        /// </summary>
        private async Task RestartSpoolerAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c net stop spooler & net start spooler",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                await process.WaitForExitAsync();

                LogService.Instance.WriteInfo(
                    $"Print Spooler дахин эхлүүлэх оролдлого хийлээ (Exit code: {process.ExitCode}).");
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Print Spooler дахин эхлүүлэх явцад алдаа гарлаа", ex);
            }
        }
    }
}
