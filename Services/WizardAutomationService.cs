using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using POSPrinterInstaller.Models;

namespace POSPrinterInstaller.Services
{
    /// <summary>
    /// System.Windows.Automation (UI Automation) ашиглаж, баримтгүй
    /// (undocumented CLI) GUI installer-ийн Wizard цонхыг хэрэглэгчийн
    /// оронд автоматаар удирдана.
    ///
    /// ЭНЭ ХУВИЛБАРЫН ГОЛ ӨӨРЧЛӨЛТ: "аль нэртэй цонх дээр юу дарах вэ" гэдгийг
    /// C# кодонд ХАТУУ БИЧИХИЙН оронд, printers.json дэх "wizardSteps"
    /// массиваас уншиж, ерөнхий дүрмийн систем (rule engine)-ээр гүйцэтгэдэг
    /// болгосон. Жишээ нь:
    ///
    /// "wizardSteps": [
    ///   {
    ///     "windowTitle": "Select Language",
    ///     "actions": [
    ///       { "type": "setCombo", "target": "", "value": "English" },
    ///       { "type": "clickButton", "target": "OK" }
    ///     ]
    ///   },
    ///   {
    ///     "windowTitle": "Printer Driver Installation Wizard",
    ///     "requireControlText": "I have read and accept",
    ///     "requireControlType": "RadioButton",
    ///     "actions": [
    ///       { "type": "selectRadio", "target": "Install printer driver" },
    ///       { "type": "selectRadio", "target": "I have read and accept the license terms" },
    ///       { "type": "clickButton", "target": "Next" }
    ///     ]
    ///   },
    ///   {
    ///     "windowTitle": "Printer Driver Installation Wizard",
    ///     "requireControlText": "Install",
    ///     "requireControlType": "Button",
    ///     "actions": [
    ///       { "type": "clickButton", "target": "Install" }
    ///     ],
    ///     "stopAfter": true
    ///   },
    ///   {
    ///     "windowTitle": "Printer Driver Installation Wizard",
    ///     "requireControlText": "Finish",
    ///     "requireControlType": "Button",
    ///     "actions": [
    ///       { "type": "clickButton", "target": "Finish" }
    ///     ],
    ///     "stopAfter": true
    ///   }
    /// ]
    ///
    /// Алгоритм: цонх бүр дэлгэц солигдох бүрд, дээрх жагсаалтаас ЭХНИЙ
    /// тохирох дүрмийг (WindowTitle таарч, RequireControlText/Type байвал
    /// тухайн control тухайн дэлгэц дээр байгаа бол) олж, дотор нь заасан
    /// Actions-ийг дараалан гүйцэтгэнэ. Дараа нь дэлгэц шинэчлэгдэх/
    /// солигдохыг хүлээгээд, дахин эхнээс тохирох дүрмийг хайна.
    ///
    /// АНХААР (Backward-compat): хэрэв PrinterModel.WizardSteps хоосон
    /// (тохируулаагүй) бол хуучин "flat" талбаруудаар (WizardNextButtonLabel,
    /// WizardFinishButtonLabel, WizardInstallButtonLabel,
    /// WizardLicenseAcceptRadioLabel гэх мэт) ажилладаг хялбаршуулсан
    /// анхдагч дүрмийг автоматаар үүсгэж ашиглана -- өмнөх printers.json
    /// бичлэгүүд өөрчлөлтгүйгээр ажиллах хэвээр байна.
    /// </summary>
    public class WizardAutomationService
    {
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint BM_CLICK = 0x00F5;

        private static readonly TimeSpan WindowWaitTimeout = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(3);

        private static readonly string DebugLogPath =
            Path.Combine(AppContext.BaseDirectory, "Logs", "wizard-debug.log");

        /// <summary>
        /// Шинэ автоматжуулалт эхлэхийн ӨМНӨ дуудна: өмнөх (гацсан/дуусаагүй)
        /// оролдлогоос үлдсэн, ЯГ ИЖИЛ гарчигтай wizard цонхнуудыг хайж олоод
        /// хүчээр хаана (эзэмшигч процессыг нь Kill хийнэ).
        /// </summary>
        public static void CloseStaleWizardWindows(string? titleContains)
        {
            if (string.IsNullOrWhiteSpace(titleContains)) return;

            try
            {
                var windowCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);
                var topWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, windowCondition);

