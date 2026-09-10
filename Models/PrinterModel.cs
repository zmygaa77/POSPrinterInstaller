using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace POSPrinterInstaller.Models
{
    /// <summary>
    /// printers.json дахь нэг принтерийн загварын мэдээлэл.
    /// Энэ бол "Printer database"-ийн нэг мөр (record).
    /// Шинэ принтер нэмэхдээ энэ бүтэцтэй ижил JSON объект нэмнэ,
    /// эх код (C# code) өөрчлөх шаардлагагүй.
    /// </summary>
    public class PrinterModel
    {
        /// <summary>USB Vendor ID, жишээ нь "0483" (VID_ дараах 4 тэмдэгт, hex)</summary>
        [JsonPropertyName("vid")]
        public string Vid { get; set; } = string.Empty;

        /// <summary>USB Product ID, жишээ нь "5743" (PID_ дараах 4 тэмдэгт, hex)</summary>
        [JsonPropertyName("pid")]
        public string Pid { get; set; } = string.Empty;

        /// <summary>Үйлдвэрлэгчийн нэр, жишээ нь "SPRT"</summary>
        [JsonPropertyName("manufacturer")]
        public string Manufacturer { get; set; } = string.Empty;

        /// <summary>Загварын нэр, жишээ нь "SPRT POS58"</summary>
        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = string.Empty;

        /// <summary>
        /// Windows дээр принтер суулгагдсаны дараа харагдах driver/printer-ийн нэр.
        /// Энэ нэрээр бид Windows-ийн принтерийн жагсаалтаас хайж баталгаажуулна.
        /// </summary>
        [JsonPropertyName("windowsPrinterName")]
        public string WindowsPrinterName { get; set; } = string.Empty;

        /// <summary>
        /// Driver-ийн суулгагч файлын зам, Drivers/ фолдероос эхэлсэн харьцангуй зам.
        /// Жишээ нь: "SPRT_POS58\\driver.exe"
        /// InstallMethod = "inf" үед энэ талбарыг ашиглахгүй (InfPath-г ашиглана).
        /// </summary>
        [JsonPropertyName("driverInstallerPath")]
        public string DriverInstallerPath { get; set; } = string.Empty;
        /// <summary>
        /// ЗӨВХӨН гар аргаар (printui.dll /if /m) принтер нэмэх үед хэрэглэгдэнэ.
        /// .inf/.gpd файл дотор ЗААСАН driver-ийн БОДИТ нэр (жишээ нь .gpd
        /// файлын *ModelName мөрөнд бичигдсэн утга) — WindowsPrinterName-ээс
        /// ОГТ ӨӨР байж болно. Хоосон бол WindowsPrinterName-тэй адилхан гэж
        /// үзнэ (backward-compat).
        /// </summary>
        [JsonPropertyName("driverModelName")]
        public string? DriverModelName { get; set; }

        /// <summary>
        /// Суулгах арга:
        ///   "exe" (анхдагч) — Drivers/ дотрох гуравдагч талын driver.exe/.msi
        ///       суулгагчийг шууд ажиллуулна (хуучин арга).
        ///   "inf" — гуравдагч талын installer.exe-г огт ажиллуулахгүйгээр,
        ///       Windows-ийн байгалийн pnputil.exe хэрэгслээр .inf driver
        ///       package-ийг шууд Driver Store-д staging хийж суулгана.
        ///       Энэ нь илүү найдвартай (тодорхойгүй эх сурвалжийн exe-г
        ///       ажиллуулахгүй, GUI wizard-аас хамаардаггүй, script-оор
        ///       бүрэн автоматжуулах боломжтой) арга.
        /// </summary>
        [JsonPropertyName("installMethod")]
        public string InstallMethod { get; set; } = "exe";

        /// <summary>
        /// InstallMethod = "inf" үед ашиглагдана. Drivers/ фолдероос эхэлсэн
        /// харьцангуй зам бөгөөд тухайн driver package-ийн .inf файлыг
        /// заана (жишээ нь "SPRT_POS58\\sprtpos58.inf"). Энэ .inf-тэй ижил
        /// фолдерт харгалзах .cat, .sys, эсвэл бусад туслах файлууд бас
        /// байрлах ёстой (үйлдвэрлэгчийн driver package дотор ихэвчлэн
        /// хамт ирдэг).
        /// </summary>
        [JsonPropertyName("infPath")]
        public string InfPath { get; set; } = string.Empty;

        /// <summary>
        /// Суулгах явцад дамжуулах командын мөрийн параметрүүд (silent install гэх мэт).
        /// Жишээ нь: "/S /VID=0483 /PID=5743"
        /// </summary>
        [JsonPropertyName("installArguments")]
        public string InstallArguments { get; set; } = string.Empty;

        /// <summary>
        /// Driver-ийн файл (InfPath эсвэл DriverInstallerPath заасан зам)
        /// Drivers/ фолдерт локалаар олдохгүй үед татаж авах URL хаяг.
        /// Хоосон бол ЗӨВХӨН локал файл ашиглана (хуучин зан төлөв,
        /// backward-compat) — олдохгүй бол алдаа буцаана.
        /// </summary>
        [JsonPropertyName("driverDownloadUrl")]
        public string? DriverDownloadUrl { get; set; }

        /// <summary>
        /// Татаж авсан файлын төрөл:
        ///   "zip" (анхдагч, эсвэл URL нь ".zip"-ээр төгсдөг бол автоматаар
        ///       танигдана) — татсан zip-ийг Drivers/ фолдерт шууд задална
        ///       (zip дотор аль хэдийн InfPath/DriverInstallerPath-тай
        ///       тохирох фолдерийн бүтэцтэй байх ёстой, ж: "SPRT_POS58/driver.exe").
        ///   "file" — татсан файлыг шууд InfPath/DriverInstallerPath-ийн
        ///       заасан газарт нэг ганц файл болгож хадгална (өөрөө .exe
        ///       эсвэл .inf файл шууд байх тохиолдолд).
        /// </summary>
        [JsonPropertyName("driverPackageType")]
        public string DriverPackageType { get; set; } = "zip";

        /// <summary>Суулгагч нь silent (/S, /quiet гэх мэт) горимыг дэмждэг эсэх</summary>
        [JsonPropertyName("supportsSilentInstall")]
        public bool SupportsSilentInstall { get; set; } = true;

        /// <summary>
        /// Суулгасны дараа энэ принтерийг Windows-ийн үндсэн (default) принтер
        /// болгож тохируулах эсэх.
        /// </summary>
        [JsonPropertyName("setAsDefault")]
        public bool SetAsDefault { get; set; } = false;

        /// <summary>Тайлбар / тэмдэглэл (заавал биш)</summary>
        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;

        // ===== ШИНЭ: Хэвлэгчийн төрөл (баримт / шошго) =====

        /// <summary>
        /// Хэвлэгчийн төрөл: "receipt" (анхдагч, ESC/POS баримтын хэвлэгч)
        /// эсвэл "label" (TSPL хэлээр ажилладаг шошго хэвлэгч, жишээ нь
        /// 4BARCODE, TSC, Xprinter шошго загварууд). Энэ утгаас хамаараад
        /// TestPrintService нь ESC/POS эсвэл TSPL командыг сонгож ашиглана.
        /// </summary>
        [JsonPropertyName("usbPrintHardwareIdPrefix")]
        public string UsbPrintHardwareIdPrefix { get; set; } = string.Empty;
        [JsonPropertyName("productNameContains")]
        public string? ProductNameContains { get; set; }
        [JsonPropertyName("printerType")]
        public string PrinterType { get; set; } = "receipt";

        /// <summary>Шошгоны өргөн (мм), зөвхөн PrinterType = "label" үед хэрэглэгдэнэ</summary>
        [JsonPropertyName("labelWidthMm")]
        public double LabelWidthMm { get; set; } = 40;

        /// <summary>Шошгоны өндөр (мм), зөвхөн PrinterType = "label" үед хэрэглэгдэнэ</summary>
        [JsonPropertyName("labelHeightMm")]
        public double LabelHeightMm { get; set; } = 30;

        /// <summary>Шошгон хоорондын зай/gap (мм), зөвхөн PrinterType = "label" үед хэрэглэгдэнэ</summary>
        [JsonPropertyName("labelGapMm")]
        public double LabelGapMm { get; set; } = 2;

        // ===== GUI Wizard суулгагч (silent параметргүй) автоматжуулах тохиргоо =====
        // Зарим driver суулгагч (жишээ нь Xprinter-ийн "Printer Driver
        // Installation Wizard" / drvinst.exe) баримтгүй, silent параметргүй,
        // зөвхөн товч дараад л явдаг GUI wizard байдаг. Ийм үед
        // "autoDriveWizard": true болгож доорх талбаруудыг бөглөвөл,
        // WizardAutomationService нь UI Automation-оор хэрэглэгчийн оронд
        // товч/сонголтуудыг автоматаар дараад дуусгана.

        /// <summary>
        /// Driver суулгагч нь silent параметргүй GUI wizard бөгөөд
        /// UI Automation-оор автоматаар удирдах эсэх. false (анхдагч) бол
        /// зөвхөн installArguments-ээр л ажиллуулна (энгийн silent install).
        /// </summary>
        [JsonPropertyName("autoDriveWizard")]
        public bool AutoDriveWizard { get; set; } = false;

        /// <summary>
        /// Wizard цонхны гарчиг (эсвэл дэд мөр) — UI Automation-оор цонхыг
        /// олоход ашиглана. Процессын PID-ээс илүү найдвартай, учир нь
        /// зарим installer өөрийгөө elevated горимд дахин эхлүүлдэг тул анхны
        /// PID нь бодит харагдаж буй цонхтой таарахгүй байж болно.
        /// </summary>
        [JsonPropertyName("wizardWindowTitle")]
        public string WizardWindowTitle { get; set; } = "Printer Driver Installation Wizard";

        /// <summary>
        /// Wizard-ийн эхний дэлгэц дэх "Install printer driver" радио
        /// товчны текст (ихэвчлэн анхдагчаар сонгогдсон байдаг).
        /// </summary>
        [JsonPropertyName("wizardInstallOptionLabel")]
        public string WizardInstallOptionLabel { get; set; } = "Install printer driver";

        /// <summary>
        /// Wizard-ийн 2-р дэлгэц дэх "Port Type" радио товчны текст
        /// (жишээ нь "USB"). Хоосон бол энэ алхмыг алгасна.
        /// </summary>
        [JsonPropertyName("wizardPortTypeLabel")]
        public string WizardPortTypeLabel { get; set; } = string.Empty;

        /// <summary>
        /// Wizard-ийн "Printer Model" combobox-д сонгох утга,
        /// ж: "POSPrinter POS-58". Хоосон бол энэ алхмыг алгасна.
        /// </summary>
        [JsonPropertyName("wizardModelName")]
        public string WizardModelName { get; set; } = string.Empty;

        /// <summary>
        /// 2-р дэлгэц дэх зүүн талын Port жагсаалтаас (Port / Description
        /// баганатай ListView, ж: "USB007 — UnknownPrinter") хамгийн
        /// сүүлийн мөрийг автоматаар сонгох эсэх. Хэрэглэгч гараар яг л
        /// ингэж хийдэг тохиолдолд (шинээр холбогдсон хэвлэгч ямагт
        /// жагсаалтын хамгийн доод мөрөнд гардаг үед) true болго.
        /// </summary>
        [JsonPropertyName("wizardSelectLastPort")]
        public bool WizardSelectLastPort { get; set; } = false;

        /// <summary>Wizard-ийн "Next" товчны текст (installer бүрээс өөр байж болно)</summary>
        [JsonPropertyName("wizardNextButtonLabel")]
        public string WizardNextButtonLabel { get; set; } = "Next";

        /// <summary>Wizard-ийн эцсийн "Finish" товчны текст</summary>
        [JsonPropertyName("wizardFinishButtonLabel")]
        public string WizardFinishButtonLabel { get; set; } = "Finish";

        /// <summary>
        /// Зарим wizard (жишээ нь энэ POS-58-Series) "Next" биш шууд
        /// "Install" товчоор (лиценз зөвшөөрсний дараа идэвхждэг) цааш
        /// (Installing... үе шат руу) явдаг. Ийм тохиолдолд энд тухайн
        /// товчны яг нэрийг зааж өгнө -- үгүй бол код зөвхөн Next/Finish-г
        /// л хайх тул "Install" товч хэзээ ч дарагдахгүй, wizard хугацааны
        /// хязгаарт хүрч зогсоно.
        /// </summary>
        [JsonPropertyName("wizardInstallButtonLabel")]
        public string? WizardInstallButtonLabel { get; set; }

        /// <summary>
        /// Лиценз зөвшөөрлийн checkbox-ийн нэр (жишээ нь "I accept the
        /// terms of the License Agreement"). Тухайн wizard-д checkbox
        /// байхгүй бол хоосон/null орхиж болно — энэ тохиолдолд алгасна.
        /// </summary>
        [JsonPropertyName("wizardLicenseCheckboxLabel")]
        public string? WizardLicenseCheckboxLabel { get; set; }

        /// <summary>
        /// Зарим wizard (жишээ нь HOIN/POS-58-Series drvinst) лиценз
        /// зөвшөөрлийг checkbox-оор БИШ, харин хоёр RadioButton-оор
        /// ("I have read and accept the license terms" / "I do not accept
        /// the license terms") асуудаг. Ийм үед checkbox талбар биш, энэ
        /// талбарыг ашиглана — эндэх текст нь ЗӨВШӨӨРӨХ (accept) radio
        /// button-ийн яг нэр байх ёстой. Сонгосны дараа л Next товч
        /// идэвхжинэ (өмнө нь Disabled байдаг) тул үүнийг сонгохгүй бол
        /// wizard автоматаар цааш явж чадахгүй. Хоосон бол алгасна.
        /// </summary>
        [JsonPropertyName("wizardLicenseAcceptRadioLabel")]
        public string? WizardLicenseAcceptRadioLabel { get; set; }

        // ===== "Setup - Select Language" урьдчилсан цонх (заавал биш) =====
        // Зарим суулгагч (ялангуяа Inno Setup-оор бүтээгдсэн) үндсэн Wizard
        // цонх нээгдэхээс ӨМНӨ тусдаа жижиг "Setup - Select Language" цонх
        // нээж, хэрэглэгчээс хэлээ сонгоод "OK" дарахыг хүсдэг. Энэ цонхыг
        // ч бас гараар хүрэлгүйгээр автоматаар дарж өнгөрөхийн тулд доорх
        // талбаруудыг ашиглана.

        /// <summary>
        /// "Select Language" цонхны гарчиг (эсвэл дэд мөр). Хоосон бол энэ
        /// алхмыг бүхэлд нь алгасна (өөрөөр хэлбэл тухайн суулгагч ийм цонх
        /// огт харуулдаггүй гэж үзнэ). Жишээ нь "Select Language".
        /// </summary>
        [JsonPropertyName("wizardLanguageDialogTitle")]
        public string WizardLanguageDialogTitle { get; set; } = string.Empty;

        /// <summary>
        /// ComboBox-с сонгох хэлний нэр, жишээ нь "English". Хоосон бол
        /// ComboBox-ийг хэвээр нь (анхдагч утгаар нь) орхиод шууд OK дарна.
        /// </summary>
        [JsonPropertyName("wizardLanguageValue")]
        public string WizardLanguageValue { get; set; } = string.Empty;

        /// <summary>"Select Language" цонхны "OK" товчны текст.</summary>
        [JsonPropertyName("wizardLanguageOkButtonLabel")]
        public string WizardLanguageOkButtonLabel { get; set; } = "OK";

        // ===== ШИНЭ: Ерөнхий дүрэг-суурьтай (rule-based) wizard automation =====
        // Дээрх бүх "wizard*" flat талбарууд (WizardInstallOptionLabel,
        // WizardLicenseAcceptRadioLabel, WizardNextButtonLabel гэх мэт) хуучин
        // (backward-compat) горимд ашиглагдана. Хэрэв доорх WizardSteps
        // (JSON-ийн "wizardSteps") бөглөгдсөн бол WizardAutomationService нь
        // ЗӨВХӨН үүнийг ашиглана -- аль нэртэй цонх дээр яг юу дарахыг бүхэлд нь
        // энд, printers.json дотор дараалсан дүрэм болгож тодорхойлно. Жишээг
        // Database/printers.json-оос үзнэ үү.

        /// <summary>
        /// Дэлгэц бүрийн дүрмийн жагсаалт. Хоосон/null бол хуучин flat
        /// талбаруудаас автоматаар эквивалент нэг ерөнхий дүрэм үүсгэнэ.
        /// </summary>

        [JsonPropertyName("wizardSteps")]
        public List<WizardStep>? WizardSteps { get; set; }
    }
}