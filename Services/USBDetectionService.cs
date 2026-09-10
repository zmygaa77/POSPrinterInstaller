using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using POSPrinterInstaller.Models;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Windows-ийн WMI (Win32_PnPEntity) ашиглан компьютерт холбогдсон
    /// бүх USB төхөөрөмжийг уншиж, тэдгээрийн дотроос VID/PID-г задалж авдаг сервис.
    ///
    /// Яагаад хэрэгтэй вэ:
    /// Хэрэглэгч ямар нэг POS хэвлэгч холбохоор Windows үүнийг USB төхөөрөмж
    /// болгон таньдаг ч, аль загварынх болохыг мэдэхийн тулд бид VID/PID-г
    /// уншиж аваад өөрсдийн printers.json өгөгдлийн сантай харьцуулах хэрэгтэй.
    /// </summary>
    public class USBDetectionService
    {
        // PNPDeviceID жишээ: "USB\VID_0483&PID_5743\6&1A2B3C4D&0&1"
        private static readonly Regex VidPidRegex =
            new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

        /// <summary>
        /// Одоогоор компьютерт холбогдсон USB төхөөрөмжүүдийг сканнердаж,
        /// VID/PID-тэй нь ялгаж жагсаалт болгож буцаана.
        /// </summary>
        public List<DetectedPrinter> DetectUsbDevices()
        {
            var result = new List<DetectedPrinter>();

            try
            {
                // Win32_PnPEntity нь Windows дээрх бүх Plug and Play төхөөрөмжийг агуулна.
                // DeviceID нь "USB\..." гэж эхэлдэг зүйлсийг л шүүж авна.
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");

                foreach (ManagementObject device in searcher.Get())
                {
                    try
                    {
                        string deviceId = device["PNPDeviceID"]?.ToString() ?? string.Empty;
                        string name = device["Name"]?.ToString() ?? "Тодорхойгүй төхөөрөмж";
                        string manufacturer = device["Manufacturer"]?.ToString() ?? string.Empty;

                        var match = VidPidRegex.Match(deviceId);
                        if (!match.Success)
                        {
                            // VID/PID агуулаагүй USB бичлэгүүд (жишээ нь USB Hub) алгасна
                            continue;
                        }

                        var detected = new DetectedPrinter
                        {
                            Vid = match.Groups[1].Value.ToUpperInvariant(),
                            Pid = match.Groups[2].Value.ToUpperInvariant(),
                            Manufacturer = manufacturer,
                            DeviceName = name,
                            PnpDeviceId = deviceId,
                            HardwareId = deviceId
                        };

                        result.Add(detected);
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.WriteWarning($"Нэг USB төхөөрөмжийг уншихад алдаа гарлаа: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("USB төхөөрөмж сканнердах явцад алдаа гарлаа", ex);
            }

            LogService.Instance.WriteInfo($"USB сканнердалт дууслаа. {result.Count} VID/PID-тэй төхөөрөмж олдлоо.");
            return result;
        }
    }
}
