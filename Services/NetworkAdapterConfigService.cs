using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Notebook-ийн Ethernet адаптерийн одоогийн IP тохиргоог бүрэн
    /// хадгалж авсан бичлэг. IP-г сэргээхэд яг ЭНЭ бичлэгт байгаа утгууд
    /// руу буцаана (DHCP байсан бол DHCP руу, static байсан бол яг тэр
    /// static утгууд руу) — хэрэглэгчийн офисын сүлжээний тохиргоо
    /// санамсаргүй алдагдахаас сэргийлнэ.
    /// </summary>
    /// <summary>
    /// Хэвлэгчийн ВЕБ ТОХИРГООНЫ ХУУДСАН дээр хэрэглэгч өөрөө гараар оруулах
    /// зорилготой, router-ийн сүлжээнд тохирсон санал болгож буй сүлжээний
    /// мэдээлэл (IP/Mask/Gateway, эсвэл DHCP горим санал болгох).
    /// </summary>
    public class SuggestedNetworkInfo
    {
        /// <summary>Санал болгож буй static IP (RecommendDhcp = true үед null байна).</summary>
        public string? SuggestedIp { get; set; }
        public string SubnetMask { get; set; } = "255.255.255.0";
        public string? Gateway { get; set; }

        /// <summary>
        /// true бол router-ийн сүлжээ DHCP горимоор ажилладаг гэж үзэж,
        /// хэвлэгчийг ч мөн DHCP горимд тавихыг санал болгоно (энэ нь
        /// IP давхцах эрсдэлээс хамгийн найдвартай зайлсхийх арга).
        /// </summary>
        public bool RecommendDhcp { get; set; }
    }

    public class AdapterIpSnapshot
    {
        public string AdapterDescription { get; set; } = string.Empty;
        public uint InterfaceIndex { get; set; }

        /// <summary>true бол DHCP-ээр IP авдаг байсан, false бол static (гараар) тохируулсан байсан.</summary>
        public bool WasDhcp { get; set; }

        /// <summary>WasDhcp = false үед л хэрэглэгдэнэ — өмнөх static IP хаяг(ууд).</summary>
        public string[] PreviousIpAddresses { get; set; } = Array.Empty<string>();

        /// <summary>WasDhcp = false үед л хэрэглэгдэнэ — өмнөх subnet mask(ууд).</summary>
        public string[] PreviousSubnetMasks { get; set; } = Array.Empty<string>();

        /// <summary>WasDhcp = false үед л хэрэглэгдэнэ — өмнөх gateway(ууд).</summary>
        public string[] PreviousGateways { get; set; } = Array.Empty<string>();

        /// <summary>Өмнөх DNS сервер(үүд) (DHCP эсэхээс үл хамааран DNS нь гараар тохируулагдсан байж болно).</summary>
        public string[] PreviousDnsServers { get; set; } = Array.Empty<string>();

        /// <summary>true бол DNS нь DHCP-ээр автоматаар ирдэг байсан.</summary>
        public bool DnsWasDhcp { get; set; }
    }

    /// <summary>
    /// Хэвлэгч өөр subnet-тэй үед, notebook-ийн Ethernet адаптерийн IP-г
    /// ТҮР ХУГАЦААГААР хэвлэгчтэй ижил subnet рүү шилжүүлж, шалгаж/суулгаж
    /// дууссаны дараа яг өмнөх тохиргоог нь найдвартай сэргээдэг сервис.
    ///
    /// Аюулгүй байдлын зарчим:
    ///   1. Юу ч өөрчлөхийн өмнө одоогийн тохиргоог ЗААВАЛ бүрэн хадгална
    ///      (Snapshot). Хадгалж чадаагүй бол ОГТ өөрчлөхгүй.
    ///   2. Өөрчлөлт хийсний дараа (амжилттай ч, амжилтгүй ч) caller талд
    ///      ЗААВАЛ RestoreAsync дуудуулна (try/finally хэлбэрээр) — ингэснээр
    ///      программ гэнэт унасан ч, дараагийн удаа апп нээгдэхэд ядаж
    ///      log-оос харж, хэрэглэгч гараар сэргээх боломжтой байна.
    /// </summary>
    public class NetworkAdapterConfigService
    {
        /// <summary>
        /// Идэвхтэй (Up), утастай (Ethernet) анхны сүлжээний адаптерийг олно.
        /// WiFi болон виртуал (VPN, Hyper-V, Bluetooth PAN) адаптеруудыг
        /// алгасна, учир нь хэвлэгчийг ихэвчлэн шууд LAN кабелиар холбодог.
        /// </summary>
        public NetworkInterface? FindEthernetAdapter()
        {
            try
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic =>
                        nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                        nic.OperationalStatus == OperationalStatus.Up &&
                        !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    .FirstOrDefault();

                if (adapter == null)
                {
                    LogService.Instance.WriteWarning("Идэвхтэй (Up) Ethernet (утастай LAN) адаптер олдсонгүй.");
                }

                return adapter;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Ethernet адаптер хайх явцад алдаа гарлаа", ex);
                return null;
            }
        }

        /// <summary>
        /// Өгөгдсөн адаптерийн одоогийн IP тохиргоог (DHCP эсэх, static IP/
        /// mask/gateway, DNS) бүрэн уншиж, дараа сэргээхэд ашиглах Snapshot
        /// үүсгэнэ. Уншиж чадахгүй бол null буцаана — ийм үед ОГТ өөрчлөлт
        /// хийхгүй байх ёстой (аюулгүй байдлын шалтгаанаар).
        /// </summary>
        public AdapterIpSnapshot? CaptureSnapshot(NetworkInterface adapter)
        {
            try
            {
                var config = FindWmiConfigForAdapter(adapter);
                if (config == null)
                {
                    LogService.Instance.WriteError(
                        $"'{adapter.Description}' адаптерт харгалзах WMI (Win32_NetworkAdapterConfiguration) бичлэг олдсонгүй.");
                    return null;
                }

                bool dhcpEnabled = Convert.ToBoolean(config["DHCPEnabled"] ?? false);
                var ipAddresses = (string[]?)config["IPAddress"] ?? Array.Empty<string>();
                var subnetMasks = (string[]?)config["IPSubnet"] ?? Array.Empty<string>();
                var gateways = (string[]?)config["DefaultIPGateway"] ?? Array.Empty<string>();
                var dnsServers = (string[]?)config["DNSServerSearchOrder"] ?? Array.Empty<string>();

                // DNS DHCP эсэхийг шууд заасан properties байдаггүй тул:
                // DHCPEnabled=true үед DNSServerSearchOrder ихэвчлэн хоосон
                // байвал автомат DNS гэж үзнэ.
                bool dnsWasDhcp = dhcpEnabled && dnsServers.Length == 0;

                var snapshot = new AdapterIpSnapshot
                {
                    AdapterDescription = adapter.Description,
                    InterfaceIndex = Convert.ToUInt32(config["InterfaceIndex"] ?? 0u),
                    WasDhcp = dhcpEnabled,
                    PreviousIpAddresses = ipAddresses,
                    PreviousSubnetMasks = subnetMasks,
                    PreviousGateways = gateways,
                    PreviousDnsServers = dnsServers,
                    DnsWasDhcp = dnsWasDhcp
                };

                LogService.Instance.WriteInfo(
                    $"Адаптерийн одоогийн тохиргоог хадгаллаа: '{adapter.Description}' " +
                    $"(DHCP={dhcpEnabled}, IP={string.Join(",", ipAddresses)})");

                return snapshot;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Адаптерийн одоогийн тохиргоог хадгалах (Snapshot) явцад алдаа гарлаа", ex);
                return null;
            }
        }

        /// <summary>
        /// Адаптерийг өгөгдсөн хэвлэгчийн IP-тэй ИЖИЛ subnet-т орох ТҮР
        /// static IP рүү шилжүүлнэ (Administrator эрх шаардана). Snapshot-г
        /// эхэлж заавал CaptureSnapshot()-оор авсан байх ёстой.
        /// </summary>
        /// <param name="printerIp">Хэвлэгчийн IP хаяг, жишээ нь "192.168.123.100"</param>
        /// <returns>Амжилттай эсэх</returns>
        public async Task<bool> ApplyTemporaryIpForPrinterAsync(AdapterIpSnapshot snapshot, string printerIp)
        {
            try
            {
                var octets = printerIp.Split('.');
                if (octets.Length != 4)
                {
                    LogService.Instance.WriteError($"Буруу IP хаяг: {printerIp}");
                    return false;
                }

                // Хэвлэгчийн subnet-тэй ИЖИЛ, гэхдээ хэвлэгчийн IP-тэй ЯГ
                // ДАВХЦАХГҮЙ static IP сонгоно (сүүлийн octet-ийг +1 хийж,
                // 255 давбал -1 хийж эргэлдүүлнэ — практикт хэвлэгчийн IP
                // ихэвчлэн .1-.254 хооронд байдаг тул зөрчилдөх магадлал бага).
                int lastOctet = int.Parse(octets[3]);
                int tempLastOctet = lastOctet >= 250 ? lastOctet - 5 : lastOctet + 5;
                var tempIp = $"{octets[0]}.{octets[1]}.{octets[2]}.{tempLastOctet}";
                const string tempSubnetMask = "255.255.255.0";

                LogService.Instance.WriteInfo(
                    $"'{snapshot.AdapterDescription}' адаптерийг түр {tempIp}/{tempSubnetMask} болгож тохируулж байна " +
                    $"(зорилго: {printerIp} хэвлэгчтэй ижил сүлжээнд орох).");

                var config = FindWmiConfigByInterfaceIndex(snapshot.InterfaceIndex);
                if (config == null)
                {
                    LogService.Instance.WriteError("Адаптерийн WMI бичлэг олдсонгүй (тохируулах гэж байхад).");
                    return false;
                }

                return await Task.Run(() =>
                {
                    var enableStaticResult = config.InvokeMethod(
                        "EnableStatic",
                        new object[] { new[] { tempIp }, new[] { tempSubnetMask } });

                    uint returnCode = Convert.ToUInt32(enableStaticResult);

                    // 0 = амжилттай, 1 = амжилттай ч дахин ачаалах шаардлагатай
                    if (returnCode != 0 && returnCode != 1)
                    {
                        LogService.Instance.WriteError($"EnableStatic буцаасан алдааны код: {returnCode}");
                        return false;
                    }

                    LogService.Instance.WriteInfo($"Адаптерийн IP-г түр {tempIp} болгож амжилттай тохирууллаа.");
                    return true;
                });
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Адаптерийн IP-г түр тохируулах явцад алдаа гарлаа", ex);
                return false;
            }
        }

        /// <summary>
        /// Snapshot-оос үл хамааран, адаптерийг шууд DHCP ("Obtain an IP
        /// address automatically") горимд шилжүүлнэ. Хэвлэгчийн IP-г веб
        /// маягтаар автоматаар солих товч дараа notebook-ийг энгийнээр
        /// "Auto" болгоход ашиглагдана.
        /// </summary>
        public async Task<bool> SetAdapterToDhcpAsync(NetworkInterface adapter)
        {
            try
            {
                var config = FindWmiConfigForAdapter(adapter);
                if (config == null)
                {
                    LogService.Instance.WriteError(
                        $"'{adapter.Description}' адаптерийг DHCP болгох гэж байхад WMI бичлэг олдсонгүй.");
                    return false;
                }

                return await Task.Run(() =>
                {
                    var result = config.InvokeMethod("EnableDHCP", null);
                    uint code = Convert.ToUInt32(result);
                    bool ok = code == 0 || code == 1;

                    if (ok)
                    {
                        LogService.Instance.WriteInfo($"'{adapter.Description}' адаптерийг DHCP (Auto) горимд шилжүүллээ.");
                    }
                    else
                    {
                        LogService.Instance.WriteError($"EnableDHCP буцаасан алдааны код: {code}");
                    }

                    return ok;
                });
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Адаптерийг DHCP горимд шилжүүлэх явцад алдаа гарлаа", ex);
                return false;
            }
        }

        /// <summary>
        /// Snapshot-д хадгалагдсан ЯГ ӨМНӨХ тохиргоог адаптерт сэргээж
        /// буцаана (DHCP байсан бол DHCP руу, static байсан бол яг тэр
        /// static IP/mask/gateway/DNS руу). Аюулгүй байдлын үүднээс энэ
        /// method-ыг ЗААВАЛ try/finally дотор дуудах ёстой — IP өөрчлөх
        /// оролдлого амжилтгүй болсон ч, амжилттай болсон ч ялгаагүй.
        /// </summary>
        public async Task<bool> RestorePreviousConfigAsync(AdapterIpSnapshot snapshot)
        {
            try
            {
                var config = FindWmiConfigByInterfaceIndex(snapshot.InterfaceIndex);
                if (config == null)
                {
                    LogService.Instance.WriteError(
                        $"Адаптерийн IP тохиргоог сэргээх боломжгүй боллоо — WMI бичлэг олдсонгүй " +
                        $"('{snapshot.AdapterDescription}'). Хэрэглэгч Control Panel > Network Adapters-с " +
                        "гараар сэргээх шаардлагатай!");
                    return false;
                }

                bool success;

                if (snapshot.WasDhcp)
                {
                    success = await Task.Run(() =>
                    {
                        var result = config.InvokeMethod("EnableDHCP", null);
                        uint code = Convert.ToUInt32(result);
                        return code == 0 || code == 1;
                    });

                    if (success)
                    {
                        LogService.Instance.WriteInfo(
                            $"'{snapshot.AdapterDescription}' адаптерийг амжилттай DHCP горимд сэргээлээ.");
                    }
                }
                else
                {
                    success = await Task.Run(() =>
                    {
                        var enableStaticResult = config.InvokeMethod(
                            "EnableStatic",
                            new object[] { snapshot.PreviousIpAddresses, snapshot.PreviousSubnetMasks });
                        uint code = Convert.ToUInt32(enableStaticResult);
                        return code == 0 || code == 1;
                    });

                    if (success && snapshot.PreviousGateways.Length > 0)
                    {
                        await Task.Run(() =>
                        {
                            var metrics = snapshot.PreviousGateways.Select(_ => (ushort)1).ToArray();
                            config.InvokeMethod("SetGateways", new object[] { snapshot.PreviousGateways, metrics });
                        });
                    }

                    if (success)
                    {
                        LogService.Instance.WriteInfo(
                            $"'{snapshot.AdapterDescription}' адаптерийг амжилттай өмнөх static тохиргоо " +
                            $"({string.Join(",", snapshot.PreviousIpAddresses)}) руу сэргээлээ.");
                    }
                }

                // DNS сэргээх
                if (success)
                {
                    await Task.Run(() =>
                    {
                        if (snapshot.DnsWasDhcp)
                        {
                            config.InvokeMethod("SetDNSServerSearchOrder", new object[] { null! });
                        }
                        else if (snapshot.PreviousDnsServers.Length > 0)
                        {
                            config.InvokeMethod("SetDNSServerSearchOrder", new object[] { snapshot.PreviousDnsServers });
                        }
                    });
                }

                if (!success)
                {
                    LogService.Instance.WriteError(
                        $"'{snapshot.AdapterDescription}' адаптерийн тохиргоог сэргээж чадсангүй! " +
                        "Хэрэглэгч Control Panel > Network Adapters-с гараар шалгах шаардлагатай.");
                }

                return success;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError(
                    $"Адаптерийн тохиргоог сэргээх явцад ГЭНЭТИЙН АЛДАА гарлаа " +
                    $"('{snapshot.AdapterDescription}'). Хэрэглэгч Control Panel > Network Adapters-с " +
                    "гараар шалгах шаардлагатай!", ex);
                return false;
            }
        }

        /// <summary>
        /// Notebook-ийн одоогийн IP хаяг(ууд)-ыг хэвлэгчийн IP-тэй ижил
        /// /24 subnet-тэй эсэхийг шалгана (эхний 3 octet харьцуулна).
        /// </summary>
        public bool IsOnSameSubnet(string printerIp)
        {
            try
            {
                var printerOctets = printerIp.Split('.');
                if (printerOctets.Length != 4) return false;
                var printerPrefix = $"{printerOctets[0]}.{printerOctets[1]}.{printerOctets[2]}";

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                        var parts = addr.Address.ToString().Split('.');
                        if (parts.Length != 4) continue;

                        var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
                        if (string.Equals(prefix, printerPrefix, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Subnet харьцуулах явцад алдаа гарлаа", ex);
                return false;
            }
        }

        /// <summary>
        /// Хэвлэгчийг router-т буцааж холбоход тохирох сүлжээний мэдээллийг
        /// (санал болгож буй static IP/Mask/Gateway, эсвэл DHCP горим) тооцож
        /// буцаана. Хэрэглэгч энэ мэдээллийг хэвлэгчийн ВЕБ ТОХИРГООНЫ
        /// ХУУДСАН дээр өөрөө гараар оруулна.
        ///
        /// Эх сурвалж (тэргүүлэх дараалал):
        ///   1. Notebook дээр АДАПТЕР РЕКОНФИГУРАЦИ ХИЙГДЭЭГҮЙ, идэвхтэй өөр
        ///      сүлжээний адаптер байвал (жишээ нь WiFi-ээр router-т
        ///      холбогдсон бол) — түүний одоогийн IP/Mask/Gateway-г ашиглана,
        ///      учир нь энэ бол router-ийн БОДИТ сүлжээ.
        ///   2. Байхгүй бол — reconfigure хийгдэж буй ижил адаптерийн
        ///      ӨМНӨХ (snapshot) тохиргоог ашиглана.
        /// </summary>
        public SuggestedNetworkInfo GetSuggestedPrinterNetworkInfo(NetworkInterface adapterBeingReconfigured, AdapterIpSnapshot snapshot)
        {
            try
            {
                var otherAdapter = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(nic =>
                        nic.Id != adapterBeingReconfigured.Id &&
                        nic.OperationalStatus == OperationalStatus.Up &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        nic.GetIPProperties().UnicastAddresses.Any(a =>
                            a.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !a.Address.ToString().StartsWith("169.254")));

                if (otherAdapter != null)
                {
                    var ipProps = otherAdapter.GetIPProperties();
                    var addr = ipProps.UnicastAddresses.First(a =>
                        a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !a.Address.ToString().StartsWith("169.254"));
                    var gateway = ipProps.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                    var mask = addr.IPv4Mask?.ToString() ?? "255.255.255.0";

                    var suggestedIp = BuildSuggestedIp(addr.Address.ToString());

                    LogService.Instance.WriteInfo(
                        $"'{otherAdapter.Description}' (өөр идэвхтэй адаптер) дэх сүлжээний мэдээллээс " +
                        $"хэвлэгчид зориулж санал болгов: IP={suggestedIp}, Mask={mask}, Gateway={gateway}");

                    return new SuggestedNetworkInfo
                    {
                        SuggestedIp = suggestedIp,
                        SubnetMask = mask,
                        Gateway = gateway,
                        RecommendDhcp = false
                    };
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteWarning($"Өөр идэвхтэй адаптерээс сүлжээний мэдээлэл авахад алдаа: {ex.Message}");
            }

            // Fallback: reconfigure хийгдэж буй адаптерийн ӨМНӨХ тохиргоог ашиглана
            if (snapshot.PreviousIpAddresses.Length > 0)
            {
                var mask = snapshot.PreviousSubnetMasks.Length > 0 ? snapshot.PreviousSubnetMasks[0] : "255.255.255.0";
                var gateway = snapshot.PreviousGateways.Length > 0 ? snapshot.PreviousGateways[0] : null;

                if (snapshot.WasDhcp)
                {
                    // DHCP-ээр авсан байсан IP нь router-ийн БОДИТ subnet дотор
                    // байгаа тул ашиглаж болно, гэхдээ DHCP горим (санамсаргүй
                    // давхцахаас зайлсхийхийн тулд) илүү найдвартай тул үүнийг
                    // үндсэн санал болгоно.
                    return new SuggestedNetworkInfo
                    {
                        SuggestedIp = BuildSuggestedIp(snapshot.PreviousIpAddresses[0]),
                        SubnetMask = mask,
                        Gateway = gateway,
                        RecommendDhcp = true
                    };
                }

                return new SuggestedNetworkInfo
                {
                    SuggestedIp = BuildSuggestedIp(snapshot.PreviousIpAddresses[0]),
                    SubnetMask = mask,
                    Gateway = gateway,
                    RecommendDhcp = false
                };
            }

            return new SuggestedNetworkInfo { RecommendDhcp = true };
        }

        private static string BuildSuggestedIp(string referenceIp)
        {
            var octets = referenceIp.Split('.');
            int lastOctet = int.Parse(octets[3]);
            int suggestedLast = lastOctet >= 200 ? lastOctet - 50 : lastOctet + 50;
            return $"{octets[0]}.{octets[1]}.{octets[2]}.{suggestedLast}";
        }

        private static ManagementObject? FindWmiConfigForAdapter(NetworkInterface adapter)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");

                foreach (ManagementObject config in searcher.Get())
                {
                    var description = config["Description"]?.ToString() ?? string.Empty;
                    if (string.Equals(description, adapter.Description, StringComparison.OrdinalIgnoreCase))
                    {
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("WMI-ээр адаптер хайх явцад алдаа гарлаа", ex);
            }

            return null;
        }

        private static ManagementObject? FindWmiConfigByInterfaceIndex(uint interfaceIndex)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = {interfaceIndex}");

                foreach (ManagementObject config in searcher.Get())
                {
                    return config;
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("WMI-ээр адаптер (InterfaceIndex-ээр) хайх явцад алдаа гарлаа", ex);
            }

            return null;
        }
    }
}