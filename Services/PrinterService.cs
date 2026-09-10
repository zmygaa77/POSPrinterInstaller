using System;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Management;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Windows дээрх принтерийн жагсаалттай харьцдаг сервис.
    /// Driver суулгасны дараа тухайн принтер жагсаалтад бодитоор
    /// нэмэгдсэн эсэхийг шалгах, шаардлагатай бол default принтер
    /// болгож тохируулах зорилготой.
    /// </summary>
    public class PrinterService
    {
        /// <summary>
        /// Windows-ийн одоогийн суусан принтерүүдийн нэрсийг GDI (winspool)
        /// API-аар буцаана. Зарим тохиолдолд (ялангуяа Администратор эрхээр
        /// elevated процессоос дуудахад) энэ жагсаалт саяхан нэмэгдсэн
        /// принтерийг шууд харуулахгүй байх нь мэдэгдэж буй асуудал байдаг
        /// тул баталгаажуулахдаа IsPrinterInstalled нь үүнээс гадна WMI-ийг
        /// ч бас нэмэлтээр шалгадаг.
        /// </summary>
        public string[] GetInstalledPrinterNames()
        {
            return PrinterSettings.InstalledPrinters
                .Cast<string>()
                .ToArray();
        }

        /// <summary>
        /// Windows-ийн spooler үйлчилгээнээс шууд, WMI (Win32_Printer) ашиглан
        /// суусан принтерүүдийн нэрсийг буцаана. Энэ нь GDI-ийн
        /// PrinterSettings.InstalledPrinters-ээс илүү найдвартай — учир нь
        /// дуудаж буй процессийн хэрэглэгчийн session/window station-аас
        /// үл хамааран, spooler-т бодитоор бүртгэгдсэн бүх принтерийг
        /// шууд харуулна (elevated процессоос дуудсан ч ялгаагүй).
        /// </summary>
        public string[] GetInstalledPrinterNamesViaWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Printer");
                return searcher.Get()
                    .Cast<ManagementObject>()
                    .Select(p => p["Name"]?.ToString() ?? string.Empty)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToArray();
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("WMI-ээр принтер жагсаалт авах явцад алдаа гарлаа", ex);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Өгөгдсөн нэртэй принтер Windows-д суусан эсэхийг шалгана
        /// (том жижиг үсэгт мэдрэмтгий биш, орчны зайг тооцно). Эхлээд
        /// GDI-ийн жагсаалтаас хайж, олдоогүй бол WMI-ээр давхар шалгана —
        /// учир нь elevated процессоос GDI жагсаалт заримдаа саяхан
        /// нэмэгдсэн принтерийг харуулдаггүй мэдэгдэж буй асуудал байдаг.
        /// </summary>
        public bool IsPrinterInstalled(string printerName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;

            var trimmedTarget = printerName.Trim();

            bool foundViaGdi = GetInstalledPrinterNames()
                .Any(p => string.Equals(p.Trim(), trimmedTarget, StringComparison.OrdinalIgnoreCase));

            if (foundViaGdi) return true;

            bool foundViaWmi = GetInstalledPrinterNamesViaWmi()
                .Any(p => string.Equals(p.Trim(), trimmedTarget, StringComparison.OrdinalIgnoreCase));

            if (foundViaWmi)
            {
                LogService.Instance.WriteInfo(
                    $"'{printerName}' GDI (PrinterSettings.InstalledPrinters) жагсаалтад олдоогүй ч " +
                    "WMI (Win32_Printer)-ээр олдлоо — GDI кэш саяхны өөрчлөлтийг харуулаагүй байж болно.");
            }

            return foundViaWmi;
        }

        /// <summary>
        /// Windows-ийн Win32_Printer WMI класс ашиглан өгөгдсөн принтерийг
        /// системийн үндсэн (default) принтер болгож тохируулна.
        /// </summary>
        public bool SetDefaultPrinter(string printerName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Printer WHERE Name = '{EscapeForWmi(printerName)}'");

                foreach (ManagementObject printer in searcher.Get())
                {
                    var result = printer.InvokeMethod("SetDefaultPrinter", null);
                    var returnValue = Convert.ToUInt32(result);

                    if (returnValue == 0)
                    {
                        LogService.Instance.WriteInfo($"'{printerName}'-ийг үндсэн принтер болгож тохирууллаа.");
                        return true;
                    }

                    LogService.Instance.WriteWarning($"SetDefaultPrinter буцаасан код: {returnValue}");
                    return false;
                }

                LogService.Instance.WriteWarning($"Default болгох гэсэн принтер олдсонгүй: {printerName}");
                return false;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Үндсэн принтер тохируулах явцад алдаа гарлаа", ex);
                return false;
            }
        }
        /// <summary>
        /// rundll32.exe-ийн ЖИНХЭНЭ (64-биттэй) хувилбарын бүтэн замыг олж
        /// буцаана. Хэрэв энэ .NET процесс өөрөө 32-биттэй горимоор
        /// ажиллаж байгаа бол (жишээ нь .csproj-д Prefer32Bit=true эсвэл
        /// PlatformTarget=x86 тохиргоотой бол), Environment.SystemDirectory
        /// БОЛОН шууд "C:\Windows\System32" гэсэн текст зам хүртэл WOW64
        /// File System Redirector-ээр АВТОМААР "C:\Windows\SysWOW64"-руу
        /// (32-биттэй хувилбар руу) чимээгүй солигддог — учир нь энэ
        /// redirection нь ФАЙЛЫН ЗАМЫН ТЕКСТ дээр биш, ДУУДАЖ БУЙ ПРОЦЕССИЙН
        /// bitness дээр суурилдаг. Тиймээс "System32" гэдгийг шууд бичсэн ч
        /// туслахгүй. Үүнийг тойрохын тулд Windows-ийн зөвхөн 32-биттэй
        /// процессод харагддаг тусгай "Sysnative" alias-ийг ашиглана.
        /// </summary>
        private static string GetRundll32Path()
        {
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                var sysnativePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Sysnative", "rundll32.exe");

                if (File.Exists(sysnativePath))
                {
                    LogService.Instance.WriteInfo(
                        "Энэ процесс 32-биттэй горимоор ажиллаж байгааг илрүүллээ. " +
                        $"Sysnative alias ашиглаж 64-биттэй rundll32.exe олов: {sysnativePath}");
                    return sysnativePath;
                }
            }

            return Path.Combine(Environment.SystemDirectory, "rundll32.exe");
        }
        /// <summary>
        /// Зарим POS хэвлэгч нь IEEE-1284 Device ID-д зөв хариу өгдөггүй тул
        /// Windows үүнийг "UNKNOWNPRINTER" гэж таньж, .inf доторх Hardware ID
        /// хэзээ ч автоматаар (PnP-ээр) таарахгүй тохиолдол байдаг. Ийм үед
        /// pnputil driver-ийг Driver Store-д staging хийсэн ч ямар ч принтер
        /// үүсдэггүй тул printui.dll-ийн rundll32 хэлбэрээр, ТУХАЙН ЗАГВАРЫН
        /// (ntprint.inf биш!) .inf файл болон driver-ийн нэрийг шууд зааж өгч
        /// гараар принтер үүсгэнэ.
        /// </summary>
        /// <param name="printerName">Windows дээр харагдах принтерийн нэр.</param>
        /// <param name="infFullPath">Тухайн загварын .inf файлын БҮРЭН (absolute) зам.</param>
        /// <param name="driverName">.inf файл доторх driver/model мөрийн яг нэр (ихэвчлэн printerName-тэй ижил).</param>
        /// <param name="portName">Windows-ийн үүсгэсэн USB порт, жишээ нь "USB007".</param>
        public bool TryAddPrinterManually(string printerName, string infFullPath, string driverName, string portName)
        {
            try
            {
                LogService.Instance.WriteInfo(
                    $"Принтерийг гар аргаар нэмж байна: printer='{printerName}' driver='{driverName}' " +
                    $"port='{portName}' inf='{infFullPath}'");

                if (!File.Exists(infFullPath))
                {
                    LogService.Instance.WriteError($"Гар аргаар нэмэх боломжгүй: .inf файл олдсонгүй ({infFullPath})");
                    return false;
                }

                // /if  -> install printer
                // /b   -> printer нэр (base name)
                // /f   -> .inf файлын зам (ЗААВАЛ тухайн загварын жинхэнэ inf, ntprint.inf биш)
                // /r   -> порт нэр
                // /m   -> .inf доторх driver/model-ийн яг нэр
                // /Z   -> shared болгохгүй (skip sharing dialog)
                //
                // АНХААР (чухал засвар): WindowStyle-ийг Hidden БОЛГОХГҮЙ.
                // printui.dll заримдаа гарын үсэггүй/WHQL бус driver-ийн хувьд
                // "Windows can't verify the publisher of this driver software"
                // гэсэн ИНТЕРАКТИВ (нэг товч дарах шаардлагатай) анхааруулга
                // гаргадаг. Хэрэв цонхыг Hidden болговол тэр анхааруулга ч мөн
                // адил нуугдмал хэвээр гарч ирдэг тул хэрэглэгч огт харахгүй,
                // дарж ч чадахгүй, харин апп WaitForExit()-ээр мөнхөд гацдаг
                // байсан. Тиймээс энд ЗААВАЛ Normal байх ёстой.
                // АНХААР (чухал засвар): "rundll32.exe" гэж зөвхөн нэрээр нь
                // (бүтэн замгүйгээр) дуудвал, зарим тохиолдолд (ялангуяа
                // UseShellExecute=true + Verb=runas хослолоор elevated
                // процесс эхлүүлэхэд) Windows санамсаргүйгээр 32-биттэй
                // (SysWOW64) хувилбарыг олж ажиллуулдаг тохиолдол бий. 32-
                // биттэй rundll32 нь 64-биттэй driver DLL (жиш нь SWLabel.DLL)-
                // ийг ачаалахыг оролдоход "%1 is not a valid Win32 application"
                // (ERROR_BAD_EXE_FORMAT, 0xC1) алдаа өгдөг. Тиймээс ЗААВАЛ
                // System32 доtorh (64-биттэй) хувилбарыг БҮТЭН замаар зааж
                // өгнө.
                var rundll32Path = GetRundll32Path();

                var psi = new ProcessStartInfo
                {
                    FileName = rundll32Path,
                    Arguments = $"printui.dll,PrintUIEntry /if /b \"{printerName}\" /f \"{infFullPath}\" /r \"{portName}\" /m \"{driverName}\" /Z",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };

                using var process = Process.Start(psi);

                // АНХААР (чухал засвар): timeout-гүй WaitForExit() ашиглахгүй.
                // Хэрэв дээрх анхааруулга (эсвэл өөр ямар нэг шалтгаанаар
                // printui) хариу хүлээгээд удаан зогсвол, апп мөнхөд гацахын
                // оронд тодорхой хугацааны (2 минут) дараа процессийг зогсоож,
                // алдаа буцаана.
                bool exited = process != null && process.WaitForExit(120000);

                if (process != null && !exited)
                {
                    LogService.Instance.WriteError(
                        $"'{printerName}'-г гар аргаар нэмэх 2 минутын дотор дуусаагүй " +
                        "(магадгүй 'Windows can't verify the publisher' цонх хариу хүлээж " +
                        "байгаа байж болзошгүй, эсвэл print spooler хариу өгөхгүй байна) -- " +
                        "процессийг зогсоов.");
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                    return false;
                }

                bool success = process != null && process.ExitCode == 0;

                LogService.Instance.WriteInfo(success
                    ? $"'{printerName}' гар аргаар амжилттай нэмэгдлээ ('{portName}' порт дээр)."
                    : $"'{printerName}'-г гар аргаар нэмэх амжилтгүй боллоо (Exit code: {process?.ExitCode}).");

                return success;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Принтерийг гараар нэмэх явцад алдаа гарлаа", ex);
                return false;
            }
        }

        private static string EscapeForWmi(string value) => value.Replace("'", "''");
    }
}