                foreach (AutomationElement window in topWindows)
                {
                    string name;
                    try { name = window.Current.Name ?? string.Empty; }
                    catch (Exception) { continue; }

                    if (string.IsNullOrEmpty(name) ||
                        !name.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
                        if (hwnd == IntPtr.Zero) continue;

                        GetWindowThreadProcessId(hwnd, out var pid);
                        if (pid == 0) continue;

                        WriteDebugLog($"Хуучин/гацсан wizard цонх ('{name}', PID={pid}) олдлоо -- цэвэрлэж хаана.");

                        using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                    catch (Exception ex)
                    {
                        WriteDebugLog($"Хуучин wizard цонх хаах үед алдаа: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDebugLog($"Хуучин wizard цонх хайх/цэвэрлэх үед алдаа гарлаа (үл хэрэгссэн): {ex.Message}");
            }
        }

        /// <summary>
        /// Wizard-ийг эхлүүлж, printer.WizardSteps (эсвэл тэдгээр байхгүй бол
        /// хуучин flat талбаруудаас үүсгэсэн анхдагч дүрмүүд)-ийг ашиглан
        /// дэлгэц бүрийг дараалан гаталж, дуустал (эсвэл гацсан тохиолдолд
        /// гараар үргэлжлүүлэхээр цонхыг харагдуулж) ажиллана.
        /// </summary>
        public Task<bool> AutomateAsync(int processId, PrinterModel printer, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                IntPtr windowHandle = IntPtr.Zero;
                using var loggerCts = new CancellationTokenSource();
                Task? screenLoggerTask = null;

                try
                {
                    WriteDebugLog($"=== {printer.Manufacturer} {printer.ModelName} автоматжуулалт эхэллээ (PID={processId}) ===");

                    var steps = BuildEffectiveSteps(printer);
                    LogStepsSummary(steps);

                    // ===== "Select Language" мэтийн тусдаа (заавал биш) диалогууд =====
                    // Эдгээр нь үндсэн wizard цонхтой ӨӨР гарчигтай тул үндсэн
                    // цонхыг хайхаас өмнө богино хугацаанд шалгаж, тохирох дүрэм
                    // байвал гүйцэтгэнэ.
                    RunStandaloneDialogSteps(printer, steps, onStatus);

                    var mainWindow = WaitForWindowByTitle(printer.WizardWindowTitle, WindowWaitTimeout, cancellationToken)
                                      ?? WaitForWindowByProcessId(processId, TimeSpan.FromSeconds(3), cancellationToken);

                    if (mainWindow is null)
                    {
                        onStatus?.Invoke("Суулгагчийн цонх олдсонгүй.");
                        WriteDebugLog("Цонх олдсонгүй (title ба PID хайлт хоёул амжилтгүй).");
                        return false;
                    }

                    windowHandle = new IntPtr(mainWindow.Current.NativeWindowHandle);
                    WriteDebugLog($"Цонх олдлоо: '{SafeGetName(mainWindow)}' (hwnd={windowHandle}).");

                    screenLoggerTask = StartScreenChangeLogger(mainWindow, loggerCts.Token);

                    var consumedOnceOnlySteps = new HashSet<WizardStep>();
                    var overallDeadline = DateTime.UtcNow + OverallTimeout;
                    var lastFingerprint = string.Empty;
                    var noMatchPollCount = 0;

                    while (DateTime.UtcNow < overallDeadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!IsWindowAlive(mainWindow))
                        {
                            // Зарим installer дэлгэц шилжихдээ хуучин цонхоо бүхэлд нь
                            // устгаад, ИЖИЛ гарчигтай шинэ HWND-тэй цонх дахин
                            // үүсгэдэг. Тиймээс шууд "дууслаа" гэж дүгнэлгүй, ижил
                            // гарчигтай шинэ цонх аль хэдийн гарч ирсэн эсэхийг
                            // богино хугацаанд дахин хайна.
                            var replacementWindow = WaitForWindowByTitle(printer.WizardWindowTitle, TimeSpan.FromSeconds(4), cancellationToken);

                            if (replacementWindow != null)
                            {
                                WriteDebugLog("Хуучин цонхны reference хүчингүй болсон ч, ижил гарчигтай шинэ цонх олдлоо -- үргэлжлүүлнэ.");
                                mainWindow = replacementWindow;
                                windowHandle = new IntPtr(mainWindow.Current.NativeWindowHandle);
                                screenLoggerTask = StartScreenChangeLogger(mainWindow, loggerCts.Token);
                                lastFingerprint = string.Empty;
                                Thread.Sleep(300);
                                continue;
                            }

                            onStatus?.Invoke("Суулгагч дуусаж, цонх хаагдлаа.");
                            WriteDebugLog("Цонх хаагдсан, ижил гарчигтай шинэ цонх ч олдсонгүй -- wizard амжилттай дууссан гэж үзнэ.");
                            return true;
                        }

                        if (IsUacPrompt(mainWindow))
                        {
                            onStatus?.Invoke("Windows-ийн зөвшөөрлийн (UAC) цонх гарлаа -- гараар зөвшөөрнө үү.");
                            WriteDebugLog("UAC prompt илэрлээ.");
                            RevealWindowForManualCompletion(windowHandle, onStatus);
                            return false;
                        }

                        // Одоогийн дэлгэц өмнөхтэй адилхан хэвээр байвал (control-уудын
                        // fingerprint өөрчлөгдөөгүй бол) яг сая гүйцэтгэсэн үйлдэл
                        // нөлөө үзүүлээгүй байж болзошгүй тул дахин ижил дүрмийг шууд
                        // давтахгүйгээр бага зэрэг хүлээнэ (endless-click-loop-оос сэргийлнэ).
                        var fingerprint = ComputeControlsFingerprint(mainWindow);
                        var screenChanged = fingerprint != lastFingerprint;
                        lastFingerprint = fingerprint;

                        var matchedStep = FindMatchingStep(mainWindow, printer, steps, consumedOnceOnlySteps);

                        if (matchedStep != null)
                        {
                            noMatchPollCount = 0;
                            WriteDebugLog($"Тохирсон дүрэм: цонх='{matchedStep.WindowTitle ?? printer.WizardWindowTitle}', requireControl='{matchedStep.RequireControlText}'.");

                            if (matchedStep.StopAfter)
                            {
                                consumedOnceOnlySteps.Add(matchedStep);
                            }

                            var windowClosedDuringActions = ExecuteActions(mainWindow, matchedStep.Actions, onStatus);

                            if (windowClosedDuringActions)
                            {
                                onStatus?.Invoke("Суулгагч дуусаж, цонх хаагдлаа.");
                                WriteDebugLog("Actions гүйцэтгэх явцад цонх хаагдсан -- амжилттай дууссан гэж үзнэ.");
                                return true;
                            }

                            Thread.Sleep(300);
                            continue;
                        }

                        // Ямар ч дүрэм тохирсонгүй -- энэ бол "Installing..." прогресс
                        // мэт завсрын дэлгэц байж болзошгүй тул шууд бууж өгөхгүй,
                        // богино зогсоод дахин шалгана. Хэт удаан (жишээ нь ~20 секунд)
                        // тохирох дүрэм олдохгүй бол диагностик лог бичээд үргэлжилнэ.
                        noMatchPollCount++;
                        if (noMatchPollCount == 1 || noMatchPollCount % 60 == 0)
                        {
                            WriteDebugLog(screenChanged
                                ? "Одоогийн дэлгэцэд тохирох дүрэм алга -- диагностик:"
                                : "Одоогийн дэлгэцэд тохирох дүрэм алга (дэлгэц өөрчлөгдөөгүй) -- диагностик:");
                            DumpControls(mainWindow);
                        }

                        Thread.Sleep(300);
                    }

                    if (IsWindowAlive(mainWindow))
                    {
                        WriteDebugLog("Ерөнхий хугацааны хязгаарт (3 мин) хүрлээ -- гараар үргэлжлүүлэхээр цонхыг харагдууллаа.");
                        DumpControls(mainWindow);
                        RevealWindowForManualCompletion(windowHandle, onStatus);
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    onStatus?.Invoke($"Автоматжуулалт зогслоо: {ex.Message}");
                    WriteDebugLog($"АЛДАА: {ex}");
                    if (windowHandle != IntPtr.Zero)
                    {
                        ShowWindow(windowHandle, SW_SHOW);
                    }
                    return false;
                }
                finally
                {
                    loggerCts.Cancel();
                }
            }, cancellationToken);
        }

