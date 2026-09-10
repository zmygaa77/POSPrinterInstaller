using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using POSPrinterInstaller.Models;
using POSPrinterInstaller.Services;


namespace POSPrinterInstaller
{
    public partial class MainWindow : Window
    {
        private readonly USBDetectionService _usbDetectionService = new();
        private readonly PrinterDatabaseService _databaseService = new();
        private readonly DriverInstallService _driverInstallService = new();
        private readonly DriverDownloadService _driverDownloadService = new();
        private readonly PrinterService _printerService = new();
        private readonly TestPrintService _testPrintService = new();
        private readonly PrinterPortDetectionService _portDetectionService = new();
        private readonly PrinterCleanupService _cleanupService = new();
        private readonly NetworkPrinterDetectionService _networkDetectionService = new();
        private readonly NetworkAdapterConfigService _adapterConfigService = new();

        private readonly ObservableCollection<DetectedPrinter> _detectedPrinters = new();
        private readonly ObservableCollection<PrinterModel> _networkModels = new();
        private readonly ObservableCollection<NetworkPrinterCandidate> _networkResults = new();
        private readonly ObservableCollection<string> _progressSteps = new();

        public MainWindow()
        {
            InitializeComponent();

            PrintersComboBox.ItemsSource = _detectedPrinters;
            NetworkModelComboBox.ItemsSource = _networkModels;
            NetworkResultsListBox.ItemsSource = _networkResults;
            ProgressListBox.ItemsSource = _progressSteps;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Хэвлэгчийн санг (printers.json) эхлээд Online-оос шалгаж
            // шинэчлэхийг оролдоно (интернэтгүй/тохируулаагүй бол чимээгүйхэн
            // алгасаж, локал (аль хэдийн дискэн дээр байгаа) сангаар
            // үргэлжлүүлнэ — апп хэзээ ч энэ шалтгаанаар гацахгүй/унахгүй).
            SetStatus("Хэвлэгчийн санг шалгаж байна...");
            var updateResult = await _databaseService.TryUpdateDatabaseFromOnlineAsync(
                msg => Dispatcher.Invoke(() => SetStatus(msg)));
            LogService.Instance.WriteInfo(
                $"Online сангийн шалгалт: {(updateResult.Success ? "шинэчлэгдлээ" : "алгассан")} — {updateResult.Message}");

            LoadNetworkModels();
            await ScanPrintersAsync();
        }

        private async void RescanButton_Click(object sender, RoutedEventArgs e)
        {
            await ScanPrintersAsync();
        }

        /// <summary>
        /// USB-г сканнердаж, printers.json дэх өгөгдөлтэй тааруулж,
        /// ComboBox-г шинэчилнэ.
        /// </summary>
        private async Task ScanPrintersAsync()
        {
            SetStatus("Хэвлэгч хайж байна...");
            InstallButton.IsEnabled = false;

            var detected = await Task.Run(() =>
            {
                var usbDevices = _usbDetectionService.DetectUsbDevices();
                _databaseService.LoadPrinterModels();

                foreach (var device in usbDevices)
                {
                    device.MatchedModel = _databaseService.FindMatchingModel(device.Vid, device.Pid, device.DeviceName);
                }

                // Зөвхөн бидний загвартай таарсан (танигдсан) төхөөрөмжүүдийг харуулна
                return usbDevices.Where(d => d.IsRecognized).ToList();
            });

            _detectedPrinters.Clear();
            foreach (var printer in detected)
            {
                _detectedPrinters.Add(printer);
            }

            if (_detectedPrinters.Count == 0)
            {
                SetStatus("Танигдах POS хэвлэгч олдсонгүй. Хэвлэгчээ USB-ээр холбоод \"Дахин шалгах\" дарна уу.");
            }
            else if (_detectedPrinters.Count == 1)
            {
                PrintersComboBox.SelectedIndex = 0;
                SetStatus($"Хэвлэгч олдлоо: {_detectedPrinters[0].DisplayName}");
            }
            else
            {
                SetStatus($"{_detectedPrinters.Count} хэвлэгч олдлоо. Дээрх жагсаалтаас сонгоно уу.");
                PrintersComboBox.SelectedIndex = 0;
            }

            UpdateInstallButtonEnabled();
        }

        /// <summary>
        /// printers.json-с бүх загварыг ачаалж, "Сүлжээ (IP)" tab-ын
        /// хэвлэгчийн загвар сонгох ComboBox-г бөглөнө. Зөвхөн InfPath
        /// заасан (InstallMethod="inf") загварууд л сүлжээгээр (гар аргаар,
        /// printui.dll-ээр) суулгах боломжтой тул бусад (зөвхөн exe-based
        /// installer-тай) загварыг энд оруулахгүй — оруулбал сонгоод
        /// "СУУЛГАХ" дарахад дэмжигдэхгүй гэсэн алдаа шууд харагдана.
        /// </summary>
        private void LoadNetworkModels()
        {
            _networkModels.Clear();

            var allModels = _databaseService.LoadPrinterModels();
            var networkCapable = allModels
                .Where(m => !string.IsNullOrWhiteSpace(m.InfPath))
                .OrderBy(m => m.ModelName)
                .ToList();

            foreach (var model in networkCapable)
            {
                _networkModels.Add(model);
            }
        }

