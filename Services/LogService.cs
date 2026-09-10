using System;
using System.IO;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// Программын бүх үйл явдал (info, warning, error)-ыг Logs/ фолдер дотор
    /// өдөр өдрөөр нь тусад нь текст файлд бичдэг энгийн logger.
    /// Ямар нэг алдаа гарвал энэ log файлаас яг юу болсныг олж харах боломжтой.
    /// </summary>
    public sealed class LogService
    {
        private static readonly Lazy<LogService> _instance = new(() => new LogService());
        public static LogService Instance => _instance.Value;

        private readonly string _logDirectory;
        private readonly object _lock = new();

        private LogService()
        {
            _logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(_logDirectory);
        }

        private string CurrentLogFile => Path.Combine(_logDirectory, $"install_{DateTime.Now:yyyyMMdd}.log");

        public void WriteInfo(string message) => Write("INFO", message);
        public void WriteWarning(string message) => Write("WARN", message);

        public void WriteError(string message, Exception? ex = null)
        {
            var full = ex == null ? message : $"{message} | Exception: {ex}";
            Write("ERROR", full);
        }

        private void Write(string level, string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            lock (_lock)
            {
                try
                {
                    File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
                }
                catch
                {
                    // Log бичихэд алдаа гарвал программыг зогсоохгүй, зүгээр алгасна.
                }
            }
        }
    }
}