        // ===================================================================
        //  Дүрмийн систем (rule engine)
        // ===================================================================

        /// <summary>
        /// printer.WizardSteps байвал шууд буцаана. Хоосон бол хуучин flat
        /// талбаруудаас (WizardInstallOptionLabel, WizardLicenseAcceptRadioLabel,
        /// WizardLicenseCheckboxLabel, WizardNextButtonLabel,
        /// WizardInstallButtonLabel, WizardFinishButtonLabel) эквивалент
        /// анхдагч дүрмийн жагсаалтыг үүсгэж буцаана (backward-compat).
        /// </summary>
        private static List<WizardStep> BuildEffectiveSteps(PrinterModel printer)
        {
            if (printer.WizardSteps != null && printer.WizardSteps.Count > 0)
            {
                return printer.WizardSteps;
            }

            WriteDebugLog("printers.json дотор 'wizardSteps' тодорхойлогдоогүй -- хуучин flat талбаруудаас анхдагч дүрэм үүсгэж байна.");

            var legacyActions = new List<WizardAction>();

            if (!string.IsNullOrWhiteSpace(printer.WizardInstallOptionLabel))
            {
                legacyActions.Add(new WizardAction { Type = "selectRadio", Target = printer.WizardInstallOptionLabel, ContinueOnFailure = true });
            }

            if (!string.IsNullOrWhiteSpace(printer.WizardLicenseCheckboxLabel))
            {
                legacyActions.Add(new WizardAction { Type = "checkCheckbox", Target = printer.WizardLicenseCheckboxLabel, ContinueOnFailure = true });
            }

            if (!string.IsNullOrWhiteSpace(printer.WizardLicenseAcceptRadioLabel))
            {
                legacyActions.Add(new WizardAction { Type = "selectRadio", Target = printer.WizardLicenseAcceptRadioLabel, ContinueOnFailure = true });
            }

            if (!string.IsNullOrWhiteSpace(printer.WizardInstallButtonLabel))
            {
                legacyActions.Add(new WizardAction { Type = "clickButton", Target = printer.WizardInstallButtonLabel, ContinueOnFailure = true, TimeoutMs = 800 });
            }

            if (!string.IsNullOrWhiteSpace(printer.WizardFinishButtonLabel))
            {
                legacyActions.Add(new WizardAction { Type = "clickButton", Target = printer.WizardFinishButtonLabel, ContinueOnFailure = true, TimeoutMs = 800 });
            }

            if (!string.IsNullOrWhiteSpace(printer.WizardNextButtonLabel))
            {
                legacyActions.Add(new WizardAction { Type = "clickButton", Target = printer.WizardNextButtonLabel, ContinueOnFailure = true, TimeoutMs = 800 });
            }

            // Нэг л ерөнхий дүрэм: аль ч дэлгэц дээр байсан "боломжит бүх
            // үйлдлийг" оролдож, олдсоныг нь хийгээд өнгөрнө (хуучин код яг
            // ийм зарчмаар ажилладаг байсан).
            return new List<WizardStep>
            {
                new WizardStep
                {
                    WindowTitle = null, // үндсэн wizard цонх
                    Actions = legacyActions
                }
            };
        }

