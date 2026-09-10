# POS Хэвлэгч суулгагч (POSPrinterInstaller)

Windows 10/11 дээр ажилладаг, USB POS хэвлэгчийн driver-ийг автоматаар
таньж суулгадаг WPF (.NET 8) десктоп программ.

## Шаардлагатай зүйлс

- Windows 10 эсвэл Windows 11 (x64)
- .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0) эсвэл
  Visual Studio 2022 (17.8+) "Windows-ийн desktop-ийн .NET хөгжүүлэлт" ачаалалттай

## Фолдерийн бүтэц

```
POSPrinterInstaller/
├── POSPrinterInstaller.sln
├── POSPrinterInstaller.csproj
├── app.manifest                 (Администраторын эрхээр ажиллуулах тохиргоо)
├── App.xaml / App.xaml.cs       (Программын эхлэл, гэнэтийн алдаа барих)
├── MainWindow.xaml / .cs        (Үндсэн цонх - Монгол UI)
├── Models/
│   ├── PrinterModel.cs          (printers.json-ийн нэг мөрийн бүтэц)
│   └── DetectedPrinter.cs       (USB-ээр олдсон төхөөрөмжийн мэдээлэл)
├── Services/
│   ├── LogService.cs            (Logs/ фолдерт log бичих)
│   ├── USBDetectionService.cs   (USB VID/PID уншина - WMI)
│   ├── PrinterDatabaseService.cs (printers.json ачаална, тааруулна)
│   ├── DriverInstallService.cs  (driver.exe-г силент ажиллуулна)
│   ├── PrinterService.cs        (Windows принтерийн жагсаалт шалгах, default тохируулах)
│   ├── RawPrinterHelper.cs      (ESC/POS raw байт шууд spooler-т явуулах Win32 API)
│   ├── TestPrintService.cs      (Тест хуудасны ESC/POS байт үүсгэж хэвлэнэ)
│   └── WizardAutomationService.cs (Silent параметргүй GUI Wizard installer-ийг UI Automation-оор автоматаар дараад дуусгана)
├── Database/
│   └── printers.json            (Дэмжигдэх принтерийн загваруудын өгөгдлийн сан)
├── Drivers/
│   ├── XPrinter/                 (Xprinter-ийн drvinst.exe-г энд байршуулна)
│   ├── SPRT_POS58/               (driver.exe-г энд байршуулна)
│   ├── Sewoo_B20/
│   └── Godex/
└── Logs/                        (Ажиллах явцын лог автоматаар энд бичигдэнэ, wizard-debug.log ч энд байна)
```

## Хэрхэн ажиллуулах вэ

### Visual Studio-гоор

1. `POSPrinterInstaller.sln`-ийг Visual Studio 2022-оор нээнэ.
2. Build Configuration-г `x64` болгоно.
3. `F5` дарж ажиллуулна (эсвэл Ctrl+F5 - debug-гүйгээр).
4. Программ Администраторын эрх асуух тул "Тийм" дарна уу (driver суулгахад
   администратор эрх шаардлагатай тул app.manifest-д requireAdministrator
   гэж тохируулсан байгаа).

### Командын мөрөөр (dotnet CLI)

```powershell
cd POSPrinterInstaller
dotnet build -c Release
dotnet bin\Release\net8.0-windows\POSPrinterInstaller.exe
```

Эсвэл шууд:

```powershell
dotnet run
```

