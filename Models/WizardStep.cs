using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace POSPrinterInstaller.Models
{
    /// <summary>
    /// printers.json дэх "wizardSteps" массивын нэг элемент. Нэг "дэлгэц"-ийг
    /// (window title + шаардлагатай бол дотор нь заавал байх control-оор)
    /// тодорхойлж, тухайн дэлгэц дээр дараалан хийх үйлдлүүдийг (Actions)
    /// агуулна.
    ///
    /// Жишээ (printers.json дотор):
    /// {
    ///   "windowTitle": "Printer Driver Installation Wizard",
    ///   "requireControlText": "I have read and accept",
    ///   "requireControlType": "RadioButton",
    ///   "actions": [
    ///     { "type": "selectRadio", "target": "Install printer driver" },
    ///     { "type": "selectRadio", "target": "I have read and accept the license terms" },
    ///     { "type": "clickButton", "target": "Next" }
    ///   ]
    /// }
    /// </summary>
    public class WizardStep
    {
        /// <summary>
        /// Энэ дүрэм аль цонх дээр хэрэглэгдэхийг заана (агуулгаар нь
        /// хайна, Contains). Хоосон/null бол PrinterModel.WizardWindowTitle-
        /// тэй адилхан (өөрөөр хэлбэл үндсэн wizard цонх) гэж үзнэ.
        /// Хэрэв "Select Language" мэтийн тусдаа диалог цонхыг зорьж байгаа
        /// бол энд тухайн цонхны гарчгийг бичнэ (WizardLanguageDialogTitle-
        /// тэй ижил байж болно).
        /// </summary>
        [JsonPropertyName("windowTitle")]
        public string? WindowTitle { get; set; }

        /// <summary>
        /// Ижил гарчигтай (жишээ нь бүх алхамд "Setup Wizard" гэсэн нэг л
        /// гарчигтай) олон дэлгэцийг ЯЛГАХ зорилготой: энэ текст (агуулгаар)
        /// агуулсан control тухайн цонхонд байгаа тохиолдолд ГАНЦ энэ дүрэм
        /// тохирно гэж үзнэ. Хоосон бол зөвхөн WindowTitle-ээр тааруулна
        /// (болгоомжтой байх ёстой - олон дэлгэц зэрэг тохирч болно).
        /// </summary>
        [JsonPropertyName("requireControlText")]
        public string? RequireControlText { get; set; }

        /// <summary>
        /// RequireControlText-ийн control төрөл: "Button", "RadioButton",
        /// "CheckBox", "ComboBox", "Text" гэх мэт. Хоосон бол "Button" гэж
        /// үзнэ. (System.Windows.Automation.ControlType-ийн нэртэй тохирно.)
        /// </summary>
        [JsonPropertyName("requireControlType")]
        public string? RequireControlType { get; set; }

        /// <summary>Энэ дэлгэц дээр дараалан хийгдэх үйлдлүүд.</summary>
        [JsonPropertyName("actions")]
        public List<WizardAction> Actions { get; set; } = new();

        /// <summary>
        /// true бол энэ дүрэм НЭГ л удаа ажиллаад дараа нь дахиж тааруулагдахгүй
        /// (жишээ нь Finish дараад дахин Finish хайхгүй байх). Ихэнх тохиолдолд
        /// шаардлагагүй, учир нь дэлгэц өөрчлөгдмөгц дараагийн дүрэм рүү шилждэг,
        /// гэхдээ "Install" мэт товч хэдэн секунд Disabled хэвээр байвал давхар
        /// дарагдахаас сэргийлэхэд хэрэг болно.
        /// </summary>
        [JsonPropertyName("stopAfter")]
        public bool StopAfter { get; set; } = false;
    }

    /// <summary>Нэг тодорхой үйлдэл: товч дарах, radio/checkbox сонгох, combo утга тохируулах г.м.</summary>
    public class WizardAction
    {
        /// <summary>
        /// "clickButton" | "selectRadio" | "checkCheckbox" | "setCombo" |
        /// "selectLastListItem" | "wait"
        ///
        /// "selectLastListItem": ListView/жагсаалтын доторх ХАМГИЙН СҮҮЛИЙН
        /// (доод) мөрийг сонгоно (жиш нь "Port" жагсаалтаас саяхан
        /// холбогдсон хэвлэгчийн порт ямагт хамгийн доор гардаг тохиолдолд
        /// ашиглана — хуучин "wizardSelectLastPort" талбарын оронд). Target
        /// талбарт (заавал биш) тухайн жагсаалт control-ын нэрийг өгвөл
        /// зөвхөн ТЭР жагсаалт дотроос хайна; хоосон бол бүх цонхны
        /// хүрээнд хайна.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "clickButton";

        /// <summary>Товч/radio/checkbox/combo-ийн нэр (Label). "wait" төрөлд шаардлагагүй.</summary>
        [JsonPropertyName("target")]
        public string? Target { get; set; }

        /// <summary>"setCombo"-д сонгох утга.</summary>
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        /// <summary>Тухайн element-ийг хайх дээд хугацаа (мс). Анхдагч 3000.</summary>
        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; set; } = 3000;

        /// <summary>Үйлдэл амжилттай/амжилтгүй хийгдсэний дараа хүлээх хугацаа (мс). Анхдагч 300.</summary>
        [JsonPropertyName("waitAfterMs")]
        public int WaitAfterMs { get; set; } = 300;

        /// <summary>
        /// false бол энэ үйлдэл амжилтгүй болоход (жишээ нь заасан target
        /// олдоогүй) тухайн Step-ийн үлдсэн үйлдлүүдийг ЗОГСООНО (дараагийн
        /// мөчлөгт дахин оролдоно). true (анхдагч) бол алдаанаас үл хамааран
        /// дараагийн үйлдэл рүү шилжинэ.
        /// </summary>
        [JsonPropertyName("continueOnFailure")]
        public bool ContinueOnFailure { get; set; } = true;

        /// <summary>"wait" төрлийн үйлдэлд хүлээх хугацаа (мс).</summary>
        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; } = 500;
    }
}
