using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Зарим POS хэвлэгч (жишээ нь Sewoo, генерик USB Printer class,
    /// VID_0525&amp;PID_A700 гэх мэт Windows-ийн built-in "USB Printing
    /// Support" (usbprint.sys) драйверыг ашигладаг төхөөрөмж) нь
    /// Win32_PnPEntity дотор "USBPRINT\..." эсвэл "...USB001" гэсэн
    /// DeviceID-гүйгээр л харагддаг тул WMI PnP query-ээр порт олж
    /// чадахгүй. Ийм үед Windows Spooler-ийн өөрийнх нь port жагсаалтыг
    /// (winspool.drv-ийн EnumPorts API) шалгаж, аль хэдийн принтерт
    /// ашиглагдаагүй "USBnnn" портуудаас хамгийн сүүлийн (тоо хамгийн
    /// их) портыг сонгоно — учир нь шинээр холбогдсон хэвлэгчийн порт
    /// ихэвчлэн хамгийн сүүлд үүсдэг бөгөөд өөр принтерт хараахан
    /// оноогдоогүй байдаг.
    /// </summary>
    public class PrinterPortDetectionService
    {
        // Spooler-ийн EnumPorts-с ирэх БҮТЭН порт нэрийг (жишээ нь "USB001")
        // барих regex — эхлэл ('^') ба төгсгөл ('$') хоёуланг шалгана.
        private static readonly Regex UsbPortRegex =
            new(@"^USB(\d{3,})$", RegexOptions.Compiled);

        // Win32_PnPEntity-ийн DeviceID мөрийн ТӨГСГӨЛД байх "USBnnn" хэсгийг
        // барих regex (мөрийн эхэнд өөр зүйл байсан ч болно).
        // Жишээ: "USBPRINT\4BARCODE3B-T371U\7&190A873F&0&USB013" → "USB013"
        private static readonly Regex UsbPortSuffixRegex =
            new(@"(USB\d{3,})$", RegexOptions.Compiled);

        private const int PORT_INFO_1_SIZE = 4; // IntPtr-ийн size биш, EnumPorts Level=1 бүтэц

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PORT_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pName;
        }

        // Level=2 бүтэц: pName-ээс гадна pMonitorName, pDescription, fPortType,
        // Reserved зэрэг нэмэлт талбартай. Яг энэ "pDescription" талбар нь
        // Printer Properties → Ports таб дээрх "Description" баганад
        // харагддаг утга (жишээ нь "SEWOO TECH LK-B30II") — SW Port Monitor
        // гэх мэт custom port monitor-ууд тухайн порт дээр одоо ЯМАР
        // хэвлэгч холбогдсоныг энд бичдэг тул, дугаараар "хамгийн сүүлийнх"
        // гэж таамаглахаас илүү найдвартай — Description-оор нь шууд
        // тохирох портыг олох боломж олгодог.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PORT_INFO_2
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pPortName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pMonitorName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDescription;
            public int fPortType;
            public int Reserved;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumPorts(
            string? pName,
            int Level,
            IntPtr pPorts,
            int cbBuf,
            out int pcbNeeded,
            out int pcReturned);

        /// <summary>
        /// Windows Print Spooler-д одоо бүртгэлтэй БҮХ портын нэрсийг
        /// (жишээ нь "LPT1:", "COM1:", "USB001", "USB002"...) буцаана.
        /// </summary>
        private List<string> EnumAllSpoolerPorts()
        {
            var result = new List<string>();

            EnumPorts(null, 1, IntPtr.Zero, 0, out int neededBytes, out _);
            if (neededBytes <= 0)
                return result;

            IntPtr buffer = Marshal.AllocHGlobal(neededBytes);
            try
            {
                bool ok = EnumPorts(null, 1, buffer, neededBytes, out _, out int returned);
                if (!ok)
                {
                    LogService.Instance.WriteError(
                        "EnumPorts амжилтгүй боллоо",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                    return result;
                }

                int structSize = Marshal.SizeOf<PORT_INFO_1>();
                for (int i = 0; i < returned; i++)
                {
                    IntPtr current = IntPtr.Add(buffer, i * structSize);
                    var info = Marshal.PtrToStructure<PORT_INFO_1>(current);
                    if (!string.IsNullOrWhiteSpace(info.pName))
                    {
                        // Порт нэрийн ард ирдэг ":" тэмдэгтийг зайлуулна (жишээ нь "USB001" нь ":" гүй,
                        // харин "LPT1:" гэх мэт зарим порт ":" -тэй ирдэг)
                        result.Add(info.pName.TrimEnd(':'));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        /// <summary>
        /// Windows Print Spooler-д одоо бүртгэлтэй портуудыг тэдгээрийн
        /// (Port, Description) хосоор буцаана (EnumPorts Level=2). Энэ
        /// Description талбар нь яг л Printer Properties → Ports таб дээр
        /// хэрэглэгчийн нүдэнд харагддаг "Description" баганатай ижил
        /// утга бөгөөд custom port monitor (жишээ нь SWPortMon.dll) тухайн
        /// порт дээр одоо холбогдсон бодит хэвлэгчийн нэрийг (жишээ нь
        /// "SEWOO TECH LK-B30II") энд бичдэг тул портыг зөв тодорхойлоход
        /// хамгийн найдвартай эх сурвалж.
        /// </summary>
        private List<(string Name, string Description)> EnumAllSpoolerPortsWithDescription()
        {
            var result = new List<(string, string)>();

            EnumPorts(null, 2, IntPtr.Zero, 0, out int neededBytes, out _);
            if (neededBytes <= 0)
                return result;

            IntPtr buffer = Marshal.AllocHGlobal(neededBytes);
            try
            {
                bool ok = EnumPorts(null, 2, buffer, neededBytes, out _, out int returned);
                if (!ok)
                {
                    LogService.Instance.WriteError(
                        "EnumPorts (Level=2) амжилтгүй боллоо",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                    return result;
                }

                int structSize = Marshal.SizeOf<PORT_INFO_2>();
                for (int i = 0; i < returned; i++)
                {
                    IntPtr current = IntPtr.Add(buffer, i * structSize);
                    var info = Marshal.PtrToStructure<PORT_INFO_2>(current);
                    if (!string.IsNullOrWhiteSpace(info.pPortName))
                    {
                        result.Add((info.pPortName.TrimEnd(':'), info.pDescription ?? string.Empty));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        /// <summary>
        /// Одоогоор ямар ч принтерт ашиглагдаагүй "USBnnn" портуудаас,
        /// Description талбарт нь өгөгдсөн түлхүүр үг (жишээ нь "SEWOO",
        /// "B30", эсвэл WindowsPrinterName) агуулагдаж буй портыг олж
        /// буцаана. Энэ бол "хамгийн сүүлийн дугаартай порт" гэсэн
        /// таамаглалаас (DetectMostRecentUsbPrinterPort) илүү нарийвчлалтай
        /// арга — учир нь Windows өөрөө Description баганад тухайн порт
        /// дээр яг ямар хэвлэгч холбогдсоныг харуулдаг тул зөв портыг
        /// шууд танина (жишээ нь дээрх зурган дээрх USB001-ийн Description
        /// нь "SEWOO TECH LK-B30II" гэж харагдаж байгаа шиг).
        ///
        /// Хэрэв keyword-той таарах Description бүхий чөлөөтэй порт
        /// олдохгүй бол null буцаана (энэ тохиолдолд дуудагч тал хуучин
        /// "хамгийн сүүлийн дугаар" heuristic руу fallback хийж болно).
        /// </summary>
        public string? DetectUsbPortByDescriptionKeyword(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return null;

            var usedPorts = GetPortsInUseByExistingPrinters();
            var allPorts = EnumAllSpoolerPortsWithDescription();

            var match = allPorts
                .Where(p => UsbPortRegex.IsMatch(p.Name))
                .Where(p => !usedPorts.Contains(p.Name))
                .Where(p => !string.IsNullOrWhiteSpace(p.Description) &&
                            p.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                // Хэд хэдэн порт таарвал дугаар нь хамгийн бага байгааг нь эхэнд авна
                // (жишээ нь USB001 нь ихэвчлэн анх суулгасан жинхэнэ хэвлэгчийн порт байдаг).
                .OrderBy(p =>
                {
                    var m = UsbPortRegex.Match(p.Name);
                    return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : int.MaxValue;
                })
                .FirstOrDefault();

            if (match.Name != null)
            {
                LogService.Instance.WriteInfo(
                    $"Description-оор ('{keyword}') тохирох чөлөөтэй USB порт олдлоо: " +
                    $"{match.Name} (Description: \"{match.Description}\")");
                return match.Name;
            }

            LogService.Instance.WriteInfo(
                $"Description дотор '{keyword}' агуулсан чөлөөтэй USB порт олдсонгүй. " +
                "Спулерийн бүх порт: " +
                string.Join(", ", allPorts.Select(p => $"{p.Name}=\"{p.Description}\"")));

            return null;
        }

        /// <summary>
        /// Одоогоор Windows-ийн ямар нэг суулгагдсан принтерт аль хэдийн
        /// ашиглагдаж буй портуудыг буцаана (жишээ нь өөр принтер
        /// "USB001"-ийг эзэлж байвал түүнийг дахин санал болгохгүй).
        /// </summary>
        private HashSet<string> GetPortsInUseByExistingPrinters()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PortName FROM Win32_Printer");

                foreach (ManagementObject printer in searcher.Get())
                {
                    var portName = printer["PortName"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(portName))
                    {
                        used.Add(portName.TrimEnd(':'));
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError(
                    "Одоо ашиглагдаж буй принтерийн портуудыг шалгах явцад алдаа гарлаа", ex);
            }

            return used;
        }

        /// <summary>
        /// Spooler-д бүртгэлтэй, "USBnnn" хэлбэртэй, ямар ч принтерт
        /// одоогоор ашиглагдаагүй бүх портуудыг буцаана.
        /// </summary>
        public List<string> DetectAllUsbPrinterPorts()
        {
            var allPorts = EnumAllSpoolerPorts();
            var usedPorts = GetPortsInUseByExistingPrinters();

            var freeUsbPorts = allPorts
                .Where(p => UsbPortRegex.IsMatch(p))
                .Where(p => !usedPorts.Contains(p))
                .ToList();

            foreach (var p in freeUsbPorts)
            {
                LogService.Instance.WriteInfo($"Чөлөөтэй USB порт олдлоо: {p}");
            }

            if (!freeUsbPorts.Any())
            {
                LogService.Instance.WriteInfo(
                    $"Чөлөөтэй USBnnn порт олдсонгүй. Spooler-д бүртгэлтэй бүх порт: " +
                    string.Join(", ", allPorts) +
                    " | Ашиглагдаж буй портууд: " + string.Join(", ", usedPorts));
            }

            return freeUsbPorts;
        }

        /// <summary>
        /// Хамгийн сүүлд (тоон дугаар хамгийн их) үүссэн, одоогоор ямар ч
        /// принтерт ашиглагдаагүй USBnnn портыг буцаана — энэ нь
        /// ихэвчлэн саяхан холбогдсон, драйвер нь автоматаар
        /// тохироогүй хэвлэгчийн порт байдаг. Порт олдоогүй бол null
        /// буцаана.
        /// </summary>
        public string? DetectMostRecentUsbPrinterPort(string? descriptionKeyword = null)
        {
            // 1) Хамгийн найдвартай арга: Description баганад (Windows-ийн
            // өөрийнх нь харуулдаг мэдээлэл) хэвлэгчийн загварын нэр /
            // үйлдвэрлэгчийн нэр агуулагдсан чөлөөтэй USB порт байвал
            // шууд түүнийг ашигла (жишээ нь "SEWOO TECH LK-B30II").
            if (!string.IsNullOrWhiteSpace(descriptionKeyword))
            {
                var byDescription = DetectUsbPortByDescriptionKeyword(descriptionKeyword);
                if (!string.IsNullOrEmpty(byDescription))
                    return byDescription;
            }

            // 2) Fallback: Description-оор тохирох порт олдоогүй бол
            // (жишээ нь driver Description-ийг бөглөдөггүй үед) хуучин
            // "хамгийн сүүлд үүссэн чөлөөтэй USBnnn" таамаглалыг ашигла.
            var ports = DetectAllUsbPrinterPorts();

            return ports
                .OrderByDescending(p =>
                {
                    var match = UsbPortRegex.Match(p);
                    return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
                })
                .FirstOrDefault();
        }

        /// <summary>
        /// Тодорхой Hardware ID угтвар (жишээ нь "USBPRINT\4BARCODE3B-T371U")
        /// -тай таарсан, одоо холбогдсон USBPRINT class төхөөрөмжийн
        /// PNPDeviceID-ийн сүүлд байгаа "USBnnn" порт нэрийг олж буцаана.
        ///
        /// Жишээ нь PNPDeviceID = "USBPRINT\4BARCODE3B-T371U\7&190A873F&0&USB013"
        /// байвал "USB013" гэж буцаана. Энэ нь Seagull DriverWizard.exe
        /// зэрэг CLI silent installer-ийн /port параметрт шаардлагатай бодит
        /// (компьютер бүр дээр өөр байдаг) порт нэрийг динамикаар олоход
        /// ашиглагдана. Олдоогүй бол null.
        /// </summary>
        public string? FindUsbPrintPortByHardwareIdPrefix(string hardwareIdPrefix)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USBPRINT%'");

                foreach (ManagementObject device in searcher.Get())
                {
                    string deviceId = device["DeviceID"]?.ToString() ?? string.Empty;

                    if (!deviceId.StartsWith(hardwareIdPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var match = UsbPortSuffixRegex.Match(deviceId);
                    if (match.Success)
                    {
                        var port = match.Groups[1].Value; // жишээ нь "USB013"
                        LogService.Instance.WriteInfo(
                            $"Hardware ID '{hardwareIdPrefix}'-аар порт олдлоо: {port} (DeviceID: {deviceId})");
                        return port;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError(
                    $"Hardware ID '{hardwareIdPrefix}'-аар USBPRINT порт хайх явцад алдаа гарлаа", ex);
            }

            LogService.Instance.WriteWarning(
                $"Hardware ID '{hardwareIdPrefix}'-тай USBPRINT device одоогоор холбогдоогүй байна.");
            return null;
        }
    }
}