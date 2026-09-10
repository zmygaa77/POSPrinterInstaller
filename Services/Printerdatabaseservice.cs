using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using POSPrinterInstaller.Models;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Database/printers.json файлыг уншиж, дэмжигдсэн принтерийн загваруудын
    /// жагсаалт болгож ачаалдаг сервис.
    ///
    /// Яагаад JSON ашигласан бэ:
    /// - Эх код (C#) өөрчлөхгүйгээр шинэ принтерийн загвар нэмэх, устгах,
    ///   засварлах боломжтой байхын тулд.
    /// - Хэрэглэгч (та) шинэ VID/PID болон driver-ээ өөрөө JSON руу
    ///   нэмээд ашиглаж болно.
    /// </summary>
    public class PrinterDatabaseService
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly string _databasePath;

        /// <summary>
        /// printers.json-ий ХАМГИЙН СҮҮЛИЙН хувилбарыг Online-оос шалгах URL-ыг
        /// заасан жижиг текст файлын зам. Энэ файл дотор ердөө нэг мөр (raw
        /// JSON URL, жишээ нь GitHub-ийн "raw.githubusercontent.com" линк)
        /// байна. Файл байхгүй эсвэл хоосон бол Online шалгалтыг ЧИМЭЭГҮЙХЭН
        /// алгасаж, зөвхөн локал сангаар ажиллана (backward-compat, интернэт
        /// заавал байх шаардлагагүй).
        /// </summary>
        private string OnlineSourceConfigPath => Path.Combine(AppContext.BaseDirectory, "Database", "online-source.txt");

        private List<PrinterModel> _cachedModels = new();

        public PrinterDatabaseService()
        {
            _databasePath = Path.Combine(AppContext.BaseDirectory, "Database", "printers.json");
        }

        /// <summary>
        /// printers.json-ий ХАМГИЙН СҮҮЛИЙН хувилбарыг Online эх сурвалжаас
        /// (OnlineSourceConfigPath файлд заасан URL) татаж, локал
        /// Database/printers.json файлыг ШИНЭЧЛЭНЭ. Ингэснээр шинэ хэвлэгчийн
        /// загвар нэмэх, driverDownloadUrl шинэчлэх зэрэгт аппыг дахин
        /// тараах шаардлагагүй болно — зөвхөн Online дэх нэг JSON файлыг
        /// шинэчилбэл хангалттай.
        ///
        /// АЮУЛГҮЙ БАЙДАЛ: татсан өгөгдөл хүчинтэй, хоосон биш
        /// List&lt;PrinterModel&gt; болж deserialize хийгдэхгүй бол (жишээ нь
        /// сүлжээ тасарч HTML алдааны хуудас ирвэл, эсвэл сервер унтарсан
        /// бол), локал файлыг ОГТ ДАРЖ БИЧИХГҮЙ — хуучин (сүүлд ажилласан,
        /// баталгаатай) хувилбар хэвээр үлдэнэ. Мөн Online-той холбогдож
        /// чадаагүй ч (интернэтгүй) апп нурахгүй, зүгээр л локал сангаар
        /// үргэлжлүүлж ажиллана.
        /// </summary>
        public async Task<(bool Success, string Message)> TryUpdateDatabaseFromOnlineAsync(Action<string>? progress = null)
        {
            string? onlineUrl = null;

            try
            {
                if (File.Exists(OnlineSourceConfigPath))
                {
                    onlineUrl = (await File.ReadAllTextAsync(OnlineSourceConfigPath)).Trim();
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteWarning($"online-source.txt уншихад алдаа гарлаа (алгасав): {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(onlineUrl))
            {
                // Online эх сурвалж тохируулаагүй — энэ бол хэвийн явдал
                // (backward-compat), зөвхөн локал сангаар ажиллана.
                return (false, "Online эх сурвалж тохируулаагүй (Database/online-source.txt байхгүй эсвэл хоосон).");
            }

            try
            {
                progress?.Invoke("Хэвлэгчийн санг Online-оос шалгаж байна...");
                LogService.Instance.WriteInfo($"printers.json-ийг Online-оос шалгаж байна: {onlineUrl}");

                var json = await _http.GetStringAsync(onlineUrl);

                List<PrinterModel>? parsed;
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    parsed = JsonSerializer.Deserialize<List<PrinterModel>>(json, options);
                }
                catch (JsonException ex)
                {
                    var msg = $"Online эх сурвалжаас ирсэн өгөгдөл хүчинтэй JSON биш байна — алгасаж, локал санг хэвээр үлдээв ({ex.Message}).";
                    LogService.Instance.WriteWarning(msg);
                    return (false, msg);
                }

                if (parsed == null || parsed.Count == 0)
                {
                    var msg = "Online эх сурвалжаас ирсэн жагсаалт хоосон байна — алгасаж, локал санг хэвээр үлдээв.";
                    LogService.Instance.WriteWarning(msg);
                    return (false, msg);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                await File.WriteAllTextAsync(_databasePath, json);
                _cachedModels = parsed;

                var successMsg = $"Хэвлэгчийн сан Online-оос амжилттай шинэчлэгдлээ ({parsed.Count} загвар).";
                LogService.Instance.WriteInfo(successMsg);
                progress?.Invoke(successMsg);

                return (true, successMsg);
            }
            catch (TaskCanceledException)
            {
                var msg = "Online санг шалгах хугацаа хэтэрлээ (timeout) — локал сангаар үргэлжлүүлж ажиллана.";
                LogService.Instance.WriteWarning(msg);
                return (false, msg);
            }
            catch (HttpRequestException ex)
            {
                var msg = $"Online санд холбогдож чадсангүй (интернэтгүй байж болно) — локал сангаар үргэлжлүүлж ажиллана: {ex.Message}";
                LogService.Instance.WriteWarning(msg);
                return (false, msg);
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Online санг татах явцад алдаа гарлаа", ex);
                return (false, $"Гэнэтийн алдаа гарлаа: {ex.Message}");
            }
        }

        /// <summary>printers.json-г диск дээрээс уншиж жагсаалт болгож буцаана.</summary>
        public List<PrinterModel> LoadPrinterModels()
        {
            try
            {
                if (!File.Exists(_databasePath))
                {
                    LogService.Instance.WriteError($"Принтерийн өгөгдлийн сан олдсонгүй: {_databasePath}");
                    return new List<PrinterModel>();
                }

                var json = File.ReadAllText(_databasePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                _cachedModels = JsonSerializer.Deserialize<List<PrinterModel>>(json, options)
                                ?? new List<PrinterModel>();

                LogService.Instance.WriteInfo($"Принтерийн өгөгдлийн сангаас {_cachedModels.Count} загвар ачааллаа.");
                return _cachedModels;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("printers.json уншихад алдаа гарлаа", ex);
                return new List<PrinterModel>();
            }
        }

        /// <summary>
        /// Өгөгдсөн VID/PID-тэй тохирох принтерийн загварыг өгөгдлийн сангаас хайна.
        /// VID/PID харьцуулалт том/жижиг үсэгт мэдрэмтгий биш (case-insensitive).
        ///
        /// Backward-compat overload: Product Name-гүйгээр дуудвал, ижил
        /// VID/PID-тэй хэд хэдэн загвар байх үед зөвхөн ЭХНИЙХИЙГ буцаана
        /// (энэ нь буруу тохирох эрсдэлтэй тул аль болох доорхи
        /// FindMatchingModel(vid, pid, deviceName) хувилбарыг ашиглана уу).
        /// </summary>
        public PrinterModel? FindMatchingModel(string vid, string pid)
        {
            return FindMatchingModel(vid, pid, deviceName: string.Empty);
        }

        /// <summary>
        /// Өгөгдсөн VID/PID-тэй тохирох принтерийн загварыг хайна. Хэрэв
        /// ижил VID/PID-тэй ХЭД ХЭДЭН загвар байвал (жишээ нь нэг USB chip-ийг
        /// хэд хэдэн өөр брэндийн хэвлэгч ашигладаг тохиолдол), Windows-ийн
        /// харуулж буй бодит төхөөрөмжийн нэр (Win32_PnPEntity.Name,
        /// DetectedPrinter.DeviceName) дотор тухайн загварын
        /// "productNameContains" утга агуулагдаж байгаа эсэхээр нарийсгана.
        ///
        /// Жишээ нь: нэг физик chip (VID_1FC9&PID_2016) дор "4BARCODE
        /// 3B-T371U" (шошго) ба "Printer POS-80" (баримт) гэсэн хоёр өөр
        /// нэрээр танигддаг хоёр өөр хэвлэгч байх үед, эдгээрийг зөв ялгаж
        /// тохируулна.
        /// </summary>
        public PrinterModel? FindMatchingModel(string vid, string pid, string deviceName)
        {
            if (_cachedModels.Count == 0)
            {
                LoadPrinterModels();
            }

            var candidates = _cachedModels
                .Where(m =>
                    string.Equals(m.Vid, vid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Pid, pid, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            // Ижил VID/PID-тэй хэд хэдэн загвар олдлоо — Product Name-аар нарийсгана
            LogService.Instance.WriteInfo(
                $"VID:{vid} PID:{pid}-тэй {candidates.Count} загвар олдлоо. " +
                $"Device Name '{deviceName}'-аар ялгахыг оролдож байна...");

            var byName = candidates.FirstOrDefault(m =>
                !string.IsNullOrWhiteSpace(m.ProductNameContains) &&
                !string.IsNullOrWhiteSpace(deviceName) &&
                deviceName.Contains(m.ProductNameContains, StringComparison.OrdinalIgnoreCase));

            if (byName != null)
            {
                LogService.Instance.WriteInfo(
                    $"Device Name-аар тохирлоо: '{byName.ModelName}' " +
                    $"(productNameContains: '{byName.ProductNameContains}')");
                return byName;
            }

            LogService.Instance.WriteWarning(
                $"VID:{vid} PID:{pid}-тэй {candidates.Count} загвар байгаа ч Device Name " +
                $"'{deviceName}'-аар ялгаж чадсангүй. Эхний загварыг ({candidates[0].ModelName}) " +
                "сонгож байна — printers.json дахь productNameContains утгуудыг шалгана уу.");

            return candidates[0];
        }

        /// <summary>
        /// Ижил VID/PID-тэй БҮХ candidate загварыг буцаана (нэг нь ч
        /// Device Name-аар тодорхой ялгарахгүй үед, UI дээр хэрэглэгчээр
        /// гараар сонгуулах зорилгоор ашиглаж болно).
        /// </summary>
        public List<PrinterModel> FindAllMatchingModels(string vid, string pid)
        {
            if (_cachedModels.Count == 0)
            {
                LoadPrinterModels();
            }

            return _cachedModels
                .Where(m =>
                    string.Equals(m.Vid, vid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Pid, pid, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}