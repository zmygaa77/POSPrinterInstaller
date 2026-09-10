using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using POSPrinterInstaller.Models;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Суулгах явцын үр дүнг илэрхийлнэ.
    /// </summary>
    public class DriverInstallResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ExitCode { get; set; } = -1;
    }

    /// <summary>
    /// printers.json дахь мэдээллийг үндэслэн Drivers/ фолдер дотор байрлах
    /// driver-ийн суулгагч (.exe/.msi) файлыг ажиллуулж, drivers-г
    /// автоматаар (шаардлагатай бол silent горимоор) суулгадаг сервис.
    /// </summary>
    public class DriverInstallService
    {
        private readonly WizardAutomationService _wizardAutomationService = new();
        private readonly PrinterPortDetectionService _portDetectionService = new();

        /// <summary>
        /// Сонгосон принтерийн загварт харгалзах driver-ийг суулгана.
        /// model.InstallMethod == "inf" бол гуравдагч талын installer.exe-г
        /// огт ажиллуулахгүйгээр pnputil-ээр INF аргаар суулгана; үгүй бол
        /// хуучин exe-based аргаар суулгана.
        /// </summary>
        public Task<DriverInstallResult> InstallDriverAsync(PrinterModel model)
        {
            if (string.Equals(model.InstallMethod, "inf", StringComparison.OrdinalIgnoreCase))
            {
                return InstallDriverViaInfAsync(model);
            }

            return InstallDriverViaExeAsync(model);
        }
        private static void EnsureTrustedCertImported()
        {
            try
            {
                var certPath = Path.Combine(AppContext.BaseDirectory, "Drivers", "_TrustedCert", "posprinter-signing.cer");
                if (!File.Exists(certPath))
                {
                    return;
                }

                foreach (var store in new[] { "TrustedPublisher", "Root" })
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "certutil.exe",
                            Arguments = $"-addstore -f \"{store}\" \"{certPath}\"",
                            UseShellExecute = true,
                            Verb = "runas",
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true
                        };

                        using var process = new Process { StartInfo = psi };
                        process.Start();
                        process.WaitForExit(15000);

                        LogService.Instance.WriteInfo(
                            $"Сертификатыг '{store}' store-д итгэмжлэх оролдлого хийлээ (Exit code: {process.ExitCode}).");
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.WriteError(
                            $"Сертификатыг '{store}' store-д итгэмжлэх үед алдаа гарлаа: {ex.Message}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError(
                    $"Сертификат итгэмжлэх шалгалт хийх үед алдаа гарлаа: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// Windows-ийн байгалийн pnputil.exe хэрэгслээр .inf driver package-ийг
        /// шууд Driver Store-д staging хийж суулгана. Гуравдагч талын
        /// installer.exe-г огт ажиллуулахгүй тул GUI wizard-аас хамаардаггүй,
        /// script-оор бүрэн silent, автоматжуулах боломжтой хамгийн найдвартай
        /// арга. pnputil нь Windows 10/11-т байгалиараа (System32 дотор) орсон
        /// байдаг тул тусад нь татаж суулгах шаардлагагүй.
        /// </summary>
        public async Task<DriverInstallResult> InstallDriverViaInfAsync(PrinterModel model)
        {
            var driversRoot = Path.Combine(AppContext.BaseDirectory, "Drivers");
            var infPath = Path.Combine(driversRoot, model.InfPath);

            if (!File.Exists(infPath))
            {
                var missingMsg = $".inf файл олдсонгүй: {infPath}. " +
                                 "Үйлдвэрлэгчийн driver package-аас .inf файлыг (мөн хамт " +
                                 "ирдэг .cat/.sys файлуудын хамт) Drivers/<загвар> фолдерт " +
                                 "байршуулсан эсэхээ шалгана уу.";
                LogService.Instance.WriteError(missingMsg);
                return new DriverInstallResult { Success = false, Message = missingMsg };
            }
            EnsureTrustedCertImported();
            try
            {
                LogService.Instance.WriteInfo(
                    $"INF аргаар driver суулгаж эхэлж байна: {model.ModelName} | INF: {infPath}");

                // pnputil /add-driver "<inf>" /install
                //   /add-driver  -> driver package-ийг Driver Store-д staging хийнэ
                //   /install     -> staging хийсний дараа тухайн төхөөрөмжид нь
                //                   яг тэр дор нь холбож суулгана (боломжтой бол)
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/add-driver \"{infPath}\" /install",
                    UseShellExecute = true, // Verb=runas ашиглахын тулд шаардлагатай
                    Verb = "runas",         // Driver staging-д Администратор эрх шаардлагатай
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                await process.WaitForExitAsync();

                // pnputil-ийн ердийн гарцын код: 0 = амжилттай,
                // 3010 = амжилттай ч дахин ачаалах шаардлагатай.
                bool success = process.ExitCode == 0 || process.ExitCode == 3010;

                var resultMsg = success
                    ? $"Driver INF аргаар амжилттай суулгагдлаа ({model.ModelName})." +
                      (process.ExitCode == 3010
                          ? " Зарим тохиолдолд компьютерээ дахин ачаалах шаардлагатай."
                          : string.Empty)
                    : $"pnputil /add-driver амжилтгүй боллоо (Exit code: {process.ExitCode}). " +
                      "INF файл тухайн Windows хувилбар (x64), эсвэл driver-ийн " +
                      "гарын үсэг (signature)-тэй нийцэхгүй байж болно. Хэрэв driver нь " +
                      "цахим гарын үсэггүй бол Windows дефолтоор татгалзана.";

                LogService.Instance.WriteInfo(resultMsg);

                return new DriverInstallResult
                {
                    Success = success,
                    Message = resultMsg,
                    ExitCode = process.ExitCode
                };
            }
            catch (Exception ex)
            {
                var msg = $"INF аргаар driver суулгах явцад алдаа гарлаа: {ex.Message}";
                LogService.Instance.WriteError(msg, ex);
                return new DriverInstallResult { Success = false, Message = msg };
            }
        }

        /// <summary>
        /// Хуучин арга: Drivers/ дотрох гуравдагч талын driver суулгагчийг
        /// (.exe/.msi) шууд ажиллуулж суулгана.
        /// </summary>
        private async Task<DriverInstallResult> InstallDriverViaExeAsync(PrinterModel model)
        {
            var driversRoot = Path.Combine(AppContext.BaseDirectory, "Drivers");
            var installerPath = Path.Combine(driversRoot, model.DriverInstallerPath);

            if (!File.Exists(installerPath))
            {
                var msg = $"Driver-ийн суулгагч файл олдсонгүй: {installerPath}. " +
                          "Та тухайн загварын driver.exe-г Drivers/<загвар> фолдерт байршуулсан эсэхээ шалгана уу.";
                LogService.Instance.WriteError(msg);
                return new DriverInstallResult { Success = false, Message = msg };
            }

            // ===== ШИНЭ: {DETECTED_PORT} placeholder-ийг бодит USBnnn портоор орлуулах =====
            // Зарим driver суулгагч (жишээ нь Seagull-ийн DriverWizard.exe)
            // CLI silent install горимдоо тодорхой "/port:USBxxx" параметр
            // шаарддаг бөгөөд энэ порт нэр нь компьютер бүр дээр өөр байдаг
            // (Windows-ийн санамсаргүй оноодог дугаар) тул printers.json-д
            // тогтмол бичих боломжгүй. UsbPrintHardwareIdPrefix заасан бол
            // одоо холбогдсон тухайн хэвлэгчийн бодит портыг WMI-ээр олж,
            // InstallArguments дотрох "{DETECTED_PORT}" текстийг орлуулна.
            var installArguments = model.InstallArguments ?? string.Empty;

            if (installArguments.Contains("{DETECTED_PORT}", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(model.UsbPrintHardwareIdPrefix))
                {
                    var msg = $"InstallArguments дотор {{DETECTED_PORT}} байгаа ч " +
                              "printers.json дотор 'usbPrintHardwareIdPrefix' талбар " +
                              "тохируулагдаагүй байна. Энэ талбарыг заавал бөглөнө үү.";
                    LogService.Instance.WriteError(msg);
                    return new DriverInstallResult { Success = false, Message = msg };
                }

                var detectedPort = _portDetectionService.FindUsbPrintPortByHardwareIdPrefix(
                    model.UsbPrintHardwareIdPrefix);

                if (string.IsNullOrEmpty(detectedPort))
                {
                    var msg = $"'{model.ModelName}' хэвлэгчийн USB порт олдсонгүй. " +
                              "Хэвлэгчээ USB кабелиар холбож, асаалттай эсэхийг шалгаад " +
                              "'Дахин шалгах' дараад дахин оролдоно уу.";
                    LogService.Instance.WriteError(msg);
                    return new DriverInstallResult { Success = false, Message = msg };
                }

                installArguments = installArguments.Replace(
                    "{DETECTED_PORT}", detectedPort, StringComparison.OrdinalIgnoreCase);

                LogService.Instance.WriteInfo(
                    $"{{DETECTED_PORT}}-г '{detectedPort}' портоор орлууллаа. Эцсийн параметр: {installArguments}");
            }

            try
            {
                LogService.Instance.WriteInfo(
                    $"Driver суулгаж эхэлж байна: {model.ModelName} | Файл: {installerPath} | Параметр: {installArguments}");

                // GUI Wizard (silent параметргүй, жишээ нь drvinst.exe) бол
                // цонхыг ХАРАГДАХ горимоор эхлүүлнэ -- учир нь
                // WizardAutomationService эхэндээ цонхыг олж, эхний Next
                // товчийг амжилттай дарсны дараа л өөрөө нуудаг (SW_HIDE).
                // Энгийн silent installer бол хэвийн ёсоор нуугдмал горимоор
                // ажиллана.
                var startVisible = model.AutoDriveWizard || !model.SupportsSilentInstall;

                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = installArguments,
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? driversRoot,
                    UseShellExecute = true,   // driver суулгагч ихэвчлэн UAC (админ эрх) шаарддаг
                    Verb = "runas",           // Администратор эрхээр ажиллуулна
                    WindowStyle = startVisible
                        ? ProcessWindowStyle.Normal
                        : ProcessWindowStyle.Hidden
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                Task<bool>? wizardTask = null;
                if (model.AutoDriveWizard)
                {
                    LogService.Instance.WriteInfo(
                        $"'{model.ModelName}' нь GUI Wizard тул автоматжуулалт (UI Automation) эхэллээ.");

                    wizardTask = _wizardAutomationService.AutomateAsync(
                        process.Id,
                        model,
                        status => LogService.Instance.WriteInfo($"[Wizard] {status}"));
                }

                // Анхаар: зарим GUI суулгагч (жишээ нь энэ drvinst.exe/XPrinter
                // wizard) Verb="runas"-аар эхэлсэн ч дотроо өөрийгөө дахин
                // elevated горимд дахин эхлүүлдэг (self re-launch). Ийм үед
                // ЭНЭ (анхны) process объект хэдхэн миллисекундэд л амьдардаг
                // бөгөөд бодит Wizard цонх бүрэн ажиллаж дуустал хүлээхгүйгээр
                // маш эрт "дууслаа" гэж буцна. Иймд процессийн гарцыг
                // хүлээхээс ГАДНА (тэр нь маш хурдан ирж болзошгүй), Wizard
                // автоматжуулалтыг ЭНЭ ЖИНХЭНЭ дуустал нь бүрэн хүлээнэ --
                // WizardAutomationService дотроо аль хэдийн 3 минутын
                // хамгаалалтын хугацааны хязгаартай тул апп мөнхөд гацахгүй.
                await process.WaitForExitAsync();

                bool wizardCompleted = true;
                if (wizardTask != null)
                {
                    wizardCompleted = await wizardTask;
                }

                bool success = process.ExitCode == 0;

                if (model.AutoDriveWizard && !wizardCompleted)
                {
                    // Wizard-ийг автоматаар бөглөж, Finish дараад дуусгаж
                    // чадсангүй (тухайн дэлгэц/товч танигдаагүй, эсвэл
                    // хугацааны хязгаарт хүрсэн). Цонхыг хэрэглэгчид
                    // харагдуулсан тул гараар үргэлжлүүлэх боломжтой, гэхдээ
                    // энэ мөчид driver бүрэн, зөв суусан эсэхийг батлах
                    // боломжгүй тул амжилтгүй гэж тэмдэглэнэ.
                    success = false;
                }

                var resultMsg = success
                    ? $"Driver амжилттай суулгагдлаа ({model.ModelName})."
                    : model.AutoDriveWizard && !wizardCompleted
                        ? "Суулгагчийн Wizard цонхыг автоматаар дуусгаж чадсангүй. " +
                          "Цонх дахин харагдаж байгаа тул гараар Finish дараад дуусгана уу, " +
                          "дараа нь СУУЛГАХ товчийг дахин дарна уу."
                        : $"Driver суулгах явцад алдаатай гарц (Exit code: {process.ExitCode}) буцлаа. " +
                          "Заримдаа энэ нь driver-ийн суулгагчаас хамаарч заавал алдаа гэсэн үг биш байж болно.";

                LogService.Instance.WriteInfo(resultMsg);

                return new DriverInstallResult
                {
                    Success = success,
                    Message = resultMsg,
                    ExitCode = process.ExitCode
                };
            }
            catch (Exception ex)
            {
                var msg = $"Driver суулгах явцад алдаа гарлаа: {ex.Message}";
                LogService.Instance.WriteError(msg, ex);
                return new DriverInstallResult { Success = false, Message = msg };
            }
        }
    }
}