        /// <summary>
        /// WindowTitle нь printer.WizardWindowTitle-ээс ӨӨР (жишээ нь
        /// "Select Language") дүрмүүдийг, үндсэн wizard цонхыг хайхаас ӨМНӨ,
        /// тухайн цонх бодитоор гарч ирсэн эсэхийг богино хугацаанд шалгаж
        /// гүйцэтгэнэ. Ийм цонх олдоогүй бол чимээгүй өнгөрнө.
        /// </summary>
        private static void RunStandaloneDialogSteps(PrinterModel printer, List<WizardStep> steps, Action<string>? onStatus)
        {
            var standaloneSteps = steps.Where(s =>
                    !string.IsNullOrWhiteSpace(s.WindowTitle) &&
                    !(printer.WizardWindowTitle ?? string.Empty).Contains(s.WindowTitle!, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var step in standaloneSteps)
            {
                var dialog = WaitForWindowByTitle(step.WindowTitle, TimeSpan.FromSeconds(6), CancellationToken.None);
                if (dialog is null)
                {
                    WriteDebugLog($"'{step.WindowTitle}' цонх олдсонгүй -- энэ суулгагч дээр байхгүй байх, эсвэл аль хэдийн хаагдсан гэж үзээд алгасав.");
                    continue;
                }

                WriteDebugLog($"'{SafeGetName(dialog)}' цонх олдлоо -- дүрмийн actions-ийг гүйцэтгэж байна.");
                onStatus?.Invoke($"'{step.WindowTitle}' цонх олдлоо -- автоматаар бөглөж байна...");
                DumpControls(dialog);

                ExecuteActions(dialog, step.Actions, onStatus);
                Thread.Sleep(400);
            }
        }

        /// <summary>
        /// Одоогийн цонхны төлөвт (control-ууд) хамгийн эхэнд тохирох
        /// WizardStep-ийг олно. StopAfter=true бөгөөд аль хэдийн нэг удаа
        /// гүйцэтгэгдсэн дүрмийг дахин тохируулахгүй.
        /// </summary>
        private static WizardStep? FindMatchingStep(
            AutomationElement window,
            PrinterModel printer,
            List<WizardStep> steps,
            HashSet<WizardStep> consumedOnceOnlySteps)
        {
            var windowName = SafeGetName(window);

            bool TitleMatches(WizardStep step)
            {
                var expectedTitle = string.IsNullOrWhiteSpace(step.WindowTitle) ? printer.WizardWindowTitle : step.WindowTitle;
                return string.IsNullOrWhiteSpace(expectedTitle) ||
                       windowName.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase);
            }

            // 1-р шат: ТОДОРХОЙ (requireControlText заасан) дүрмүүдийг эхэлж
            // шалгана. Учир нь эдгээр нь тухайн дэлгэцийг ЯЛГААТАЙ таньдаг тул
            // (жишээ нь "лиценз radio байгаа эсэх") хамгийн найдвартай. Хэрэв
            // requireControlText-гүй "ерөнхий" дүрэм (жишээ нь filter-гүй
            // "Next дар") жагсаалтад эрт байрласан ч, лиценз/Install/Finish
            // мэт тодорхой дэлгэц дээр байгаа үед ТЭР ерөнхий дүрмийг БИШ,
            // харин яг тохирох тодорхой дүрмийг илүүд үзнэ -- ингэснээр "Next"
            // "I agree" сонгогдохоос өмнө хэт хурдан дарагдах алдаа гарахгүй.
            foreach (var step in steps)
            {
                if (consumedOnceOnlySteps.Contains(step)) continue;
                if (string.IsNullOrWhiteSpace(step.RequireControlText)) continue;
                if (!TitleMatches(step)) continue;

                var controlType = ParseControlType(step.RequireControlType);
                var found = FindByPartialNameNoWait(window, step.RequireControlText!, controlType);
                if (found != null) return step;
            }

            // 2-р шат: 1-р шатанд ямар ч тодорхой дүрэм тохироогүй бол л,
            // filter-гүй (requireControlText=null) ерөнхий дүрмүүдийг жагсаалтын
            // дарааллаар шалгана.
            foreach (var step in steps)
            {
                if (consumedOnceOnlySteps.Contains(step)) continue;
                if (!string.IsNullOrWhiteSpace(step.RequireControlText)) continue;
                if (!TitleMatches(step)) continue;

                return step;
            }

            return null;
        }