        private void ConnectionTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateInstallButtonEnabled();
        }

        private void PrintersComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PrintersComboBox.SelectedItem is DetectedPrinter printer)
            {
                SetStatus($"Сонгогдсон хэвлэгч: {printer.DisplayName}");
            }
            UpdateInstallButtonEnabled();
        }

        private void NetworkModelComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (NetworkModelComboBox.SelectedItem is PrinterModel model)
            {
                SetStatus($"Сонгогдсон загвар: {model.ModelName}. Одоо IP хаягаа оруулна уу.");
            }
            UpdateInstallButtonEnabled();
        }

        private void IpAddressTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var ip = IpAddressTextBox.Text?.Trim() ?? string.Empty;

            if (IsValidIpAddress(ip))
            {
                bool sameSubnet = _adapterConfigService.IsOnSameSubnet(ip);

                if (sameSubnet)
                {
                    DifferentSubnetPanel.Visibility = Visibility.Collapsed;
                    SetStatus($"'{ip}' танай компьютертой ижил сүлжээнд байна. Шууд суулгаж болно.");
                }
                else
                {
                    DifferentSubnetPanel.Visibility = Visibility.Visible;
                    SetStatus($"'{ip}' танай компьютертой ӨӨР сүлжээнд байна.");
                }
            }
            else
            {
                DifferentSubnetPanel.Visibility = Visibility.Collapsed;
            }

            UpdateInstallButtonEnabled();
        }

        private void NetworkResultsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (NetworkResultsListBox.SelectedItem is NetworkPrinterCandidate candidate)
            {
                IpAddressTextBox.Text = candidate.IpAddress;
            }
        }

        /// <summary>
        /// Идэвхтэй tab-аас хамааран СУУЛГАХ товч идэвхжих эсэхийг тохируулна.
        /// </summary>
        private void UpdateInstallButtonEnabled()
        {
            if (ConnectionTabControl.SelectedIndex == 1)
            {
                InstallButton.IsEnabled = NetworkModelComboBox.SelectedItem != null
                    && IsValidIpAddress(IpAddressTextBox.Text);
            }
            else
            {
                InstallButton.IsEnabled = PrintersComboBox.SelectedItem != null;
            }
        }

        private static bool IsValidIpAddress(string? text)
        {
            return !string.IsNullOrWhiteSpace(text) && IPAddress.TryParse(text.Trim(), out _);
        }

        /// <summary>
        /// Локал сүлжээг (LAN) 9100 портоор скан хийж, боломжит хэвлэгчийн
        /// IP хаягуудыг олж, жагсаалтад харуулна.
        /// </summary>
        private async void ScanNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            ScanNetworkButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
            _networkResults.Clear();
            NetworkResultsListBox.Visibility = Visibility.Collapsed;

            SetStatus("Сүлжээг хайж байна, энэ хэдэн секунд үргэлжилж болно...");

            try
            {
                var found = await _networkDetectionService.ScanLocalNetworkAsync(
                    msg => Dispatcher.Invoke(() => SetStatus(msg)));

                foreach (var candidate in found)
                {
                    _networkResults.Add(candidate);
                }

                if (_networkResults.Count == 0)
                {
                    SetStatus(
                        "Сүлжээнээс автоматаар хэвлэгч олдсонгүй. Хэвлэгч WiFi/Ethernet-д зөв холбогдсон эсэхийг " +
                        "шалгаад дахин оролдоно уу, эсвэл IP хаягийг гараар оруулна уу.");
                }
                else
                {
                    NetworkResultsListBox.Visibility = Visibility.Visible;
                    NetworkResultsListBox.SelectedIndex = 0;
                    SetStatus($"{_networkResults.Count} боломжит хэвлэгч олдлоо. Жагсаалтаас сонгоно уу.");
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Сүлжээ скан хийх явцад алдаа гарлаа", ex);
                SetStatus("Сүлжээ хайх явцад алдаа гарлаа. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
            }
            finally
            {
                ScanNetworkButton.IsEnabled = true;
                UpdateInstallButtonEnabled();
            }
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionTabControl.SelectedIndex == 1)
            {
                await InstallNetworkPrinterAsync();
            }
            else
            {
                await InstallUsbPrinterAsync();
            }
        }

        /// <summary>USB-ээр холбогдсон хэвлэгчийг суулгах (хуучин логик).</summary>
        private async Task InstallUsbPrinterAsync()
        {
            if (PrintersComboBox.SelectedItem is not DetectedPrinter selected || selected.MatchedModel == null)
            {
                MessageBox.Show("Эхлээд хэвлэгчээ сонгоно уу.", "Анхаар", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            InstallButton.IsEnabled = false;
            RescanButton.IsEnabled = false;
            _progressSteps.Clear();

            var model = selected.MatchedModel;

            try
            {
                bool alreadyInstalled = _printerService.IsPrinterInstalled(model.WindowsPrinterName);
                // 1. Хэвлэгч шалгаж байна
                AddProgress("✓ Хэвлэгч шалгаж байна...");
                SetStatus("Хэвлэгчийн мэдээллийг шалгаж байна...");
                await Task.Delay(300);


                // 2. Driver олдлоо
                AddProgress($"✓ Driver олдлоо: {model.DriverInstallerPath}");
                if (!alreadyInstalled)
                {
                    // 2.5. Хуучин/эвдэрсэн (PendingDeletion, давхардсан "(Copy 1)" гэх мэт)
                    // принтерийн бичлэгүүдийг цэвэрлэх. Ингэхгүй бол өмнөх амжилтгүй
                    // суулгалтаас үлдсэн зогсонги бичлэг шинэ хэвлэлтийг Windows error
                    // 1905 (ERROR_PRINTER_DELETED)-ээр саатуулж, Control Panel-д ч
                    // харагдахгүй байх боломжтой.
                    AddProgress("⏳ Хуучин принтерийн бичлэг цэвэрлэж байна...");
                    SetStatus("Хуучин/эвдэрсэн принтерийн бичлэгийг шалгаж байна...");
                    var namePrefix = model.ModelName.Split('(')[0].Trim();
                    await _cleanupService.CleanupStalePrintersAsync(
                        model.WindowsPrinterName,
                        namePrefix,
                        msg => ReplaceLastProgress($"⏳ {msg}"));
                    ReplaceLastProgress("✓ Хуучин принтерийн бичлэг цэвэрлэгдлээ");

                    // 2.6. Driver локал дискнээс олдохгүй бол Online-оос татаж бэлдэх
                    AddProgress("⏳ Driver бэлдэж байна (шаардлагатай бол Online-оос татна)...");
                    SetStatus($"'{model.ModelName}' driver бэлдэж байна...");
                    var downloadResult = await _driverDownloadService.EnsureDriverAvailableAsync(
                        model, msg => ReplaceLastProgress($"⏳ {msg}"));

                    if (!downloadResult.Success)
                    {
                        ReplaceLastProgress($"✗ Driver бэлдэх (татах) явцад алдаа гарлаа: {downloadResult.Message}");
                        SetStatus("Driver бэлдэх амжилтгүй боллоо. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                        return;
                    }
                    ReplaceLastProgress("✓ Driver бэлэн боллоо");

                    // 3. Driver суулгаж байна
                    AddProgress("⏳ Driver суулгаж байна...");
                    SetStatus($"'{model.ModelName}' driver суулгаж байна. Түр хүлээнэ үү...");
                    var installResult = await _driverInstallService.InstallDriverAsync(model);

                    if (!installResult.Success)
                    {
                        ReplaceLastProgress($"✗ Driver суулгах явцад алдаа гарлаа: {installResult.Message}");
                        SetStatus("Driver суулгах амжилтгүй боллоо. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                        return;
                    }
                    ReplaceLastProgress("✓ Driver амжилттай суулгагдлаа");

                    // 4. Хэвлэгч нэмэгдсэн эсэхийг шалгах
                    AddProgress("⏳ Хэвлэгч нэмэгдсэнийг шалгаж байна...");
                    SetStatus("Windows-ийн принтерийн жагсаалтыг шалгаж байна...");
                    await Task.Delay(1500); // driver spooler-т бүртгэгдэж, порт бэлэн болох бага зэргийн хугацаа өгнө

                    bool installed = _printerService.IsPrinterInstalled(model.WindowsPrinterName);

                    if (!installed)
                    {
                        // Зарим POS хэвлэгч нь USB Device ID хүсэлтэд зөв хариу
                        // өгдөггүй тул Windows "UNKNOWNPRINTER" гэж таньдаг —
                        // ийм үед .inf доторх Hardware ID хэзээ ч PnP-ээр
                        // автоматаар таарахгүй тул pnputil driver-ийг staging
                        // хийсэн ч ямар ч принтер үүсгэдэггүй. Иймд гар аргаар
                        // (printui.dll) тухайн загварын жинхэнэ .inf-ийг ашиглан
                        // үүсгэхийг оролдоно.
                        ReplaceLastProgress("⚠ Автоматаар холбогдсонгүй, гар аргаар нэмж оролдож байна...");
                        SetStatus(
                            "Driver Store-д суусан ч Windows автоматаар холбож чадсангүй " +
                            "(хэвлэгч Device ID-гаа зөв дамжуулаагүй байж болзошгүй). USB портыг хайж, " +
                            "гар аргаар принтер нэмж байна...");

                        AddProgress("⏳ USB порт хайж байна...");
                        // Эхлээд Windows-ийн порт жагсаалтын "Description" баганад
                        // (Printer Properties → Ports таб дээр хэрэглэгчийн нүдэнд
                        // харагддагтай яг адилхан утга) хэвлэгчийн нэр/үйлдвэрлэгч
                        // агуулагдсан чөлөөтэй USB порт байгаа эсэхийг шалгана —
                        // энэ нь "хамгийн сүүлийн дугаартай порт" гэсэн зүгээр
                        // таамаглалаас илүү найдвартай, яг тухайн хэвлэгчид
                        // харгалзах портыг олно (жишээ нь "SEWOO TECH LK-B30II").
                        // WindowsPrinterName-ээр олдохгүй бол Manufacturer-ээр дахин
                        // оролдоод, эцэст нь л хуучин "сүүлийн дугаар" heuristic руу
                        // fallback хийнэ.
                        var port = await Task.Run(() =>
                            _portDetectionService.DetectUsbPortByDescriptionKeyword(model.WindowsPrinterName)
                            ?? _portDetectionService.DetectUsbPortByDescriptionKeyword(model.Manufacturer)
                            ?? _portDetectionService.DetectMostRecentUsbPrinterPort());

                        if (string.IsNullOrEmpty(port))
                        {
                            ReplaceLastProgress("✗ USB принтерийн порт олдсонгүй");
                            SetStatus(
                                "Хэвлэгчийн USB порт (USB001, USB002 гэх мэт) олдсонгүй. Хэвлэгчээ USB " +
                                "кабелиар дахин холбож, асаалттай эсэхийг шалгаад 'Дахин шалгах' дараад " +
                                "дахин оролдоно уу.");
                            return;
                        }
                        ReplaceLastProgress($"✓ USB порт олдлоо: {port}");

                        AddProgress("⏳ Принтерийг гар аргаар нэмж байна...");
                        var infFullPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Drivers", model.InfPath);
                        var driverModelName = string.IsNullOrWhiteSpace(model.DriverModelName)
                            ? model.WindowsPrinterName
                            : model.DriverModelName;
                        bool addedManually = await Task.Run(() => _printerService.TryAddPrinterManually(
                            model.WindowsPrinterName, infFullPath, driverModelName, port));

                        if (!addedManually)
                        {
                            ReplaceLastProgress("✗ Гар аргаар нэмэхэд амжилтгүй боллоо");
                            SetStatus(
                                $"'{model.WindowsPrinterName}' принтерийг '{port}' порт дээр гар аргаар ч " +
                                "нэмж чадсангүй. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                            return;
                        }
                        ReplaceLastProgress($"✓ Принтер '{port}' порт дээр гар аргаар нэмэгдлээ");

                        AddProgress("⏳ Windows-д баталгаажуулж байна...");
                        await Task.Delay(1000);
                        installed = _printerService.IsPrinterInstalled(model.WindowsPrinterName);

                        if (!installed)
                        {
                            ReplaceLastProgress("✗ Гар аргаар нэмсэн ч Windows-д баталгаажсангүй");
                            SetStatus(
                                "Гар аргаар нэмэх оролдлого хийсэн ч Windows-ийн принтерийн жагсаалтад " +
                                "баталгаажуулж чадсангүй. Компьютерээ дахин ачаалаад дахин оролдож үзнэ үү.");
                            return;
                        }
                        ReplaceLastProgress("✓ Хэвлэгч нэмэгдсэн (Windows-д баталгаажлаа)");
                    }
                    else
                    {
                        ReplaceLastProgress("✓ Хэвлэгч нэмэгдсэн (Windows-д баталгаажлаа)");
                    }

                }
                else
                {
                    AddProgress("✓ Driver аль хэдийн суусан байна — дахин суулгахгүй");
                    SetStatus($"'{model.ModelName}' аль хэдийн суусан байна. Тохиргоо/тест хэвлэлт хийж байна...");
                }

                // 5. Default принтер болгох (шаардлагатай бол)
                if (model.SetAsDefault)
                {
                    AddProgress("⏳ Үндсэн принтер болгож тохируулж байна...");
                    var setDefault = _printerService.SetDefaultPrinter(model.WindowsPrinterName);
                    ReplaceLastProgress(setDefault
                        ? "✓ Үндсэн принтер болгож тохируулагдлаа"
                        : "✗ Үндсэн принтер болгож чадсангүй (алгасав)");
                }

                // 6. Тест хэвлэлт
                // PrinterType == "label" бол TSPL шошго тест, эс бол хуучин ESC/POS баримт тест.
                // (Sewoo-B30 гэх мэт шошго хэвлэгч рүү ESC/POS явуулбал "амжилтгүй" гарна,
                // учир нь тэр тушаалыг шошго хэвлэгч ойлгодоггүй.)
                bool isLabelPrinter = string.Equals(model.PrinterType, "label", StringComparison.OrdinalIgnoreCase);

                AddProgress("⏳ Test хэвлэж байна...");
                SetStatus(isLabelPrinter ? "Тест шошго хэвлэж байна..." : "Тест хуудас хэвлэж байна...");
                bool printed = await Task.Run(() => _testPrintService.PrintTestPage(
                    model.WindowsPrinterName,
                    model.ModelName,
                    isLabelPrinter,
                    labelWidthMm: model.LabelWidthMm,
                    labelHeightMm: model.LabelHeightMm,
                    labelGapMm: model.LabelGapMm));

                if (printed)
                {
                    ReplaceLastProgress("✓ Test хэвлэж дууслаа");
                    SetStatus($"'{model.ModelName}' амжилттай суугдаж, тест хуудас хэвлэгдлээ! 🎉");
                }
                else
                {
                    ReplaceLastProgress("✗ Test хэвлэлт амжилтгүй боллоо");
                    SetStatus("Driver суусан ч тест хэвлэлт амжилтгүй боллоо. Кабель/цахилгаан холболтоо шалгана уу.");
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Суулгах явцад гэнэтийн алдаа гарлаа", ex);
                AddProgress($"✗ Гэнэтийн алдаа гарлаа: {ex.Message}");
                SetStatus("Гэнэтийн алдаа гарлаа. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
            }
            finally
            {
                InstallButton.IsEnabled = true;
                RescanButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// IP-гээр (сүлжээгээр) холбогдсон хэвлэгчийг суулгах шинэ логик.
        /// Урсгал: (1) driver-ийг Driver Store-д staging хийх (INF аргаар),
        /// (2) тухайн IP хаягт зориулж Standard TCP/IP порт (Win32_TCPIPPrinterPort)
        /// үүсгэх, (3) printui.dll-ээр принтерийг тэр порт дээр гар аргаар
        /// холбох — яг л USB урсгал дахь "гар аргаар нэмэх" алхамтай ижил
        /// PrinterService.TryAddPrinterManually(...) методыг ашиглана,
        /// зөвхөн порт нэр нь "USBnnn" биш "IP_x.x.x.x" байна.
        /// </summary>
        private async Task InstallNetworkPrinterAsync()
        {
            if (NetworkModelComboBox.SelectedItem is not PrinterModel model)
            {
                MessageBox.Show("Эхлээд хэвлэгчийн загвараа сонгоно уу.", "Анхаар", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ip = IpAddressTextBox.Text?.Trim() ?? string.Empty;
            if (!IsValidIpAddress(ip))
            {
                MessageBox.Show("Зөв IP хаяг оруулна уу (жишээ нь 192.168.1.100).", "Анхаар", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(model.InfPath))
            {
                MessageBox.Show(
                    $"'{model.ModelName}' загвар одоогоор зөвхөн USB-ээр (гуравдагч талын .exe driver) суулгагддаг тул " +
                    "сүлжээгээр (IP) автоматаар суулгах боломжгүй байна. Эхлээд нэг удаа USB-ээр холбож суулгана уу, " +
                    "эсвэл printers.json дотор энэ загварт .inf driver package тохируулна уу.",
                    "Дэмжигдэхгүй", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            InstallButton.IsEnabled = false;
            RescanButton.IsEnabled = false;
            ScanNetworkButton.IsEnabled = false;
            _progressSteps.Clear();

            // Нэг IP дээр нэг л удаа суулгах боловч, ижил загварыг олон
            // өөр IP дээр (олон принтер) зэрэг суулгах боломжтой байлгахын
            // тулд Windows дээрх принтерийн нэрэнд IP хаягийг хавсаргана.
            var printerName = $"{model.WindowsPrinterName} ({ip})";

            try
            {
                AddProgress("✓ Хэвлэгч шалгаж байна...");
                SetStatus("Хэвлэгчийн мэдээллийг шалгаж байна...");
                bool alreadyInstalled = _printerService.IsPrinterInstalled(printerName);

                if (!alreadyInstalled)
                {
                    // 0.5. Driver локал дискнээс олдохгүй бол Online-оос татаж бэлдэх
                    AddProgress("⏳ Driver файл бэлдэж байна (шаардлагатай бол Online-оос татна)...");
                    SetStatus($"'{model.ModelName}' driver файл бэлдэж байна...");
                    var downloadResult = await _driverDownloadService.EnsureDriverAvailableAsync(
                        model, msg => ReplaceLastProgress($"⏳ {msg}"));

                    if (!downloadResult.Success)
                    {
                        ReplaceLastProgress($"✗ Driver бэлдэх (татах) явцад алдаа гарлаа: {downloadResult.Message}");
                        SetStatus("Driver бэлдэх амжилтгүй боллоо. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                        return;
                    }
                    ReplaceLastProgress("✓ Driver файл бэлэн боллоо");

                    // 1. Driver Store-д driver-ийг бэлдэх (INF staging)
                    AddProgress("⏳ Driver бэлдэж байна...");
                    SetStatus($"'{model.ModelName}' driver-ийг бэлдэж байна...");
                    var installResult = await _driverInstallService.InstallDriverViaInfAsync(model);

                    if (!installResult.Success)
                    {
                        ReplaceLastProgress($"✗ Driver бэлдэх явцад алдаа гарлаа: {installResult.Message}");
                        SetStatus("Driver бэлдэх амжилтгүй боллоо. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                        return;
                    }
                    ReplaceLastProgress("✓ Driver бэлэн боллоо");

                    // 2. Тухайн IP хаягт зориулж Standard TCP/IP порт үүсгэх
                    AddProgress("⏳ Сүлжээний порт (TCP/IP) үүсгэж байна...");
                    SetStatus($"'{ip}' хаягт зориулж TCP/IP порт үүсгэж байна...");
                    var portName = await Task.Run(() => _networkDetectionService.CreateOrGetTcpIpPort(ip));

                    if (string.IsNullOrEmpty(portName))
                    {
                        ReplaceLastProgress("✗ TCP/IP порт үүсгэж чадсангүй");
                        SetStatus("Сүлжээний порт үүсгэх амжилтгүй боллоо. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                        return;
                    }
                    ReplaceLastProgress($"✓ TCP/IP порт үүсгэлээ: {portName}");

                    // 3. Принтерийг тэр порт дээр (printui.dll-ээр) холбох
                    AddProgress("⏳ Принтерийг нэмж байна...");
                    SetStatus("Принтерийг Windows-д нэмж байна...");
                    var infFullPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Drivers", model.InfPath);
                    var driverModelName = string.IsNullOrWhiteSpace(model.DriverModelName)
                        ? model.WindowsPrinterName
                        : model.DriverModelName;

                    bool added = await Task.Run(() => _printerService.TryAddPrinterManually(
                        printerName, infFullPath, driverModelName, portName));

                    if (!added)
                    {
                        ReplaceLastProgress("✗ Принтер нэмэхэд амжилтгүй боллоо");
                        SetStatus(
                            $"'{printerName}' принтерийг '{portName}' порт дээр нэмж чадсангүй. " +
                            "Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                        return;
                    }
                    ReplaceLastProgress("✓ Принтер амжилттай нэмэгдлээ");

                    AddProgress("⏳ Windows-д баталгаажуулж байна...");
                    await Task.Delay(1000);
                    bool installed = _printerService.IsPrinterInstalled(printerName);

                    if (!installed)
                    {
                        ReplaceLastProgress("✗ Нэмсэн ч Windows-д баталгаажсангүй");
                        SetStatus(
                            "Принтер нэмэх оролдлого хийсэн ч Windows-ийн принтерийн жагсаалтад " +
                            "баталгаажуулж чадсангүй. IP хаягаа шалгаад дахин оролдоно уу.");
                        return;
                    }
                    ReplaceLastProgress("✓ Хэвлэгч нэмэгдсэн (Windows-д баталгаажлаа)");
                }
                else
                {
                    AddProgress("✓ Энэ IP дээрх хэвлэгч аль хэдийн суусан байна — дахин суулгахгүй");
                    SetStatus($"'{model.ModelName}' ({ip}) аль хэдийн суусан байна. Тохиргоо/тест хэвлэлт хийж байна...");
                }

                // 4. Default принтер болгох (шаардлагатай бол)
                if (model.SetAsDefault)
                {
                    AddProgress("⏳ Үндсэн принтер болгож тохируулж байна...");
                    var setDefault = _printerService.SetDefaultPrinter(printerName);
                    ReplaceLastProgress(setDefault
                        ? "✓ Үндсэн принтер болгож тохируулагдлаа"
                        : "✗ Үндсэн принтер болгож чадсангүй (алгасав)");
                }

                // 5. Тест хэвлэлт
                bool isLabelPrinter = string.Equals(model.PrinterType, "label", StringComparison.OrdinalIgnoreCase);

                AddProgress("⏳ Test хэвлэж байна...");
                SetStatus(isLabelPrinter ? "Тест шошго хэвлэж байна..." : "Тест хуудас хэвлэж байна...");
                bool printed = await Task.Run(() => _testPrintService.PrintTestPage(
                    printerName,
                    model.ModelName,
                    isLabelPrinter,
                    labelWidthMm: model.LabelWidthMm,
                    labelHeightMm: model.LabelHeightMm,
                    labelGapMm: model.LabelGapMm));

                if (printed)
                {
                    ReplaceLastProgress("✓ Test хэвлэж дууслаа");
                    SetStatus($"'{model.ModelName}' ({ip}) амжилттай суугдаж, тест хуудас хэвлэгдлээ! 🎉");
                }
                else
                {
                    ReplaceLastProgress("✗ Test хэвлэлт амжилтгүй боллоо");
                    SetStatus(
                        "Driver/порт суусан ч тест хэвлэлт амжилтгүй боллоо. Хэвлэгч асаалттай, IP хаяг зөв, " +
                        "мөн компьютертой ижил сүлжээнд байгаа эсэхийг шалгана уу.");
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Сүлжээгээр (IP) суулгах явцад гэнэтийн алдаа гарлаа", ex);
                AddProgress($"✗ Гэнэтийн алдаа гарлаа: {ex.Message}");
                SetStatus("Гэнэтийн алдаа гарлаа. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
            }
            finally
            {
                InstallButton.IsEnabled = true;
                RescanButton.IsEnabled = true;
                ScanNetworkButton.IsEnabled = true;
                UpdateInstallButtonEnabled();
            }
        }

        /// <summary>
        /// Хэвлэгч notebook-той өөр subnet-т байх үед дараах бүх алхмыг
        /// НЭГ товчоор дараалан, БҮРЭН АВТОМАТААР гүйцэтгэнэ:
        ///   1. Notebook-ийн одоогийн (router-ийн сүлжээн дэх) IP-г хадгална.
        ///   2. Notebook-ийн IP-г түр хэвлэгчтэй ижил subnet рүү шилжүүлнэ.
        ///   3. Хэвлэгчийн built-in веб тохиргооны хуудсыг (жишээ нь "IP Info")
        ///      уншиж, доторх маягтын IP талбарыг ХАДГАЛСАН notebook IP-гээр
        ///      бөглөж, "Send" товч дарсантай ЯГ АДИЛХАН HTTP хүсэлт илгээнэ —
        ///      ингэснээр хэвлэгч ӨӨРӨӨ notebook-ийн хуучин (router-ийн
        ///      сүлжээнд аль хэдийн ажилладаг байсан) IP-г авна.
        ///   4. Notebook-ийн IP-г эргүүлж "Auto" (DHCP) горимд шилжүүлнэ.
        ///
        /// Эцэст нь хэрэглэгч хэвлэгчээ router-руугаа буцааж холбоод,
        /// шинэ IP (өмнөх notebook IP)-гээр "СУУЛГАХ" дарна.
        /// </summary>
        private async void AutoUpdatePrinterIpButton_Click(object sender, RoutedEventArgs e)
        {
            var printerIp = IpAddressTextBox.Text?.Trim() ?? string.Empty;
            if (!IsValidIpAddress(printerIp))
            {
                MessageBox.Show("Зөв IP хаяг оруулна уу (жишээ нь 192.168.0.129).", "Анхаар", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                "Дараах үйлдлүүд АВТОМАТААР дараалан хийгдэнэ:\n\n" +
                "1) Notebook-ийн одоогийн IP хадгалагдана\n" +
                "2) Notebook-ийн IP хэвлэгчтэй ижил сүлжээ рүү түр шилжинэ\n" +
                "3) Хэвлэгчийн веб тохиргоонд ХАДГАЛСАН notebook IP автоматаар бичигдэж илгээгдэнэ\n" +
                "4) Notebook-ийн IP буцаад \"Auto\" (DHCP) болно\n\n" +
                "Энэ хугацаанд notebook-ийн интернэт/сүлжээ түр тасалдаж болзошгүй.\n\nҮргэлжлүүлэх үү?",
                "Баталгаажуулах", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            AutoUpdatePrinterIpButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
            ScanNetworkButton.IsEnabled = false;
            _progressSteps.Clear();

            var adapter = _adapterConfigService.FindEthernetAdapter();
            if (adapter == null)
            {
                MessageBox.Show(
                    "Идэвхтэй Ethernet (утастай LAN) адаптер олдсонгүй. Хэвлэгчийг LAN кабелиар notebook-руугаа " +
                    "шууд холбосон эсэхээ шалгана уу.",
                    "Адаптер олдсонгүй", MessageBoxButton.OK, MessageBoxImage.Warning);
                AutoUpdatePrinterIpButton.IsEnabled = true;
                InstallButton.IsEnabled = true;
                ScanNetworkButton.IsEnabled = true;
                return;
            }

            // 1. АЮУЛГҮЙ БАЙДАЛ: юу ч өөрчлөхийн өмнө notebook-ийн одоогийн
            //    IP-г заавал хадгална. Энэ хадгалсан IP нь ХЭВЛЭГЧИЙН ШИНЭ
            //    IP болж хувирна (доор алхам 3).
            AddProgress("⏳ Notebook-ийн одоогийн IP хадгалж байна...");
            var snapshot = await Task.Run(() => _adapterConfigService.CaptureSnapshot(adapter));

            if (snapshot == null || snapshot.PreviousIpAddresses.Length == 0)
            {
                ReplaceLastProgress("✗ Notebook-ийн одоогийн IP-г хадгалж чадсангүй — зогслоо");
                SetStatus(
                    "Notebook-ийн одоогийн сүлжээний тохиргоог уншиж чадсангүй тул аюулгүй байдлын " +
                    "үүднээс IP-г огт өөрчлөөгүй болно. Admin эрхээр ажиллуулж байгаа эсэхээ шалгана уу.");
                AutoUpdatePrinterIpButton.IsEnabled = true;
                InstallButton.IsEnabled = true;
                ScanNetworkButton.IsEnabled = true;
                return;
            }

            var newPrinterIp = snapshot.PreviousIpAddresses[0];
            ReplaceLastProgress($"✓ Notebook-ийн IP хадгалагдлаа: {newPrinterIp} (энэ нь хэвлэгчийн шинэ IP болно)");

            bool ipChanged = false;

            try
            {
                // 2. Notebook-ийн IP-г хэвлэгчтэй ижил subnet рүү түр шилжүүлэх
                AddProgress("⏳ Notebook-ийн IP-г хэвлэгчтэй ижил сүлжээ рүү түр шилжүүлж байна...");
                SetStatus("Notebook-ийн IP-г түр хугацаагаар өөрчилж байна (Admin эрх шаардана)...");

                ipChanged = await _adapterConfigService.ApplyTemporaryIpForPrinterAsync(snapshot, printerIp);
                if (!ipChanged)
                {
                    ReplaceLastProgress("✗ Notebook-ийн IP-г түр өөрчлөх амжилтгүй боллоо");
                    SetStatus("Notebook-ийн IP-г өөрчлөх амжилтгүй боллоо. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
                    return;
                }
                ReplaceLastProgress("✓ Notebook-ийн IP түр өөрчлөгдлөө");

                AddProgress("⏳ Сүлжээ тогтворжихыг хүлээж байна...");
                await Task.Delay(2000);
                ReplaceLastProgress("✓ Сүлжээ бэлэн боллоо");

                // 3. Хэвлэгчийн веб тохиргооны хуудсыг уншиж, IP-г автоматаар солих
                AddProgress("⏳ Хэвлэгчийн веб тохиргооны хуудас руу холбогдож байна...");
                SetStatus($"Хэвлэгчийн ({printerIp}) веб тохиргооны хуудсыг уншиж байна...");

                var webConfigService = new PrinterWebConfigService();
                var result = await webConfigService.UpdatePrinterIpAsync(printerIp, newPrinterIp);

                if (!result.Success)
                {
                    ReplaceLastProgress($"✗ Хэвлэгчийн IP-г автоматаар солиход амжилтгүй боллоо: {result.Message}");
                    SetStatus(
                        $"Хэвлэгчийн IP-г автоматаар солиход амжилтгүй боллоо: {result.Message}\n" +
                        $"Гараар хийхийг хүсвэл хөтчөөр http://{printerIp} руу орж, IP-г {newPrinterIp} болгоно уу.");
                    return;
                }

                ReplaceLastProgress($"✓ Хэвлэгчийн IP-г {newPrinterIp} болгож амжилттай илгээлээ");
                AddProgress("✅ Хэвлэгч дахин ачаалж болзошгүй — хэдэн секунд хүлээнэ үү");

                SetStatus(
                    $"Хэвлэгчийн IP-г '{newPrinterIp}' болгож амжилттай солилоо. Одоо хэвлэгчээ router-руугаа буцааж " +
                    "холбоод, доорх IP хаягийг шинэчилж 'СУУЛГАХ' дараарай.");
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Хэвлэгчийн IP автоматаар солих явцад гэнэтийн алдаа гарлаа", ex);
                AddProgress($"✗ Гэнэтийн алдаа гарлаа: {ex.Message}");
                SetStatus("Гэнэтийн алдаа гарлаа. Logs фолдер дэх дэлгэрэнгүй мэдээллийг харна уу.");
            }
            finally
            {
                // 4. АЮУЛГҮЙ БАЙДАЛ: IP өөрчлөгдсөн эсэхээс үл хамааран
                //    (амжилттай ч, дунд зам дээр алдаа гарсан ч) notebook-ийн
                //    IP-г ЗААВАЛ буцааж "Auto" (DHCP) горимд шилжүүлнэ.
                if (ipChanged)
                {
                    AddProgress("⏳ Notebook-ийн IP-г \"Auto\" (DHCP) болгож байна...");
                    var restoredToDhcp = await _adapterConfigService.SetAdapterToDhcpAsync(adapter);

                    ReplaceLastProgress(restoredToDhcp
                        ? "✓ Notebook-ийн IP \"Auto\" (DHCP) болгогдлоо"
                        : "✗ АНХААР: Notebook-ийн IP-г автоматаар \"Auto\" болгож чадсангүй!");

                    if (!restoredToDhcp)
                    {
                        MessageBox.Show(
                            "Notebook-ийн сүлжээний IP-г автоматаар \"Auto\" (DHCP) болгож чадсангүй!\n\n" +
                            "Control Panel → Network Connections → тухайн Ethernet адаптер → Properties → " +
                            "TCP/IPv4 руу орж, 'Obtain an IP address automatically'-г гараар сонгоно уу.\n\n" +
                            "Logs фолдероос дэлгэрэнгүй мэдээллийг харж болно.",
                            "Анхаар: Гараар сэргээх шаардлагатай",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                // Тохиргоо шинэчлэгдсэн IP-г "IP хаяг" талбарт автоматаар
                // бөглөж, хэрэглэгч router-т буцааж холбосны дараа шууд
                // "СУУЛГАХ" дарж болохоор бэлдэнэ.
                IpAddressTextBox.Text = newPrinterIp;

                AutoUpdatePrinterIpButton.IsEnabled = true;
                InstallButton.IsEnabled = true;
                ScanNetworkButton.IsEnabled = true;
            }
        }


        private void SetStatus(string text) => StatusTextBlock.Text = text;

        private void AddProgress(string text) => _progressSteps.Add(text);

        private void ReplaceLastProgress(string text)
        {
            if (_progressSteps.Count > 0)
            {
                _progressSteps[_progressSteps.Count - 1] = text;
            }
            else
            {
                _progressSteps.Add(text);
            }
        }
    }
}