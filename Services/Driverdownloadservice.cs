using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using POSPrinterInstaller.Models;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Drivers/ фолдерт driver-ийн файл (InfPath эсвэл DriverInstallerPath
    /// заасан зам) локалаар байхгүй үед, printers.json дэх
    /// "driverDownloadUrl" хаягаас Online-оос татаж, шаардлагатай бол
    /// (zip) задалж, дараа нь одоо байгаа DriverInstallService (pnputil /
    /// installer.exe) ажиллах боломжтой болгож бэлдэж өгдөг сервис.
    ///
    /// Ингэснээр apps дотор driver файлуудыг тарааж явах шаардлагагүй
    /// болж, printers.json-д зөвхөн URL заагаад л шинэ хэвлэгч нэмэх
    /// боломжтой болно.
    /// </summary>
    public class DriverDownloadService
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

        /// <summary>
        /// Тухайн загварын driver Drivers/ фолдерт бэлэн эсэхийг шалгаад,
        /// байхгүй бол DriverDownloadUrl-аас татаж бэлдэнэ. Файл аль хэдийн
        /// байгаа бол ЮУ Ч ТАТАХГҮЙ (интернэт хэрэглээ хэмнэх, хурдан байх).
        /// </summary>
        public async Task<DriverInstallResult> EnsureDriverAvailableAsync(PrinterModel model, Action<string>? progress = null)
        {
            var driversRoot = Path.Combine(AppContext.BaseDirectory, "Drivers");
            Directory.CreateDirectory(driversRoot);

            var relativeTargetPath = string.Equals(model.InstallMethod, "inf", StringComparison.OrdinalIgnoreCase)
                ? model.InfPath
                : model.DriverInstallerPath;

            if (string.IsNullOrWhiteSpace(relativeTargetPath))
            {
                var msg = $"'{model.ModelName}' загварт driver файлын зам (InfPath/DriverInstallerPath) " +
                          "printers.json дотор тохируулагдаагүй байна.";
                LogService.Instance.WriteError(msg);
                return new DriverInstallResult { Success = false, Message = msg };
            }

            var localFullPath = Path.Combine(driversRoot, relativeTargetPath);

            if (File.Exists(localFullPath))
            {
                LogService.Instance.WriteInfo($"Driver локал дискнээс олдлоо, татах шаардлагагүй: {localFullPath}");
                return new DriverInstallResult { Success = true, Message = "Driver локал дискнээс олдлоо." };
            }

            if (string.IsNullOrWhiteSpace(model.DriverDownloadUrl))
            {
                var msg = $"'{model.ModelName}' загварын driver локал дискнээс олдсонгүй ({localFullPath}), " +
                          "мөн printers.json дотор 'driverDownloadUrl' тохируулагдаагүй тул Online-оос ч " +
                          "татаж авах боломжгүй байна.";
                LogService.Instance.WriteError(msg);
                return new DriverInstallResult { Success = false, Message = msg };
            }

            try
            {
                progress?.Invoke($"'{model.ModelName}' driver Online-оос татаж байна...");
                LogService.Instance.WriteInfo(
                    $"Driver локал дискнээс олдсонгүй, Online-оос татаж эхэлж байна: {model.DriverDownloadUrl}");

                bool isZip = string.Equals(model.DriverPackageType, "zip", StringComparison.OrdinalIgnoreCase)
                    || model.DriverDownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                if (isZip)
                {
                    var tempZipPath = Path.Combine(Path.GetTempPath(), $"pos-driver-{Guid.NewGuid():N}.zip");

                    await DownloadFileWithProgressAsync(model.DriverDownloadUrl!, tempZipPath, progress);

                    progress?.Invoke("Татсан driver package-ийг задалж байна...");
                    LogService.Instance.WriteInfo($"Zip задалж байна: {tempZipPath} -> {driversRoot}");
                    ZipFile.ExtractToDirectory(tempZipPath, driversRoot, overwriteFiles: true);

                    try { File.Delete(tempZipPath); }
                    catch (Exception ex)
                    {
                        LogService.Instance.WriteWarning($"Түр zip файл устгахад алдаа гарлаа (алгасав): {ex.Message}");
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localFullPath) ?? driversRoot);
                    await DownloadFileWithProgressAsync(model.DriverDownloadUrl!, localFullPath, progress);
                }

                if (!File.Exists(localFullPath))
                {
                    var msg = $"Driver татаж/задалсны дараа ч хүлээгдэж буй файл олдсонгүй: {localFullPath}. " +
                              "printers.json дэх 'driverDownloadUrl'/'driverPackageType', эсвэл zip дотрох " +
                              "фолдерийн бүтэц (InfPath/DriverInstallerPath-тай тохирч байгаа эсэх)-ийг шалгана уу.";
                    LogService.Instance.WriteError(msg);
                    return new DriverInstallResult { Success = false, Message = msg };
                }

                progress?.Invoke("Driver амжилттай татагдлаа.");
                LogService.Instance.WriteInfo($"Driver амжилттай татагдаж бэлэн боллоо: {localFullPath}");

                return new DriverInstallResult { Success = true, Message = "Driver Online-оос амжилттай татагдлаа." };
            }
            catch (TaskCanceledException)
            {
                var msg = "Driver татах хугацаа хэтэрлээ (timeout). Интернэт холболтоо шалгаад дахин оролдоно уу.";
                LogService.Instance.WriteError(msg);
                return new DriverInstallResult { Success = false, Message = msg };
            }
            catch (HttpRequestException ex)
            {
                var msg = $"Driver татах явцад сүлжээний алдаа гарлаа: {ex.Message}. Интернэт холболтоо шалгана уу.";
                LogService.Instance.WriteError(msg, ex);
                return new DriverInstallResult { Success = false, Message = msg };
            }
            catch (Exception ex)
            {
                var msg = $"Driver татах явцад алдаа гарлаа: {ex.Message}";
                LogService.Instance.WriteError(msg, ex);
                return new DriverInstallResult { Success = false, Message = msg };
            }
        }

        private static async Task DownloadFileWithProgressAsync(string url, string destinationPath, Action<string>? progress)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            bool canReportPercent = totalBytes > 0;

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? Path.GetTempPath());

            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            int lastReportedPercent = -1;

            while ((bytesRead = await httpStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (canReportPercent)
                {
                    int percent = (int)(totalRead * 100 / totalBytes);
                    if (percent != lastReportedPercent && percent % 10 == 0)
                    {
                        lastReportedPercent = percent;
                        progress?.Invoke($"Татаж байна... {percent}%");
                    }
                }
            }

            LogService.Instance.WriteInfo(
                $"Татаж дууслаа: {destinationPath} ({totalRead:N0} байт, {(canReportPercent ? $"{totalBytes:N0} байтаас" : "хэмжээ тодорхойгүй эх сурвалжаас")})");
        }
    }
}