        /// <summary>
        /// Нэг Step-ийн Actions жагсаалтыг дараалан гүйцэтгэнэ.
        /// Буцаах утга: true бол Actions гүйцэтгэх явцад цонх ХААГДСАН
        /// (жишээ нь Finish дарсны нөлөөгөөр) гэсэн үг.
        /// </summary>
        private static bool ExecuteActions(AutomationElement window, List<WizardAction> actions, Action<string>? onStatus)
        {
            foreach (var action in actions)
            {
                if (!IsWindowAlive(window))
                {
                    return true;
                }

                bool ok;
                switch (action.Type?.Trim().ToLowerInvariant())
                {
                    case "clickbutton":
                        ok = TryClickButton(window, action.Target ?? string.Empty, TimeSpan.FromMilliseconds(action.TimeoutMs));
                        WriteDebugLog(ok ? $"[Action] clickButton '{action.Target}' -- амжилттай."
                                          : $"[Action] clickButton '{action.Target}' -- олдсонгүй/дарж чадсангүй.");
                        break;

                    case "selectradio":
                        ok = TrySelectRadioButton(window, action.Target, TimeSpan.FromMilliseconds(action.TimeoutMs));
                        WriteDebugLog(ok ? $"[Action] selectRadio '{action.Target}' -- амжилттай."
                                          : $"[Action] selectRadio '{action.Target}' -- олдсонгүй/сонгож чадсангүй.");
                        break;

                    case "checkcheckbox":
                        ok = TryCheckCheckBox(window, action.Target ?? string.Empty, TimeSpan.FromMilliseconds(action.TimeoutMs));
                        WriteDebugLog(ok ? $"[Action] checkCheckbox '{action.Target}' -- амжилттай."
                                          : $"[Action] checkCheckbox '{action.Target}' -- олдсонгүй/чагтлаж чадсангүй.");
                        break;

                    case "setcombo":
                        ok = TrySetComboBoxValue(window, action.Value ?? string.Empty, TimeSpan.FromMilliseconds(action.TimeoutMs));
                        WriteDebugLog(ok ? $"[Action] setCombo '{action.Value}' -- амжилттай."
                                          : $"[Action] setCombo '{action.Value}' -- олдсонгүй/тохируулж чадсангүй.");
                        break;

                    case "selectlastlistitem":
                        ok = TrySelectLastListItem(window, action.Target);
                        WriteDebugLog(ok ? $"[Action] selectLastListItem (container='{action.Target}') -- амжилттай."
                                          : $"[Action] selectLastListItem (container='{action.Target}') -- жагсаалт/мөр олдсонгүй.");
                        break;

                    case "wait":
                        Thread.Sleep(Math.Max(0, action.DurationMs));
                        ok = true;
                        break;

                    default:
                        WriteDebugLog($"[Action] Танигдаагүй action type: '{action.Type}' -- алгасав.");
                        ok = false;
                        break;
                }

                if (action.WaitAfterMs > 0)
                {
                    Thread.Sleep(action.WaitAfterMs);
                }

                if (!IsWindowAlive(window))
                {
                    return true;
                }

                if (!ok && !action.ContinueOnFailure)
                {
                    WriteDebugLog($"[Action] '{action.Type}' амжилтгүй бөгөөд ContinueOnFailure=false тул энэ Step-ийг зогсоов.");
                    break;
                }
            }

            return !IsWindowAlive(window);
        }

