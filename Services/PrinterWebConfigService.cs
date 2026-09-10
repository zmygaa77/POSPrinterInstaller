using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace POSPrinterInstaller.Services
{
    public class PrinterWebConfigResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Зарим POS/шошго хэвлэгч (Sewoo гэх мэт) нь built-in энгийн HTML веб
    /// сервертэй байдаг бөгөөд тэндээс IP/Subnet/Gateway-г HTML маягт
    /// (form)-аар тохируулдаг ("IP Info" гэсэн жижиг хуудас, доор нь
    /// "Send" товчтой). Энэ сервис нь тэр хуудсыг HttpClient-ээр уншиж,
    /// маягтын бүтцийг (action, method, талбаруудын нэр/утга) задалж аваад,
    /// зөвхөн IP талбарыг шинэ утгаар СОЛИЖ, бусад талбарыг (Subnet,
    /// Gateway гэх мэт) өөрчлөлгүйгээр яг тэр хэвлэгчийн формыг бөглөж
    /// "Send" товч дарсантай ЯГ АДИЛХАН HTTP хүсэлтийг автоматаар илгээнэ.
    ///
    /// Энэ нь бодит браузерын UI автоматжуулалт биш (Selenium гэх мэт биш),
    /// харин "Send" товч дарахад браузер яг л ямар HTTP хүсэлт илгээдэг
    /// вэ гэдгийг өөрөө үүсгэж, шууд илгээдэг арга — үр дүнгийн хувьд
    /// адилхан ч, илүү хурдан бөгөөд найдвартай.
    /// </summary>
    public class PrinterWebConfigService
    {
        private static readonly Regex FormOpenRegex = new(@"<form\b([^>]*)>", RegexOptions.IgnoreCase);
        private static readonly Regex InputRegex = new(@"<input\b([^>]*)/?>", RegexOptions.IgnoreCase);
        private static readonly Regex AttrRegex = new(@"([a-zA-Z_][\w\-]*)\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        private static readonly Regex BooleanAttrRegex = new(@"\b(readonly|disabled)\b(?!\s*=)", RegexOptions.IgnoreCase);

        /// <summary>
        /// printerIp дээрх хэвлэгчийн веб тохиргооны хуудсыг (жишээ нь
        /// "IP Info" маягттай) уншиж, доторх IP талбарыг newIp утгаар
        /// солиод, маягтыг "Send" дарсантай адилхан аргаар илгээнэ.
        /// </summary>
        public async Task<PrinterWebConfigResult> UpdatePrinterIpAsync(string printerIp, string newIp)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var baseUrl = $"http://{printerIp}/";

                LogService.Instance.WriteInfo($"Хэвлэгчийн веб тохиргооны хуудсыг уншиж байна: {baseUrl}");
                var html = await http.GetStringAsync(baseUrl);

                var formOpenMatch = FormOpenRegex.Match(html);
                if (!formOpenMatch.Success)
                {
                    return Fail("Хэвлэгчийн тохиргооны хуудаснаас <form> элемент олдсонгүй.");
                }

                var formAttrs = ParseAttributes(formOpenMatch.Groups[1].Value);
                var action = formAttrs.GetValueOrDefault("action", string.Empty);
                var method = formAttrs.GetValueOrDefault("method", "get").Trim().ToLowerInvariant();

                var actionUrl = string.IsNullOrWhiteSpace(action)
                    ? baseUrl
                    : new Uri(new Uri(baseUrl), action).ToString();

                var afterFormOpen = html.Substring(formOpenMatch.Index + formOpenMatch.Length);
                var closeIdx = afterFormOpen.IndexOf("</form>", StringComparison.OrdinalIgnoreCase);
                var formBody = closeIdx >= 0 ? afterFormOpen.Substring(0, closeIdx) : afterFormOpen;

                var fields = new Dictionary<string, string>();
                var orderedEditableTextFields = new List<string>();
                string? ipFieldName = null;

                foreach (Match inputMatch in InputRegex.Matches(formBody))
                {
                    var rawTag = inputMatch.Groups[1].Value;
                    var attrs = ParseAttributes(rawTag);
                    if (!attrs.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var type = attrs.GetValueOrDefault("type", "text").Trim().ToLowerInvariant();

                    // Checkbox (жишээ нь "DHCP") бол — static IP тохируулах гэж
                    // байгаа тул чеклэгдээгүй гэж үзэж, маягтад огт оруулахгүй
                    // орхино (HTML стандартын дагуу unchecked checkbox нь
                    // submit хийгдэхгүй).
                    if (type == "checkbox" || type == "submit" || type == "button" || type == "reset")
                    {
                        continue;
                    }

                    fields[name] = attrs.GetValueOrDefault("value", string.Empty);

                    // readonly/disabled нь ихэвчлэн утгагүй, ганцаараа бичигддэг
                    // тул (жишээ нь <input ... readonly>) тусад нь шалгана.
                    bool isReadOnly = attrs.ContainsKey("readonly") || attrs.ContainsKey("disabled")
                        || BooleanAttrRegex.IsMatch(rawTag);

                    // Port, MAC гэх мэт readonly/disabled талбарууд (зурган дээрх
                    // саарал өнгөтэй мөрүүд) бол засварлах боломжгүй тул
                    // IP-ийн нэр дэвшигч болгож авахгүй.
                    if (!isReadOnly && (type == "text" || !attrs.ContainsKey("type")))
                    {
                        orderedEditableTextFields.Add(name);
                    }

                    var lowerName = name.ToLowerInvariant();
                    if (ipFieldName == null && (lowerName == "i" || lowerName == "ip" || lowerName.Contains("ipaddr")))
                    {
                        ipFieldName = name;
                    }
                }

                // Нэр дээр үндэслэн олж чадаагүй бол — маягт дахь ЭХНИЙ
                // засварлагдах (readonly биш) text талбарыг IP гэж үзнэ.
                // Ихэнх энэ төрлийн embedded веб хуудсанд IP хамгийн эхэнд
                // байрладаг (жишээ нь IP → Subnet → Gateway → Port(readonly)
                // → MAC(readonly) дараалалтай).
                if (ipFieldName == null && orderedEditableTextFields.Count > 0)
                {
                    ipFieldName = orderedEditableTextFields[0];
                    LogService.Instance.WriteInfo(
                        $"IP талбарыг нэрээр нь олж чадаагүй тул маягт дахь ЭХНИЙ засварлагдах талбарыг " +
                        $"('{ipFieldName}') IP гэж үзэж байна.");
                }

                if (ipFieldName == null)
                {
                    return Fail("Маягт дотроос IP оруулах талбарыг автоматаар олж чадсангүй.");
                }

                // ЗӨВХӨН IP талбарыг л шинэ утгаар солино — бусад талбар
                // (Subnet, Gateway г.м) хэвлэгчийн хуудсан дээр байсан
                // хэвээрээ дамжина.
                fields[ipFieldName] = newIp;

                LogService.Instance.WriteInfo(
                    $"Маягтыг илгээж байна: {method.ToUpperInvariant()} {actionUrl} " +
                    $"(талбар '{ipFieldName}' = '{newIp}')");

                HttpResponseMessage response;
                if (method == "post")
                {
                    using var content = new FormUrlEncodedContent(fields);
                    response = await http.PostAsync(actionUrl, content);
                }
                else
                {
                    var query = string.Join("&", fields.Select(kv =>
                        $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                    var separator = actionUrl.Contains('?') ? "&" : "?";
                    response = await http.GetAsync($"{actionUrl}{separator}{query}");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Fail($"Хэвлэгч алдаа буцаалаа (HTTP {(int)response.StatusCode}).");
                }

                LogService.Instance.WriteInfo(
                    $"Хэвлэгчийн IP-г '{printerIp}' -> '{newIp}' болгож маягтаар амжилттай илгээлээ.");

                return new PrinterWebConfigResult
                {
                    Success = true,
                    Message = $"IP-г '{newIp}' болгож амжилттай илгээлээ. Хэвлэгч одоо дахин ачаалж болзошгүй."
                };
            }
            catch (TaskCanceledException)
            {
                return Fail("Хэвлэгчийн веб тохиргооны хуудас хугацаанд нь хариу өгсөнгүй (timeout).");
            }
            catch (HttpRequestException ex)
            {
                return Fail($"Хэвлэгчийн веб хуудас руу холбогдож чадсангүй: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogService.Instance.WriteError("Хэвлэгчийн веб тохиргоог автоматаар солих явцад алдаа гарлаа", ex);
                return Fail($"Гэнэтийн алдаа гарлаа: {ex.Message}");
            }
        }

        private static PrinterWebConfigResult Fail(string message)
        {
            LogService.Instance.WriteWarning($"Хэвлэгчийн IP-г веб маягтаар автоматаар солиход амжилтгүй боллоо: {message}");
            return new PrinterWebConfigResult { Success = false, Message = message };
        }

        private static Dictionary<string, string> ParseAttributes(string attrsRaw)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in AttrRegex.Matches(attrsRaw))
            {
                dict[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return dict;
        }
    }
}