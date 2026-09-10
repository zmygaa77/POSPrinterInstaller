namespace POSPrinterInstaller.Models
{
    /// <summary>
    /// USB сканнердах явцад олдсон, бодит холбогдсон төхөөрөмжийн мэдээлэл.
    /// MatchedModel != null бол энэ төхөөрөмж бидний printers.json дахь
    /// нэг загвартай тохирсон гэсэн үг.
    /// </summary>
    public class DetectedPrinter
    {
        public string Vid { get; set; } = string.Empty;
        public string Pid { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string HardwareId { get; set; } = string.Empty;
        public string PnpDeviceId { get; set; } = string.Empty;

        /// <summary>printers.json-с олдсон тохирох загвар (олдоогүй бол null)</summary>
        public PrinterModel? MatchedModel { get; set; }

        public bool IsRecognized => MatchedModel != null;

        /// <summary>ComboBox/ListBox-д харагдах текст</summary>
        public string DisplayName => IsRecognized
            ? MatchedModel!.ModelName
            : $"{DeviceName} (Тодорхойгүй загвар - VID:{Vid} PID:{Pid})";

        public override string ToString() => DisplayName;
    }
}
