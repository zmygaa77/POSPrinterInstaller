using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Сүлжээгээр (Ethernet/WiFi) скан хийж олдсон боломжит хэвлэгчийн IP хаяг.
    /// </summary>
    public class NetworkPrinterCandidate
    {
        public string IpAddress { get; set; } = string.Empty;

        /// <summary>Reverse DNS-ээр олдсон нэр (олдоогүй бол хоосон)</summary>
        public string HostName { get; set; } = string.Empty;

        public string DisplayName => string.IsNullOrWhiteSpace(HostName)
            ? IpAddress
            : $"{IpAddress} ({HostName})";

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Локал сүлжээнд (LAN) холбогдсон, RAW/JetDirect (9100 порт) дэмждэг
    /// сүлжээний (IP) хэвлэгчүүдийг олж танихад зориулсан сервис. Ихэнх
    /// POS/шошго хэвлэгчийн Ethernet/WiFi модуль энэ 9100 портыг ашигладаг
    /// тул тухайн порт нээлттэй IP хаягуудыг хэвлэгч байж болзошгүй гэж
    /// үзнэ. Мөн Windows-ийн Print Spooler-т зориулж тухайн IP хаягт
    /// "Standard TCP/IP Port" үүсгэх функц агуулна.
    /// </summary>
    public class NetworkPrinterDetectionService
    {
        // 9100 = RAW/JetDirect, POS болон шошго хэвлэгчдийн дийлэнх нь дэмждэг стандарт порт.
        private const int PrinterRawPort = 9100;
        private const int ScanTimeoutMs = 250;
        private const int MaxParallelScans = 60;

        /// <summary>
        /// Компьютер холбогдсон бүх идэвхтэй IPv4 /24 дэд сүлжээг (subnet)
        /// сканнердаж, 9100 порт нээлттэй хариулсан IP хаягуудыг буцаана.
        /// </summary>
        /// <param name="progress">Явцын мессеж (UI-д харуулах зориулалттай) callback</param>
        public async Task<List<NetworkPrinterCandidate>> ScanLocalNetworkAsync(
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<NetworkPrinterCandidate>();
            var resultsLock = new object();

            var subnets = GetLocalIPv4Subnets();
            if (subnets.Count == 0)
            {
                progress?.Invoke("Идэвхтэй сүлжээний адаптер олдсонгүй.");
                LogService.Instance.WriteWarning("Сүлжээ скан хийхэд идэвхтэй IPv4 адаптер олдсонгүй.");
                return results;
            }

            foreach (var (networkPrefix, ownIp) in subnets)
            {
                progress?.Invoke($"{networkPrefix}.0/24 сүлжээг хайж байна...");

                var ipsToScan = Enumerable.Range(1, 254)
                    .Select(i => $"{networkPrefix}.{i}")
                    .Where(ip => ip != ownIp)
                    .ToList();

                using var semaphore = new SemaphoreSlim(MaxParallelScans);
                var tasks = ipsToScan.Select(async ip =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        if (await IsPortOpenAsync(ip, PrinterRawPort, ScanTimeoutMs, cancellationToken))
                        {
                            var hostName = TryResolveHostName(ip);
                            var candidate = new NetworkPrinterCandidate { IpAddress = ip, HostName = hostName };

                            lock (resultsLock)
                            {
                                results.Add(candidate);
                            }
                            progress?.Invoke($"Боломжит хэвлэгч олдлоо: {candidate.DisplayName}");
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }

            LogService.Instance.WriteInfo(
                $"Сүлжээний скан дууслаа. {results.Count} боломжит хэвлэгч (9100 порт нээлттэй) олдлоо.");

            return results.OrderBy(r => r.IpAddress, StringComparer.Ordinal).ToList();
        }

        private static async Task<bool> IsPortOpenAsync(string ip, int port, int timeoutMs, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ip, port);
                var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
                var completed = await Task.WhenAny(connectTask, timeoutTask);

                return completed == connectTask && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static string TryResolveHostName(string ip)
        {
            try
            {
                var entry = Dns.GetHostEntry(ip);
                return entry.HostName ?? string.Empty;
            }
            catch
            {
                // Ихэнх принтер reverse DNS бүртгэлгүй байдаг тул алдаа
                // гарах нь энгийн зүйл — зүгээр IP хаягаараа харуулна.
                return string.Empty;
            }
        }

        /// <summary>
        /// Идэвхтэй (Up), loopback биш сүлжээний адаптеруудын IPv4 /24
        /// сүлжээний угтвар (жишээ нь "192.168.1") болон тухайн адаптерийн
        /// өөрийн IP хаягийг буцаана.
        /// </summary>
        private static List<(string NetworkPrefix, string OwnIp)> GetLocalIPv4Subnets()
        {
            var subnets = new List<(string, string)>();

            try
            {
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
                        if (!subnets.Any(s => s.Item1 == prefix))
                        {
                            subnets.Add((prefix, addr.Address.ToString()));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Сүлжээний адаптер жагсаах явцад алдаа гарлаа", ex);
            }

            return subnets;
        }

        /// <summary>
        /// Windows Print Spooler дотор өгөгдсөн IP хаягт зориулж "Standard
        /// TCP/IP Port" (RAW протокол, 9100 порт) үүсгэнэ. Тухайн порт аль
        /// хэдийн байгаа бол шинээр үүсгэхгүй, зүгээр нэрийг нь буцаана.
        ///
        /// Буцаах порт нэр (жишээ нь "IP_192.168.1.50") нь
        /// PrinterService.TryAddPrinterManually(...)-ийн portName параметрт
        /// шууд дамжуулагдахад зориулагдсан — USB порт (жишээ нь "USB007")-
        /// той яг адилхан замаар ашиглагдана.
        /// </summary>
        public string? CreateOrGetTcpIpPort(string ipAddress, int portNumber = PrinterRawPort)
        {
            var portName = $"IP_{ipAddress}";

            try
            {
                using var existingSearcher = new ManagementObjectSearcher(
                    $"SELECT Name FROM Win32_TCPIPPrinterPort WHERE Name = '{EscapeForWmi(portName)}'");

                foreach (ManagementObject _ in existingSearcher.Get())
                {
                    LogService.Instance.WriteInfo($"TCP/IP порт аль хэдийн байна, дахин ашиглаж байна: {portName}");
                    return portName;
                }

                using var portClass = new ManagementClass("Win32_TCPIPPrinterPort");
                using var newPort = portClass.CreateInstance();

                if (newPort == null)
                {
                    LogService.Instance.WriteError(
                        "Win32_TCPIPPrinterPort.CreateInstance() null буцаалаа — WMI бэлэн бус байж болзошгүй.");
                    return null;
                }

                newPort["Name"] = portName;
                newPort["HostAddress"] = ipAddress;
                newPort["PortNumber"] = portNumber.ToString();
                newPort["Protocol"] = 1; // 1 = RAW (9100), 2 = LPR
                newPort["SNMPEnabled"] = false;

                newPort.Put();

                LogService.Instance.WriteInfo($"Шинэ TCP/IP порт үүсгэлээ: {portName} ({ipAddress}:{portNumber})");
                return portName;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError($"'{ipAddress}' хаягт TCP/IP порт үүсгэх явцад алдаа гарлаа", ex);
                return null;
            }
        }

        private static string EscapeForWmi(string value) => value.Replace("'", "''");
    }
}