        private static ControlType ParseControlType(string? name) => (name?.Trim().ToLowerInvariant()) switch
        {
            "radiobutton" => ControlType.RadioButton,
            "checkbox" => ControlType.CheckBox,
            "combobox" => ControlType.ComboBox,
            "text" => ControlType.Text,
            "edit" => ControlType.Edit,
            _ => ControlType.Button,
        };

        private static void LogStepsSummary(List<WizardStep> steps)
        {
            WriteDebugLog($"Нийт {steps.Count} wizard дүрэм ачааллаа:");
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                WriteDebugLog($"  #{i + 1}: windowTitle='{s.WindowTitle ?? "(үндсэн цонх)"}' requireControlText='{s.RequireControlText}' actions={s.Actions.Count} stopAfter={s.StopAfter}");
            }
        }

        // ===================================================================
        //  Доорх туслах функцууд (UI Automation primitives) -- өмнөх
        //  хувилбартай ижил, зөвхөн ерөнхий engine-ээс дуудагдана.
        // ===================================================================

        private static void RevealWindowForManualCompletion(IntPtr windowHandle, Action<string>? onStatus)
        {
            if (windowHandle != IntPtr.Zero)
            {
                ShowWindow(windowHandle, SW_SHOW);
                SetForegroundWindow(windowHandle);
            }

            onStatus?.Invoke("Автомат алхам дуусав -- цонх дахин харагдаж байна, гараар үргэлжлүүлнэ үү.");
        }

        private static string ComputeControlsFingerprint(AutomationElement root)
        {
            try
            {
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                var sb = new System.Text.StringBuilder();
                foreach (AutomationElement el in all)
                {
                    try
                    {
                        var name = el.Current.Name;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        sb.Append(el.Current.ControlType.ProgrammaticName)
                          .Append('|').Append(name)
                          .Append('|').Append(el.Current.IsEnabled)
                          .Append(';');
                    }
                    catch (Exception) { }
                }
                return sb.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private Task StartScreenChangeLogger(AutomationElement mainWindow, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var lastFingerprint = string.Empty;
                var isFirst = true;

                while (!ct.IsCancellationRequested)
                {
                    if (!IsWindowAlive(mainWindow)) break;

                    var fingerprint = ComputeControlsFingerprint(mainWindow);
                    if (!string.IsNullOrEmpty(fingerprint) && fingerprint != lastFingerprint)
                    {
                        lastFingerprint = fingerprint;
                        WriteDebugLog(isFirst ? "[ТӨЛӨВ] Анхны дэлгэцийн төлөв:" : "[ТӨЛӨВ] Дэлгэц/товчны төлөв өөрчлөгдлөө -- шинэ төлөв:");
                        isFirst = false;
                        DumpControls(mainWindow);
                    }

                    Thread.Sleep(400);
                }
            }, ct);
        }

        private static bool IsWindowAlive(AutomationElement element)
        {
            try { _ = element.Current.Name; return true; }
            catch (ElementNotAvailableException) { return false; }
            catch (Exception) { return false; }
        }

        private static bool IsUacPrompt(AutomationElement window)
        {
            var name = SafeGetName(window);
            return name.Contains("User Account Control", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Хэрэглэгчийн бүртгэлийн хяналт", StringComparison.OrdinalIgnoreCase);
        }

        private static AutomationElement? WaitForWindowByTitle(string? titleContains, TimeSpan timeout, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(titleContains)) return null;

            var deadline = DateTime.UtcNow + timeout;
            var windowCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var topWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, windowCondition);
                    foreach (AutomationElement window in topWindows)
                    {
                        var name = SafeGetName(window);
                        if (!string.IsNullOrEmpty(name) && name.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                        {
                            return window;
                        }
                    }
                }
                catch (Exception) { }

