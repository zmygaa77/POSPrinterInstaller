using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// POS thermal хэвлэгчид ихэвчлэн ердийн текст бус, шууд ESC/POS тушаалын
    /// байт (raw bytes) хүлээж авдаг. Энгийн PrintDocument классаар GDI
    /// драйверээр дамжуулж хэвлэвэл зарим тушаал (cut paper гэх мэт)
    /// ажиллахгүй байх тохиолдол гардаг тул winspool.drv-ийн Win32 API-г
    /// шууд дуудаж, байтуудыг шууд spooler-т явуулна.
    ///
    /// Энэ бол Windows дээр raw data хэвлэхэд түгээмэл ашигладаг стандарт арга.
    /// </summary>
    public static class RawPrinterHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] ref DOCINFOA di);

        [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        // Шинээр суулгасан хэвлэгчийн Print Queue объект Windows spooler дотор
        // тогтворжиж амжаагүй үед л түр зуур гардаг, дахин оролдвол
        // ихэвчлэн шийдэгддэг Windows алдааны кодууд.
        //   1905 ERROR_PRINTER_DELETED       - "Тухайн принтер устгагдсан"
        //   1906 ERROR_INVALID_PRINTER_STATE - принтерийн төлөв хүчингүй
        //   3003 ERROR_SPL_NO_STARTDOC       - StartDocPrinter дуудагдаагүй
        private static readonly int[] TransientErrorCodes = { 1905, 1906, 3003 };

        /// <summary>
        /// Windows-д суусан printerName нэртэй хэвлэгч рүү raw байтуудыг шууд явуулна.
        /// Шинэ суулгасан хэвлэгчийн хувьд spooler дотор Print Queue объект
        /// хараахан бүрэн тогтворжоогүй байх (race condition) тохиолдолд
        /// (ж: Windows error 1905 - ERROR_PRINTER_DELETED) автоматаар
        /// дахин оролддог.
        /// </summary>
        /// <param name="printerName">Windows дээрх принтерийн нэр (жишээ нь "SPRT POS58")</param>
        /// <param name="data">ESC/POS тушаал болон текст агуулсан raw байтууд</param>
        /// <param name="docName">Хэвлэлтийн job-ийн нэр (spooler дотор харагдана)</param>
        /// <param name="maxRetries">Түр зуурын (transient) алдаа гарвал хэдэн удаа дахин оролдох (анхдагч 3)</param>
        /// <param name="retryDelayMs">Оролдлого хоорондын хүлээх хугацаа, мс (анхдагч 1000)</param>
        public static bool SendBytesToPrinter(string printerName, byte[] data, string docName = "POS Test Print",
            int maxRetries = 3, int retryDelayMs = 1000)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (TrySendOnce(printerName, data, docName, out int win32Error))
                {
                    return true;
                }

                bool isTransient = Array.IndexOf(TransientErrorCodes, win32Error) >= 0;

                if (isTransient && attempt < maxRetries)
                {
                    LogService.Instance.WriteWarning(
                        $"Түр зуурын алдаа гарлаа (Windows error: {win32Error}), {retryDelayMs}мс хүлээгээд " +
                        $"{attempt + 1}-р оролдлого хийнэ ({printerName}).");
                    Thread.Sleep(retryDelayMs);
                    continue;
                }

                // Transient биш алдаа эсвэл сүүлийн оролдлого — цааш дахин оролдохгүй.
                return false;
            }

            return false;
        }

        /// <summary>
        /// Хэвлэх нэг оролдлого. Амжилтгүй бол Windows error кодыг гаргаж өгнө
        /// (retry логикт transient эсэхийг шалгахад ашиглагдана).
        /// </summary>
        private static bool TrySendOnce(string printerName, byte[] data, string docName, out int win32Error)
        {
            win32Error = 0;

            if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            {
                win32Error = Marshal.GetLastWin32Error();
                LogService.Instance.WriteError($"Принтер нээж чадсангүй: {printerName}. Windows error: {win32Error}");
                return false;
            }

            IntPtr pUnmanagedBytes = IntPtr.Zero;

            try
            {
                var di = new DOCINFOA
                {
                    pDocName = docName,
                    pOutputFile = null,
                    pDataType = "RAW"
                };

                if (!StartDocPrinter(hPrinter, 1, ref di))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    LogService.Instance.WriteError($"StartDocPrinter амжилтгүй боллоо. Windows error: {win32Error}");
                    return false;
                }

                if (!StartPagePrinter(hPrinter))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    LogService.Instance.WriteError($"StartPagePrinter амжилтгүй боллоо. Windows error: {win32Error}");
                    EndDocPrinter(hPrinter);
                    return false;
                }

                pUnmanagedBytes = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, pUnmanagedBytes, data.Length);

                bool written = WritePrinter(hPrinter, pUnmanagedBytes, data.Length, out int bytesWritten);
                if (!written)
                {
                    win32Error = Marshal.GetLastWin32Error();
                }

                EndPagePrinter(hPrinter);
                EndDocPrinter(hPrinter);

                if (!written || bytesWritten != data.Length)
                {
                    LogService.Instance.WriteError($"Бүх байт бичигдсэнгүй эсвэл WritePrinter амжилтгүй боллоо. Windows error: {win32Error}");
                    return false;
                }

                LogService.Instance.WriteInfo($"Raw хэвлэлт амжилттай явлаа: {printerName} ({bytesWritten} байт).");
                return true;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Raw хэвлэх явцад алдаа гарлаа", ex);
                return false;
            }
            finally
            {
                if (pUnmanagedBytes != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pUnmanagedBytes);
                }
                ClosePrinter(hPrinter);
            }
        }
    }
}
