using System;
using System.IO;
using System.Text;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Driver амжилттай суулгагдсаны дараа, тухайн принтер бодитоор
    /// хэвлэж чадаж байгаа эсэхийг шалгах зорилгоор тест хуудас/шошго
    /// илгээдэг сервис. ESC/POS (баримт хэвлэгч) болон TSPL (шошго
    /// хэвлэгч) хоёуланг дэмждэг.
    /// </summary>
    public class TestPrintService
    {
        // ---------------------------------------------------------------
        // ESC/POS - баримт (receipt) хэвлэгчид зориулсан тест (өмнөх код)
        // ---------------------------------------------------------------

        /// <summary>
        /// ESC/POS стандарт тушаал ашиглан "POS Printer Test" гэсэн
        /// энгийн баримт бичгийн байтуудыг үүсгэнэ.
        /// </summary>
        private byte[] BuildTestReceipt(string printerModelName)
        {
            using var ms = new MemoryStream();

            // ESC @ -> принтерийг анхны төлөвт шинэчлэх (initialize)
            ms.Write(new byte[] { 0x1B, 0x40 });

            void WriteLine(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text + "\n");
                ms.Write(bytes, 0, bytes.Length);
            }

            // ESC a 1 -> төвд эгнүүлэх (center align)
            ms.Write(new byte[] { 0x1B, 0x61, 0x01 });
            WriteLine("--------------------------------");
            WriteLine("POS Printer Test");
            WriteLine("--------------------------------");

            // ESC a 0 -> зүүн эгнүүлэх (left align)
            ms.Write(new byte[] { 0x1B, 0x61, 0x00 });
            WriteLine("");
            WriteLine($"Printer: {printerModelName}");
            WriteLine("Driver: Installed successfully");
            WriteLine("Connection: OK");
            WriteLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteLine("");
            WriteLine("--------------------------------");
            WriteLine("");
            WriteLine("");
            WriteLine("");

            // GS V 1 -> цаас таслах (partial cut), зарим принтер дэмждэггүй
            // тул алдаа гарвал энэ мөрийг устгаж болно.
            ms.Write(new byte[] { 0x1D, 0x56, 0x01 });

            return ms.ToArray();
        }

        // ---------------------------------------------------------------
        // TSPL - шошго (label) хэвлэгчид зориулсан тест (шинээр нэмэгдсэн)
        // ---------------------------------------------------------------

        /// <summary>
        /// TSPL (Sewoo/TSC төрлийн шошго хэвлэгчийн стандарт) тушаал
        /// ашиглан тест шошгоны байтуудыг үүсгэнэ.
        /// widthMm/heightMm/gapMm нь printers.json дахь тухайн загварын
        /// labelWidthMm/labelHeightMm/labelGapMm утгуудаас ирнэ (өгөгдөөгүй
        /// бол 40x30мм, 2мм gap-ыг анхдагчаар ашиглана).
        /// </summary>
        private byte[] BuildTestLabel(string printerModelName, double widthMm = 40, double heightMm = 30, double gapMm = 2)
        {
            var sb = new StringBuilder();

            // Шошгоны хэмжээ (mm)
            sb.Append($"SIZE {widthMm} mm,{heightMm} mm\r\n");
            // Gap sensor хэрэглэх зайг тохируулна (mm)
            sb.Append($"GAP {gapMm} mm,0 mm\r\n");
            // Хэвлэх чиглэл
            sb.Append("DIRECTION 1\r\n");
            // Өмнөх зургийг цэвэрлэх
            sb.Append("CLS\r\n");

            // Текст хэвлэх: TEXT x,y,"font",rotation,x-mult,y-mult,"content"
            sb.Append("TEXT 20,20,\"3\",0,1,1,\"POS Printer Test\"\r\n");
            sb.Append($"TEXT 20,60,\"2\",0,1,1,\"Printer: {printerModelName}\"\r\n");
            sb.Append("TEXT 20,90,\"2\",0,1,1,\"Driver: Installed OK\"\r\n");
            sb.Append($"TEXT 20,120,\"2\",0,1,1,\"Date: {DateTime.Now:yyyy-MM-dd HH:mm}\"\r\n");

            // Баркод жишээ нэмж болно (сонголтоор):
            // sb.Append("BARCODE 20,150,\"128\",50,1,0,2,2,\"TEST12345\"\r\n");

            // Нэг шошго хэвлэ
            sb.Append("PRINT 1,1\r\n");

            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        // ---------------------------------------------------------------
        // Нийтлэг дуудлагын функцууд
        // ---------------------------------------------------------------

        /// <summary>
        /// Өмнөх (ESC/POS баримт) тест хэвлэлт — хуучин код хэвээр,
        /// backward compatibility-д зориулж үлдээсэн.
        /// </summary>
        public bool PrintTestPage(string windowsPrinterName, string modelName)
        {
            try
            {
                var receiptBytes = BuildTestReceipt(modelName);
                var success = RawPrinterHelper.SendBytesToPrinter(windowsPrinterName, receiptBytes, "POS Printer Installer - Test Print");

                if (success)
                {
                    LogService.Instance.WriteInfo($"'{windowsPrinterName}' рүү тест хуудас амжилттай илгээгдлээ.");
                }
                else
                {
                    LogService.Instance.WriteWarning($"'{windowsPrinterName}' рүү тест хуудас илгээхэд амжилтгүй боллоо.");
                }

                return success;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Тест хэвлэлт хийх явцад алдаа гарлаа", ex);
                return false;
            }
        }

        /// <summary>
        /// Шинэ, ерөнхий overload — принтерийн төрлөөр (баримт эсвэл шошго)
        /// зохих тушаалыг сонгож илгээнэ, мөн UI-ийн явцын жагсаалт руу
        /// progress callback дамжуулна.
        /// </summary>
        /// <param name="windowsPrinterName">Windows дээр бүртгэгдсэн принтерийн нэр</param>
        /// <param name="modelName">Принтерийн загварын нэр (тест хуудсанд харагдана)</param>
        /// <param name="isLabelPrinter">true бол TSPL шошго тест, false бол ESC/POS баримт тест</param>
        /// <param name="progress">
        /// Суулгах явцын жагсаалт руу мессеж илгээх callback.
        /// success: null = яваад байна, true = амжилттай, false = амжилтгүй
        /// </param>
        /// <param name="labelWidthMm">Шошгоны өргөн (мм), зөвхөн isLabelPrinter=true үед хэрэглэгдэнэ</param>
        /// <param name="labelHeightMm">Шошгоны өндөр (мм), зөвхөн isLabelPrinter=true үед хэрэглэгдэнэ</param>
        /// <param name="labelGapMm">Шошгон хоорондын зай (мм), зөвхөн isLabelPrinter=true үед хэрэглэгдэнэ</param>
        public bool PrintTestPage(string windowsPrinterName, string modelName, bool isLabelPrinter, Action<string, bool?> progress = null,
            double labelWidthMm = 40, double labelHeightMm = 30, double labelGapMm = 2)
        {
            progress?.Invoke("Хэвлэгч тестэлж байна...", null);

            try
            {
                var bytes = isLabelPrinter
                    ? BuildTestLabel(modelName, labelWidthMm, labelHeightMm, labelGapMm)
                    : BuildTestReceipt(modelName);

                var jobName = isLabelPrinter
                    ? "POS Printer Installer - Test Label"
                    : "POS Printer Installer - Test Print";

                var success = RawPrinterHelper.SendBytesToPrinter(windowsPrinterName, bytes, jobName);

                if (success)
                {
                    LogService.Instance.WriteInfo($"'{windowsPrinterName}' рүү тест {(isLabelPrinter ? "шошго" : "хуудас")} амжилттай илгээгдлээ.");
                    progress?.Invoke("Тест хэвлэлт амжилттай боллоо", true);
                }
                else
                {
                    LogService.Instance.WriteWarning($"'{windowsPrinterName}' рүү тест {(isLabelPrinter ? "шошго" : "хуудас")} илгээхэд амжилтгүй боллоо.");
                    progress?.Invoke("Тест хэвлэлт амжилтгүй боллоо", false);
                }

                return success;
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Тест хэвлэлт хийх явцад алдаа гарлаа", ex);
                progress?.Invoke("Тест хэвлэлт амжилтгүй боллоо (алдаа)", false);
                return false;
            }
        }
    }
}