                Thread.Sleep(PollInterval);
            }

            return null;
        }

        private static AutomationElement? WaitForWindowByProcessId(int processId, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
                if (window != null) return window;
                Thread.Sleep(PollInterval);
            }

            return null;
        }

        private static string SafeGetName(AutomationElement element)
        {
            try { return element.Current.Name ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        private static string NormalizeLabel(string label) => label.Replace("&", string.Empty).Trim();

        private static bool TrySelectRadioButton(AutomationElement root, string? label, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;

            var element = FindElementByExactName(root, label, ControlType.RadioButton, timeout ?? TimeSpan.FromSeconds(3))
                          ?? FindElementByPartialName(root, label, ControlType.RadioButton, TimeSpan.FromMilliseconds(800));

            if (element is null) return false;

            try
            {
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                {
                    ((SelectionItemPattern)pattern).Select();
                    Thread.Sleep(300);
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        private static bool TryCheckCheckBox(AutomationElement root, string label, TimeSpan timeout)
        {
            var element = FindElementByExactName(root, label, ControlType.CheckBox, timeout)
                          ?? FindElementByPartialName(root, label, ControlType.CheckBox, TimeSpan.FromMilliseconds(500));

            if (element is null) return false;

            try
            {
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern))
                {
                    var toggle = (TogglePattern)pattern;
                    if (toggle.Current.ToggleState != ToggleState.On)
                    {
                        toggle.Toggle();
                        Thread.Sleep(300);
                    }
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        private static bool TrySetComboBoxValue(AutomationElement root, string value, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var comboCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);
                    var combos = root.FindAll(TreeScope.Descendants, comboCondition);

                    foreach (AutomationElement combo in combos)
                    {
                        if (TrySetSingleComboBoxValue(combo, value)) return true;
                    }
                }
                catch (Exception) { }

                Thread.Sleep(PollInterval);
            }

            try
            {
                var comboCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);
                var combos = root.FindAll(TreeScope.Descendants, comboCondition);
                WriteDebugLog($"ComboBox утга '{value}' олдсонгүй. Нийт {combos.Count} ComboBox олдлоо:");
                var itemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                foreach (AutomationElement combo in combos)
                {
                    var comboName = SafeGetName(combo);
                    var items = combo.FindAll(TreeScope.Descendants, itemCondition);
                    var itemNames = new List<string>();
                    foreach (AutomationElement item in items) itemNames.Add(SafeGetName(item));
                    WriteDebugLog($"  ComboBox '{comboName}': [{string.Join(", ", itemNames)}]");
                }
            }
            catch (Exception) { }

            return false;
        }

        private static bool TrySetSingleComboBoxValue(AutomationElement combo, string value)
        {
            try
            {
                if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern))
                {
                    var expand = (ExpandCollapsePattern)expandPattern;
                    expand.Expand();
                    Thread.Sleep(250);

                    var exactCondition = new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.NameProperty, value));

                    var item = combo.FindFirst(TreeScope.Descendants, exactCondition);

                    if (item is null)
                    {
                        var listItemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                        var candidates = combo.FindAll(TreeScope.Descendants, listItemCondition);
                        foreach (AutomationElement candidate in candidates)
                        {
                            var name = SafeGetName(candidate);
                            if (name.Contains(value, StringComparison.OrdinalIgnoreCase))
                            {
                                item = candidate;
                                break;
                            }
                        }
                    }

                    if (item != null && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPattern))
                    {
                        ((SelectionItemPattern)selPattern).Select();
                        Thread.Sleep(500);
                        expand.Collapse();
                        return true;
                    }

                    expand.Collapse();
                    return false;
                }

                if (combo.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                {
                    ((ValuePattern)valuePattern).SetValue(value);
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        /// <summary>
        /// Жагсаалт (ListView гэх мэт)-ын дотор ХАМГИЙН СҮҮЛИЙН ListItem-ийг
        /// сонгоно. Хуучин "wizardSelectLastPort" талбарын ерөнхий (JSON-с
        /// удирдагддаг) хувилбар. containerName өгөгдсөн бол зөвхөн тэр
        /// нэртэй List/DataGrid control дотроос хайна; хоосон бол бүх
        /// цонхны хүрээнд ListItem-үүдийг хайж, СҮҮЛИЙНХ (control tree-ийн
        /// дараалалд сүүлд гарч ирсэн)-ийг сонгоно.
        /// </summary>
        private static bool TrySelectLastListItem(AutomationElement root, string? containerName)
        {
            try
            {
                AutomationElement searchRoot = root;

                if (!string.IsNullOrWhiteSpace(containerName))
                {
                    var container = FindByPartialNameNoWait(root, containerName, ControlType.List)
                                     ?? FindByPartialNameNoWait(root, containerName, ControlType.DataGrid)
                                     ?? FindByPartialNameNoWait(root, containerName, ControlType.Pane);
                    if (container != null) searchRoot = container;
                }

                var itemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                var items = searchRoot.FindAll(TreeScope.Descendants, itemCondition);
                if (items.Count == 0) return false;

                var lastItem = items[items.Count - 1];
                if (lastItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                {
                    ((SelectionItemPattern)pattern).Select();
                    Thread.Sleep(300);
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        private static AutomationElement? FindElementByExactName(AutomationElement root, string name, ControlType type, TimeSpan timeout)
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, type),
                new PropertyCondition(AutomationElement.NameProperty, name));

            return PollFind(root, condition, timeout);
        }

        private static AutomationElement? FindButtonExactNoWait(AutomationElement root, string name)
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name));

            try { return root.FindFirst(TreeScope.Descendants, condition); }
            catch (Exception) { return null; }
        }

        private static AutomationElement? FindElementByPartialName(AutomationElement root, string label, ControlType type, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                var found = FindByPartialNameNoWait(root, label, type);
                if (found != null) return found;
                Thread.Sleep(PollInterval);
            }

            return null;
        }

        private static AutomationElement? FindButtonPartialNoWait(AutomationElement root, string label) =>
            FindByPartialNameNoWait(root, label, ControlType.Button);

        private static AutomationElement? FindByPartialNameNoWait(AutomationElement root, string label, ControlType type)
        {
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
            var normalizedLabel = NormalizeLabel(label);

            try
            {
                var candidates = root.FindAll(TreeScope.Descendants, condition);
                foreach (AutomationElement el in candidates)
                {
                    var name = NormalizeLabel(SafeGetName(el));
                    if (name.Contains(normalizedLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        return el;
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        private static AutomationElement? PollFind(AutomationElement root, Condition condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                AutomationElement? found = null;
                try { found = root.FindFirst(TreeScope.Descendants, condition); }
                catch (Exception) { }

                if (found != null) return found;
                Thread.Sleep(PollInterval);
            }

            return null;
        }

        private static bool TryClickButton(AutomationElement root, string label, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;

            var element = FindButtonExactNoWait(root, label) ?? FindButtonPartialNoWait(root, label);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (element is not null)
                {
                    bool isEnabled;
                    try { isEnabled = element.Current.IsEnabled; }
                    catch (ElementNotAvailableException) { isEnabled = false; element = null; }

                    if (element is not null && isEnabled) break;
                    element = null;
                }

                Thread.Sleep(PollInterval);
                element = FindButtonExactNoWait(root, label) ?? FindButtonPartialNoWait(root, label);
            }

            if (element is null) return false;

            try { if (!element.Current.IsEnabled) return false; }
            catch (ElementNotAvailableException) { return false; }

            // АНХААР: өмнө нь энд 3 давхар оролдлого (InvokePattern → BM_CLICK →
            // хулганы клик симуляц) байсан ч, хэрэглэгчийн хүсэлтээр ГАНЦ л
            // удаа (зөвхөн InvokePattern-ээр) оролдож, бүтэлгүйтвэл шууд false
            // буцаадаг болгосон. Товч дахин дарагдах эсэх нь гаднах
            // poll/step-механизмаас (дараагийн мөчлөгт дахин FindMatchingStep
            // дуудагдана) хамаарна -- энд давхар аргаар "албадаж" дарахгүй.
            return TryInvokeAndVerify(root, element, () =>
            {
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern))
                {
                    ((InvokePattern)invokePattern).Invoke();
                    return true;
                }
                return false;
            });
        }

        private static bool TryInvokeAndVerify(AutomationElement root, AutomationElement element, Func<bool> action)
        {
            try { if (!action()) return false; }
            catch (Exception) { return false; }

            Thread.Sleep(400);

            if (!IsWindowAlive(root)) return true;

            try
            {
                _ = element.Current.IsOffscreen;
                return false;
            }
            catch (ElementNotAvailableException) { return true; }
            catch (Exception) { return false; }
        }

        private static void DumpControls(AutomationElement root)
        {
            try
            {
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                WriteDebugLog($"--- Диагностик: {all.Count} control олдлоо ---");
                foreach (AutomationElement el in all)
                {
                    string typeName;
                    string name;
                    bool enabled;
                    try
                    {
                        typeName = el.Current.ControlType.ProgrammaticName;
                        name = el.Current.Name;
                        enabled = el.Current.IsEnabled;
                    }
                    catch (Exception) { continue; }

                    if (string.IsNullOrWhiteSpace(name)) continue;
                    WriteDebugLog($"[{typeName}] '{name}' Enabled={enabled}");
                }
            }
            catch (Exception) { }
        }

        private static readonly object DebugLogLock = new();

        private static void WriteDebugLog(string line)
        {
            try
            {
                lock (DebugLogLock)
                {
                    var dir = Path.GetDirectoryName(DebugLogPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(DebugLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
                }
            }
            catch (Exception) { }
        }
    }
}