### Тараах (production) хувилбар үүсгэх

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Ингэснээр `bin\Release\net8.0-windows\win-x64\publish\` дотор ганц .exe
файл (POSPrinterInstaller.exe) үүснэ. Үүнийг Drivers/, Database/ фолдеруудын
хамт хэрэглэгчийн компьютер руу хуулж өгвөл болно.

## Шинэ принтерийн загвар нэмэх (эх код өөрчлөхгүйгээр)

1. `Drivers/` дотор шинэ фолдер үүсгээд (жишээ нь `Drivers/MyPrinter/`)
   дотор нь driver-ийн суулгагч (`driver.exe` эсвэл `.msi`) файлыг
   байршуулна.

2. `Database/printers.json`-д доорх шиг шинэ бичлэг нэмнэ:

```json
{
  "vid": "0483",
  "pid": "5743",
  "manufacturer": "MyBrand",
  "modelName": "MyBrand Printer X1",
  "windowsPrinterName": "MyBrand Printer X1",
  "driverInstallerPath": "MyPrinter\\driver.exe",
  "installArguments": "/S",
  "supportsSilentInstall": true,
  "setAsDefault": false,
  "notes": "Тайлбар"
}
```

- **vid / pid**: Хэвлэгчийг Windows-ийн Device Manager дотор нээгээд
  Properties → Details → "Hardware Ids" эсвэл "Device instance path"-аас
  `VID_xxxx&PID_xxxx` хэлбэрээр харж болно.
- **driverInstallerPath**: `Drivers/` фолдероос эхэлсэн харьцангуй зам.
- **installArguments**: Таны driver суулгагчийн жинхэнэ silent параметр
  (жишээ нь Inno Setup суулгагч бол `/VERYSILENT /SUPPRESSMSGBOXES`,
  NSIS бол `/S`, MSI бол зохих `msiexec` параметр гэх мэт).
- **windowsPrinterName**: Driver суулгагдсаны дараа Windows-ийн
  "Printers & scanners" жагсаалтад харагдах яг нэр. Энэ нэрээр
  программ баталгаажуулалт хийдэг тул зөв бичих ёстой.

3. Программыг дахин ажиллуулаад "Дахин шалгах" дарахад л шинэ загвар
   автоматаар танигдана — эх кодыг дахин compile хийх шаардлагагүй.

### Гуравдагч талын driver.exe-г огт ажиллуулахгүй суулгах (INF + pnputil арга)

Хэрэв та гуравдагч талын `driver.exe`/`drvinst.exe`-г шууд ажиллуулахыг
хүсэхгүй байвал (жишээ нь тухайн installer нь тодорхойгүй эх сурвалжтай,
эсвэл зөвхөн Windows-ийн байгалийн хэрэгслээр л суулгах шаардлагатай бол),
`installMethod: "inf"` тохиргоог ашиглана. Энэ горим нь гуравдагч талын
exe-г огт ажиллуулахгүйгээр, Windows-д байнга байдаг `pnputil.exe`
хэрэгслээр `.inf` driver package-ийг шууд Driver Store-д staging хийж
суулгадаг тул хамгийн найдвартай, script-оор бүрэн автоматжуулах
боломжтой арга юм.

```json
{
  "vid": "0483",
  "pid": "5743",
  "manufacturer": "MyBrand",
  "modelName": "MyBrand Printer X1",
  "windowsPrinterName": "MyBrand Printer X1",
  "installMethod": "inf",
  "infPath": "MyPrinter_INF\\mybrand.inf",
  "setAsDefault": false,
  "notes": "INF аргаар суулгана, driver.exe шаардлагагүй."
}
```

- `installMethod`: `"inf"` гэж тохируулбал `driverInstallerPath`/
  `installArguments`/`autoDriveWizard`-ийг тооцохгүй, зөвхөн `infPath`-ийг
  ашиглана.
- `infPath`: `Drivers/` фолдероос эхэлсэн харьцангуй зам бөгөөд `.inf`
  файлыг заана. Тухайн `.inf`-тэй ижил фолдерт харгалзах `.cat`, `.sys`
  зэрэг туслах файлууд бас байрлах ёстой (эдгээрийг ихэвчлэн
  үйлдвэрлэгчийн driver package дотроос олно).

**`.inf` файлаа хаанаас олох вэ:**

1. Үйлдвэрлэгчийн албан ёсны сайтаас "driver package" (заримдаа `.zip`
   хэлбэрээр, exe wrapper-гүй) байгаа эсэхийг эхлээд шалгах — хамгийн
   найдвартай эх сурвалж.
2. Байхгүй бол одоо байгаа `driver.exe`/`drvinst.exe`-г 7-Zip зэрэг
   архиваторуудаар (ихэнх NSIS/Inno Setup installer нь энгийн архив
   байдаг тул) задалж, дотор нь `.inf`, `.cat`, `.sys` файлуудыг хайж
   олно.
3. Олсон файлуудаа бүгдийг нь (INF-тэй хамт байх ёстой бусад файлын
   хамт) `Drivers/<загвар>_INF/` шинэ фолдерт хуулна.

**Программ дотор pnputil-ийг яг юу ажиллуулдаг вэ:**

```
pnputil.exe /add-driver "Drivers\MyPrinter_INF\mybrand.inf" /install
```

Энэ команд нь UAC (Администратор эрх) асууна — driver staging хийхэд
энэ шаардлагатай, гуравдагч талын installer.exe шаардсантай адилхан
шалтгаанаар (Windows driver суулгах бүрд Администратор эрх шаарддаг).
Гарцын код `0` эсвэл `3010` (амжилттай, дахин ачаалах шаардлагатай) бол
амжилттай гэж үзнэ.

**Анхаарах:** зарим driver (WHQL гэрчилгээгүй, гарын үсэггүй) Windows
дефолт тохиргоогоор татгалзагдана. Ийм тохиолдолд эсвэл үйлдвэрлэгчээс
signed хувилбар авах, эсвэл Windows-ийн Test Signing горим ашиглах
шаардлагатай болно — сүүлийнх нь production машин дээр зөвлөгддөггүй.

### Silent параметргүй GUI Wizard суулгагч бол (жишээ нь Xprinter drvinst.exe)

Зарим driver суулгагч (жишээ нь Xprinter-ийн "Printer Driver
Installation Wizard") баримтгүй, silent параметргүй, зөвхөн товч дараад
л явдаг GUI wizard байдаг. Ийм үед `autoDriveWizard: true` тохиргоог
ашиглана — программ `WizardAutomationService`-ээр UI Automation ашиглаж,
хэрэглэгчийн оронд Wizard-ийн товч, сонголтуудыг автоматаар дараад
дуусгадаг:

```json
{
  "vid": "0483",
  "pid": "070B",
  "manufacturer": "XPrinter",
  "modelName": "XP-58 (POSPrinter POS-58)",
  "windowsPrinterName": "POSPrinter POS-58",
  "driverInstallerPath": "XPrinter\\drvinst.exe",
  "installArguments": "",
  "supportsSilentInstall": false,
  "autoDriveWizard": true,
  "wizardWindowTitle": "Printer Driver Installation Wizard",
  "wizardInstallOptionLabel": "Install printer driver",
  "wizardPortTypeLabel": "USB",
  "wizardModelName": "POSPrinter POS-58",
  "wizardSelectLastPort": true,
  "wizardNextButtonLabel": "Next",
  "wizardFinishButtonLabel": "Finish"
}
```

- `autoDriveWizard` — GUI wizard-ийг UI Automation-оор автоматаар
  дараад дуусгах эсэх. `true` бол `installArguments`/
  `supportsSilentInstall`-ийг тооцохгүй, wizard эхэндээ харагдах горимоор
  нээгдэж, эхний Next товч амжилттай дарагдсаны дараа өөрөө нуугдана.
- `wizardWindowTitle` — Wizard цонхны гарчиг (эсвэл дэд мөр), цонхыг
  олоход ашиглана.
- `wizardInstallOptionLabel` — эхний дэлгэц дэх "Install printer
  driver" радио товчны текст (ихэвчлэн анхдагчаар сонгогдсон байдаг).
- `wizardPortTypeLabel` — 2-р дэлгэц дэх Port Type радио товчны текст
  (жишээ нь "USB"). Хоосон бол алгасна.
- `wizardModelName` — Printer Model combobox-д сонгох утга. Хоосон бол
  алгасна.
- `wizardSelectLastPort` — 2-р дэлгэц дэх зүүн талын Port жагсаалтаас
  (Port / Description баганатай ListView, ж: "USB007 — UnknownPrinter")
  хамгийн сүүлийн мөрийг автоматаар сонгоно. Хэрэглэгч гараар яг л
  ингэж сонгодог тохиолдолд (шинээр холбогдсон хэвлэгч ямагт жагсаалтын
  хамгийн доод мөрөнд гардаг үед) `true` болго.
- `wizardNextButtonLabel` / `wizardFinishButtonLabel` — товчнуудын
  текст, installer бүрээс өөр байж болно.
- `wizardLicenseCheckboxLabel` — лиценз зөвшөөрлийн checkbox байвал,
  тухайн checkbox-ийн нэр (заавал биш).

**Анхаарах:** энэ бол "best-effort" автоматжуулалт — installer-ийн UI
хувилбар өөрчлөгдвөл (жишээ нь товчны текст өөрчлөгдвөл) тохирох
элемент олдохгүй байж болно. Ийм үед тухайн алхмыг чимээгүй алгасаад,
Wizard цонх дахин харагдана — апп гацахгүй, хэрэглэгч өөрөө гараар
үргэлжлүүлж болно. Диагностик мэдээллийг `Logs/wizard-debug.log`
файлаас харна уу.

## Алдаа гарвал

- `Logs/install_YYYYMMDD.log` файлаас дэлгэрэнгүй мэдээллийг үзнэ үү.
- Хэрвээ driver суулгагдсан ч "Windows-ийн жагсаалтад олдсонгүй" гэсэн
  алдаа гарвал, `windowsPrinterName` утга Windows дээр бодитоор
  харагдаж буй принтерийн нэртэй яг таарч байгаа эсэхийг шалгана уу
  (Control Panel → Devices and Printers).
- Тест хэвлэлт амжилтгүй болвол, кабель холболт, принтерийн USB port,
  цахилгаан хангамжийг шалгана уу.

## Аюулгүй байдлын тэмдэглэл

- Driver суулгах, принтер бүртгэх зэрэг үйлдлүүд Windows дээр
  Администраторын эрх шаарддаг тул программ автоматаар UAC prompt
  харуулна. Энэ бол хэвийн үзэгдэл.
- Гуравдагч талын driver.exe файлуудыг зөвхөн үйлдвэрлэгчийн албан ёсны
  эх сурвалжаас татаж ашиглахыг зөвлөж байна.
