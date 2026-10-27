using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;

namespace HaierHonDesktop
{
    public class HonCloudClient
    {
        private const string AUTH_API = "https://account2.hon-smarthome.com";
        private const string API_URL = "https://api-iot.he.services";
        private const string API_KEY = "GRCqFhC6Gk@ikWXm1RmnSmX1cm,MxY-configuration";
        private const string CLIENT_ID = "3MVG9QDx8IX8nP5T2Ha8ofvlmjLZl5L_gvfbT9.HJvpHGKoAS_dcMN8LYpTSYeVFCraUnV.2Ag1Ki7m4znVO6";
        private const string APP_VERSION = "2.6.5";
        private const int OS_VERSION = 999;
        private const string OS_TYPE = "android";
        private const string DEVICE_MODEL = "HonDesktopWin";
        private const string USER_AGENT = "Chrome/999.999.999.999";

        private readonly JavaScriptSerializer _json;
        private readonly object _lock = new object();
        private CookieContainer _cookies = new CookieContainer();
        private Thread _backgroundDiscoveryThread;
        private volatile bool _running = true;

        public bool IsCloudConnected { get; private set; }
        public string CurrentEmail { get; private set; }
        public string LastError { get; private set; }
        public string SelectedMac { get; set; }
        public string UiLang { get; private set; }
        public string UiTheme { get; private set; }

        private string _accessToken = "";
        private string _refreshToken = "";
        private string _idToken = "";
        private string _cognitoToken = "";
        private string _mobileId = "HonDesktop_" + Environment.MachineName;
        private DateTime _tokenExpiresUtc = DateTime.MinValue;

        private List<Dictionary<string, object>> _appliances = new List<Dictionary<string, object>>();
        private Dictionary<string, Dictionary<string, object>> _parametersByMac = new Dictionary<string, Dictionary<string, object>>();
        private Dictionary<string, Dictionary<string, object>> _commandSchemaByMac = new Dictionary<string, Dictionary<string, object>>();
        private Dictionary<string, Dictionary<string, bool>> _capabilitiesByMac = new Dictionary<string, Dictionary<string, bool>>();
        private Dictionary<string, Dictionary<string, object>> _modelProfileByMac = new Dictionary<string, Dictionary<string, object>>();
        private Dictionary<string, Dictionary<string, string>> _userStickyParamsByMac = new Dictionary<string, Dictionary<string, string>>();
        private Dictionary<string, DateTime> _lastCommandSentUtcByMac = new Dictionary<string, DateTime>();

        private readonly string _sessionFilePath;
        private readonly string _stickyPrefsFilePath;
        private readonly string _uiPrefsFilePath;

        private static readonly string[] ALL_FEATURE_KEYS = new string[]
        {
            "tempSel",
            "machMode",
            "windSpeed",
            "windDirectionVertical",
            "windDirectionHorizontal",
            "humanSensingStatus",
            "rapidMode",
            "muteStatus",
            "silentSleepStatus",
            "healthMode",
            "energySavingStatus",
            "10degreeHeatingStatus",
            "screenDisplayStatus",
            "echoStatus",
            "selfCleaningStatus",
            "selfCleaning56Status",
            "freshAirStatus",
            "lockStatus"
        };

        public HonCloudClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072;
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HaierHonDesktop"
            );
            Directory.CreateDirectory(appData);
            _sessionFilePath = Path.Combine(appData, "session.json");
            _stickyPrefsFilePath = Path.Combine(appData, "sticky_prefs.json");
            _uiPrefsFilePath = Path.Combine(appData, "ui_prefs.json");

            UiLang = "tr";
            UiTheme = "dark";
            LoadUiPrefs();
            LoadStickyPrefs();
            InitDemoAppliances();
            TryRestoreSession();
            StartBackgroundModelProfiler();
        }

        private void LoadUiPrefs()
        {
            try
            {
                if (!File.Exists(_uiPrefsFilePath)) return;
                string raw = File.ReadAllText(_uiPrefsFilePath, Encoding.UTF8);
                var data = _json.Deserialize<Dictionary<string, string>>(raw);
                if (data != null)
                {
                    if (data.ContainsKey("lang") && (data["lang"] == "tr" || data["lang"] == "en"))
                        UiLang = data["lang"];
                    if (data.ContainsKey("theme") && (data["theme"] == "dark" || data["theme"] == "light"))
                        UiTheme = data["theme"];
                }
            }
            catch { }
        }

        public void SetUiPreferences(string lang, string theme)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(lang))
                {
                    string l = lang.Trim().ToLowerInvariant();
                    if (l == "tr" || l == "en")
                        UiLang = l;
                }
                if (!string.IsNullOrEmpty(theme))
                {
                    string t = theme.Trim().ToLowerInvariant();
                    if (t == "dark" || t == "light")
                        UiTheme = t;
                }
            }
            try
            {
                var data = new Dictionary<string, string>
                {
                    { "lang", UiLang },
                    { "theme", UiTheme }
                };
                File.WriteAllText(_uiPrefsFilePath, _json.Serialize(data), Encoding.UTF8);
            }
            catch { }
        }

        private void LoadStickyPrefs()
        {
            try
            {
                if (!File.Exists(_stickyPrefsFilePath)) return;
                string raw = File.ReadAllText(_stickyPrefsFilePath, Encoding.UTF8);
                var data = _json.Deserialize<Dictionary<string, Dictionary<string, string>>>(raw);
                if (data != null)
                {
                    var cleaned = new Dictionary<string, Dictionary<string, string>>();
                    foreach (var macKv in data)
                    {
                        if (macKv.Value == null) continue;
                        var onlySticky = new Dictionary<string, string>();
                        if (macKv.Value.ContainsKey("echoStatus"))
                            onlySticky["echoStatus"] = macKv.Value["echoStatus"];
                        if (macKv.Value.ContainsKey("screenDisplayStatus"))
                            onlySticky["screenDisplayStatus"] = macKv.Value["screenDisplayStatus"];
                        if (macKv.Value.ContainsKey("lightStatus"))
                            onlySticky["lightStatus"] = macKv.Value["lightStatus"];
                        cleaned[macKv.Key] = onlySticky;
                    }
                    lock (_lock)
                    {
                        _userStickyParamsByMac = cleaned;
                    }
                    SaveStickyPrefs();
                }
            }
            catch { }
        }

        private void SaveStickyPrefs()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    json = _json.Serialize(_userStickyParamsByMac);
                }
                File.WriteAllText(_stickyPrefsFilePath, json, Encoding.UTF8);
            }
            catch { }
        }

        private void StartBackgroundModelProfiler()
        {
            _backgroundDiscoveryThread = new Thread(BackgroundProfilerLoop)
            {
                IsBackground = true,
                Name = "HonBackgroundModelProfiler"
            };
            _backgroundDiscoveryThread.Start();

            Thread realtimeStatusThread = new Thread(RealtimeStatusLoop)
            {
                IsBackground = true,
                Name = "HonRealtimeStatusMonitor"
            };
            realtimeStatusThread.Start();
        }

        private void RealtimeStatusLoop()
        {
            int tick = 0;
            while (_running)
            {
                try
                {
                    Thread.Sleep(1000);
                    if (!IsCloudConnected) continue;
                    tick++;

                    List<Dictionary<string, object>> snapshotList;
                    string currentSelectedMac;
                    lock (_lock)
                    {
                        snapshotList = new List<Dictionary<string, object>>(_appliances);
                        currentSelectedMac = SelectedMac;
                    }

                    foreach (var app in snapshotList)
                    {
                        string mac = GetStr(app, "macAddress");
                        if (string.IsNullOrEmpty(mac)) continue;
                        bool isDemo = app.ContainsKey("isDemo") && Convert.ToBoolean(app["isDemo"]);
                        if (isDemo) continue;

                        if (mac == currentSelectedMac || (tick % 5 == 0))
                        {
                            RefreshApplianceAttributes(app);
                        }
                    }
                }
                catch { }
            }
        }

        private void BackgroundProfilerLoop()
        {
            while (_running)
            {
                try
                {
                    Thread.Sleep(15000);
                    if (!IsCloudConnected) continue;

                    // 1. Check for newly added appliances on the account
                    CheckForNewCloudAppliancesInBackground();

                    // 2. Ensure every connected appliance has a complete model profile & fresh shadow telemetry
                    List<Dictionary<string, object>> snapshotList;
                    lock (_lock)
                    {
                        snapshotList = new List<Dictionary<string, object>>(_appliances);
                    }

                    foreach (var app in snapshotList)
                    {
                        string mac = GetStr(app, "macAddress");
                        if (string.IsNullOrEmpty(mac)) continue;
                        bool isDemo = app.ContainsKey("isDemo") && Convert.ToBoolean(app["isDemo"]);
                        if (isDemo) continue;

                        bool needsSchema;
                        lock (_lock)
                        {
                            needsSchema = !_commandSchemaByMac.ContainsKey(mac) || !_capabilitiesByMac.ContainsKey(mac);
                        }

                        if (needsSchema)
                        {
                            RefreshApplianceAttributes(app);
                            LoadApplianceCommands(app);
                        }
                        else if (mac == SelectedMac)
                        {
                            RefreshApplianceAttributes(app);
                        }
                    }
                }
                catch { }
            }
        }

        private void CheckForNewCloudAppliancesInBackground()
        {
            if (!IsCloudConnected) return;
            try
            {
                string url = API_URL + "/commands/v1/appliance";
                int status;
                string location;
                string resp = HttpSend("GET", url, null, GetAuthenticatedHeaders(), true, out status, out location);
                var root = _json.Deserialize<Dictionary<string, object>>(resp);
                if (root == null || !root.ContainsKey("payload")) return;
                var payload = root["payload"] as Dictionary<string, object>;
                if (payload == null || !payload.ContainsKey("appliances")) return;
                var list = payload["appliances"] as ArrayList;
                if (list == null) return;

                foreach (object item in list)
                {
                    var app = item as Dictionary<string, object>;
                    if (app == null) continue;
                    string mac = GetStr(app, "macAddress");
                    if (string.IsNullOrEmpty(mac)) continue;

                    bool exists = false;
                    lock (_lock)
                    {
                        foreach (var existing in _appliances)
                        {
                            if (GetStr(existing, "macAddress") == mac)
                            {
                                exists = true;
                                break;
                            }
                        }
                    }

                    if (!exists)
                    {
                        app["isDemo"] = false;
                        NormalizeApplianceAttributes(app);
                        lock (_lock)
                        {
                            _appliances.Add(app);
                        }
                        RefreshApplianceAttributes(app);
                        LoadApplianceCommands(app);
                    }
                }
            }
            catch { }
        }

        private void InitDemoAppliances()
        {
            // Demo 1: Haier Tundra (HAS09TAAIN - ac_1263 real schema)
            string mac1 = "AC-HA-1E-R0-12-63";
            var app1 = new Dictionary<string, object>
            {
                { "macAddress", mac1 },
                { "nickName", "Salon Kliması" },
                { "modelName", "HAS09TAAIN" },
                { "brand", "Haier" },
                { "series", "Tundra Green" },
                { "applianceTypeName", "AC" },
                { "applianceModelId", 1263 },
                { "code", "AC01263TND" },
                { "fwVersion", "5.15.0" },
                { "eepromId", 41 },
                { "windType", "vertical" },
                { "installationType", "1to1" },
                { "connectivity", "Wi-Fi + BLE" },
                { "isDemo", true }
            };

            var params1 = CreateDefaultAcParams("23", "25.5", "31.0", "48");
            var caps1 = new Dictionary<string, bool>
            {
                { "tempSel", true },
                { "machMode", true },
                { "windSpeed", true },
                { "windDirectionVertical", true },
                { "windDirectionHorizontal", false },
                { "humanSensingStatus", false },
                { "rapidMode", true },
                { "muteStatus", true },
                { "silentSleepStatus", true },
                { "healthMode", false },
                { "energySavingStatus", false },
                { "10degreeHeatingStatus", true },
                { "screenDisplayStatus", true },
                { "echoStatus", true },
                { "selfCleaningStatus", true },
                { "selfCleaning56Status", false },
                { "freshAirStatus", false },
                { "lockStatus", false }
            };

            // Demo 2: Haier Flexis Plus (AS35S2SF1FA - ac_311 real schema)
            string mac2 = "AC-HA-1E-R0-03-11";
            var app2 = new Dictionary<string, object>
            {
                { "macAddress", mac2 },
                { "nickName", "Yatak Odası Kliması" },
                { "modelName", "AS35S2SF1FA" },
                { "brand", "Haier" },
                { "series", "Flexis Plus" },
                { "applianceTypeName", "AC" },
                { "applianceModelId", 311 },
                { "code", "AC00311FLX" },
                { "fwVersion", "5.18.2" },
                { "eepromId", 52 },
                { "windType", "both" },
                { "installationType", "1to1" },
                { "connectivity", "Wi-Fi + BLE" },
                { "isDemo", true }
            };

            var params2 = CreateDefaultAcParams("22", "24.0", "31.0", "45");
            var caps2 = new Dictionary<string, bool>
            {
                { "tempSel", true },
                { "machMode", true },
                { "windSpeed", true },
                { "windDirectionVertical", true },
                { "windDirectionHorizontal", true },
                { "humanSensingStatus", true },
                { "rapidMode", true },
                { "muteStatus", true },
                { "silentSleepStatus", true },
                { "healthMode", true },
                { "energySavingStatus", true },
                { "10degreeHeatingStatus", true },
                { "screenDisplayStatus", true },
                { "echoStatus", true },
                { "selfCleaningStatus", true },
                { "selfCleaning56Status", true },
                { "freshAirStatus", false },
                { "lockStatus", false }
            };

            _appliances.Clear();
            _appliances.Add(app1);
            _appliances.Add(app2);

            _parametersByMac[mac1] = params1;
            _parametersByMac[mac2] = params2;

            _capabilitiesByMac[mac1] = caps1;
            _capabilitiesByMac[mac2] = caps2;

            _modelProfileByMac[mac1] = BuildModelProfileSummary(app1, caps1, 16, 30, 1);
            _modelProfileByMac[mac2] = BuildModelProfileSummary(app2, caps2, 16, 30, 1);

            SelectedMac = mac1;
        }

        private static Dictionary<string, object> BuildModelProfileSummary(
            Dictionary<string, object> app,
            Dictionary<string, bool> caps,
            double tempMin,
            double tempMax,
            double tempStep,
            Dictionary<string, object> modeTempLimits = null)
        {
            int supportedCount = 0;
            int unsupportedCount = 0;
            foreach (var kv in caps)
            {
                if (kv.Key == "tempSel" || kv.Key == "machMode" || kv.Key == "windSpeed")
                    continue;
                if (kv.Value) supportedCount++;
                else unsupportedCount++;
            }

            if (modeTempLimits == null)
            {
                modeTempLimits = new Dictionary<string, object>
                {
                    { "0", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                    { "1", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                    { "2", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                    { "4", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                    { "6", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } }
                };
            }

            return new Dictionary<string, object>
            {
                { "profileStatus", "completed" },
                { "detectedAt", DateTime.Now.ToString("HH:mm:ss") },
                { "modelName", GetStr(app, "modelName", "Haier AC") },
                { "series", GetStr(app, "series", "Split Series") },
                { "applianceModelId", GetStr(app, "applianceModelId", "-") },
                { "fwVersion", GetStr(app, "fwVersion", "-") },
                { "windType", GetStr(app, "windType", "vertical") },
                { "installationType", GetStr(app, "installationType", "1to1") },
                { "tempMin", tempMin },
                { "tempMax", tempMax },
                { "tempStep", tempStep },
                { "modeTempLimits", modeTempLimits },
                { "supportedCount", supportedCount },
                { "unsupportedCount", unsupportedCount },
                { "totalFeatures", supportedCount + unsupportedCount }
            };
        }

        private static Dictionary<string, object> CreateDefaultAcParams(string tempSel, string tempIn, string tempOut, string hum)
        {
            return new Dictionary<string, object>
            {
                { "onOffStatus", "1" },
                { "machMode", "1" },
                { "tempSel", tempSel },
                { "tempIndoor", tempIn },
                { "tempAirOutdoor", tempOut },
                { "tempOutdoor", tempOut },
                { "humidityIndoor", hum },
                { "windSpeed", "5" },
                { "windDirectionVertical", "5" },
                { "windDirectionHorizontal", "0" },
                { "muteStatus", "0" },
                { "rapidMode", "0" },
                { "silentSleepStatus", "0" },
                { "healthMode", "0" },
                { "energySavingStatus", "0" },
                { "10degreeHeatingStatus", "0" },
                { "screenDisplayStatus", "1" },
                { "echoStatus", "1" },
                { "selfCleaningStatus", "0" },
                { "selfCleaning56Status", "0" },
                { "humanSensingStatus", "0" },
                { "freshAirStatus", "0" },
                { "lockStatus", "0" },
                { "halfDegreeSettingStatus", "0" },
                { "tempUnit", "0" },
                { "operationName", "grSetDAC" },
                { "tempCoilerIndoor", "14.5" },
                { "tempCoilerOutdoor", "38.0" },
                { "compressorFrequency", "42" },
                { "expansionValveOpenDegree", "210" },
                { "fourWayValveStatus", "0" },
                { "airQuality", "1" },
                { "pm2p5ValueIndoor", "12" },
                { "vocValueIndoor", "8" },
                { "errors", "00" },
                { "totalWorkTime", "418" },
                { "specialMode", "0" },
                { "cleaningTimeStatus", "0" },
                { "heatAccumulationStatus", "0" },
                { "humidificationStatus", "0" },
                { "pmvStatus", "0" },
                { "humiditySel", "30" },
                { "lightStatus", "0" },
                { "electricHeatingStatus", "0" },
                { "ch2oCleaningStatus", "0" },
                { "filterChangeStatusCloud", "0" },
                { "energySavePeriod", "15" },
                { "intelligenceStatus", "0" },
                { "voiceSignStatus", "0" },
                { "voiceStatus", "0" },
                { "windSensingStatus", "0" },
                { "pm2p5CleaningStatus", "0" }
            };
        }

        private void TryRestoreSession()
        {
            try
            {
                if (!File.Exists(_sessionFilePath)) return;
                string raw = File.ReadAllText(_sessionFilePath, Encoding.UTF8);
                var data = _json.Deserialize<Dictionary<string, string>>(raw);
                if (data != null && data.ContainsKey("refreshToken") && !string.IsNullOrEmpty(data["refreshToken"]))
                {
                    _refreshToken = data["refreshToken"];
                    CurrentEmail = data.ContainsKey("email") ? data["email"] : "";
                    if (RefreshCloudSession(_refreshToken))
                    {
                        ThreadPool.QueueUserWorkItem(_ => LoadCloudAppliances());
                    }
                }
            }
            catch { }
        }

        private void SaveSession()
        {
            try
            {
                var data = new Dictionary<string, string>
                {
                    { "email", CurrentEmail ?? "" },
                    { "refreshToken", _refreshToken ?? "" }
                };
                File.WriteAllText(_sessionFilePath, _json.Serialize(data), Encoding.UTF8);
            }
            catch { }
        }

        public void LogoutAndUseDemo()
        {
            lock (_lock)
            {
                IsCloudConnected = false;
                CurrentEmail = "";
                _accessToken = "";
                _refreshToken = "";
                _idToken = "";
                _cognitoToken = "";
                try { if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath); } catch { }
                InitDemoAppliances();
            }
        }

        public bool LoginWithCredentials(string email, string password, out string message)
        {
            LastError = "";
            message = "";
            try
            {
                _cookies = new CookieContainer();
                CurrentEmail = email != null ? email.Trim() : "";

                string nonce = Guid.NewGuid().ToString();
                string redirectUri = Uri.EscapeDataString("hon://mobilesdk/detect/oauth/done");
                string introUrl = string.Format(
                    "{0}/services/oauth2/authorize/expid_Login?response_type=token+id_token&client_id={1}&redirect_uri={2}&display=touch&scope=api%20openid%20refresh_token%20web&nonce={3}",
                    AUTH_API, CLIENT_ID, redirectUri, nonce
                );

                int status;
                string location;
                string body = HttpSend("GET", introUrl, null, null, true, out status, out location);

                if (body.Contains("oauth/done#access_token=") || (location != null && location.Contains("oauth/done#access_token=")))
                {
                    ParseTokensFromText(body + "\n" + location);
                }
                else
                {
                    Match mUrl = Regex.Match(body, "(?:url|href)\\s*=\\s*['\"](.+?)['\"]");
                    if (!mUrl.Success)
                    {
                        LastError = "OAuth2 giriş sayfası yönlendirmesi bulunamadı.";
                        message = LastError;
                        return false;
                    }

                    string loginUrl = mUrl.Groups[1].Value;
                    if (loginUrl.StartsWith("/NewhOnLogin"))
                        loginUrl = AUTH_API + "/s/login" + loginUrl;
                    else if (loginUrl.StartsWith("/"))
                        loginUrl = AUTH_API + loginUrl;

                    string redir1 = ManualRedirect(loginUrl);
                    string redir2 = ManualRedirect(redir1);
                    string finalLoginPageUrl = redir2 + "&System=IoT_Mobile_App&RegistrationSubChannel=hOn";

                    string loginPageHtml = HttpSend("GET", finalLoginPageUrl, null, null, true, out status, out location);

                    Match mContext = Regex.Match(loginPageHtml, "\"fwuid\":\"(.*?)\",\"loaded\":(\\{.*?\\})");
                    if (!mContext.Success)
                    {
                        LastError = "Salesforce Aura oturum bağlamı alınamadı.";
                        message = LastError;
                        return false;
                    }

                    string fwUid = mContext.Groups[1].Value;
                    string loadedJson = mContext.Groups[2].Value;
                    object loadedObj = _json.DeserializeObject(loadedJson);
                    string pageUri = finalLoginPageUrl.Replace(AUTH_API, "");

                    string startUrlPart = pageUri;
                    int idxStart = pageUri.LastIndexOf("startURL=", StringComparison.OrdinalIgnoreCase);
                    if (idxStart >= 0)
                        startUrlPart = pageUri.Substring(idxStart + "startURL=".Length);
                    startUrlPart = Uri.UnescapeDataString(startUrlPart);
                    int idxEq = startUrlPart.IndexOf("%3D", StringComparison.OrdinalIgnoreCase);
                    if (idxEq >= 0)
                        startUrlPart = startUrlPart.Substring(0, idxEq);

                    var action = new Dictionary<string, object>
                    {
                        { "id", "79;a" },
                        { "descriptor", "apex://LightningLoginCustomController/ACTION$login" },
                        { "callingDescriptor", "markup://c:loginForm" },
                        { "params", new Dictionary<string, object> {
                            { "username", CurrentEmail },
                            { "password", password },
                            { "startUrl", startUrlPart }
                        }}
                    };

                    var auraMessage = new Dictionary<string, object>
                    {
                        { "actions", new object[] { action } }
                    };

                    var auraContext = new Dictionary<string, object>
                    {
                        { "mode", "PROD" },
                        { "fwuid", fwUid },
                        { "app", "siteforce:loginApp2" },
                        { "loaded", loadedObj },
                        { "dn", new object[0] },
                        { "globals", new Dictionary<string, object>() },
                        { "uad", false }
                    };

                    string formData =
                        "message=" + Uri.EscapeDataString(_json.Serialize(auraMessage)) +
                        "&aura.context=" + Uri.EscapeDataString(_json.Serialize(auraContext)) +
                        "&aura.pageURI=" + Uri.EscapeDataString(pageUri) +
                        "&aura.token=null";

                    string auraUrl = AUTH_API + "/s/sfsites/aura?r=3&other.LightningLoginCustom.login=1";
                    var auraHeaders = new Dictionary<string, string>
                    {
                        { "Content-Type", "application/x-www-form-urlencoded" }
                    };

                    string auraResp = HttpSend("POST", auraUrl, formData, auraHeaders, true, out status, out location);

                    var auraObj = _json.Deserialize<Dictionary<string, object>>(auraResp);
                    string auraError;
                    string redirectTokenUrl = ExtractAuraRedirectUrl(auraObj, out auraError);
                    if (string.IsNullOrEmpty(redirectTokenUrl))
                    {
                        LastError = !string.IsNullOrEmpty(auraError)
                            ? "Giriş başarısız: " + auraError
                            : "Giriş başarısız: E-posta veya şifre hatalı.";
                        message = LastError;
                        return false;
                    }

                    if (!FollowTokenRedirects(redirectTokenUrl))
                    {
                        LastError = "OAuth2 erişim anahtarları alınamadı.";
                        message = LastError;
                        return false;
                    }
                }

                if (!ExchangeCognitoToken())
                {
                    LastError = "Haier IoT API Cognito yetkilendirmesi başarısız oldu.";
                    message = LastError;
                    return false;
                }

                _tokenExpiresUtc = DateTime.UtcNow.AddHours(7);
                IsCloudConnected = true;
                SaveSession();
                LoadCloudAppliances();

                message = "Haier hOn hesabına başarıyla bağlanıldı.";
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Bağlantı hatası: " + ex.Message;
                message = LastError;
                return false;
            }
        }

        private string ExtractAuraRedirectUrl(Dictionary<string, object> auraObj, out string auraError)
        {
            auraError = "";
            if (auraObj == null) return "";
            try
            {
                if (auraObj.ContainsKey("events"))
                {
                    var events = auraObj["events"] as ArrayList;
                    if (events != null && events.Count > 0)
                    {
                        var ev0 = events[0] as Dictionary<string, object>;
                        if (ev0 != null && ev0.ContainsKey("attributes"))
                        {
                            var attrs = ev0["attributes"] as Dictionary<string, object>;
                            if (attrs != null && attrs.ContainsKey("values"))
                            {
                                var vals = attrs["values"] as Dictionary<string, object>;
                                if (vals != null)
                                {
                                    if (vals.ContainsKey("url") && vals["url"] != null)
                                        return vals["url"].ToString();
                                    if (vals.ContainsKey("message") && vals["message"] != null)
                                        auraError = vals["message"].ToString();
                                }
                            }
                        }
                    }
                }
                if (auraObj.ContainsKey("actions"))
                {
                    var actions = auraObj["actions"] as ArrayList;
                    if (actions != null && actions.Count > 0)
                    {
                        var act0 = actions[0] as Dictionary<string, object>;
                        if (act0 != null && act0.ContainsKey("returnValue") && act0["returnValue"] != null)
                        {
                            auraError = act0["returnValue"].ToString();
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private bool FollowTokenRedirects(string url)
        {
            int status;
            string location;
            string html = HttpSend("GET", url, null, null, false, out status, out location);

            if (ParseTokensFromText(html + "\n" + location))
                return true;

            string nextHref = ExtractHref(html, location);
            if (string.IsNullOrEmpty(nextHref))
                return false;

            if (nextHref.Contains("ProgressiveLogin"))
            {
                string progUrl = nextHref.StartsWith("http") ? nextHref : (AUTH_API + nextHref);
                html = HttpSend("GET", progUrl, null, null, false, out status, out location);
                if (ParseTokensFromText(html + "\n" + location))
                    return true;
                nextHref = ExtractHref(html, location);
            }

            if (!string.IsNullOrEmpty(nextHref))
            {
                string finalUrl = nextHref.StartsWith("http") ? nextHref : (AUTH_API + nextHref);
                html = HttpSend("GET", finalUrl, null, null, false, out status, out location);
                if (ParseTokensFromText(html + "\n" + location))
                    return true;
            }

            return false;
        }

        private string ExtractHref(string html, string locationHeader)
        {
            if (!string.IsNullOrEmpty(locationHeader))
                return locationHeader;
            if (string.IsNullOrEmpty(html))
                return "";
            Match m = Regex.Match(html, "(?:href|location\\.replace\\(|window\\.location\\s*=)\\s*['\"](.+?)['\"]");
            return m.Success ? m.Groups[1].Value : "";
        }

        private bool ParseTokensFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            Match mAccess = Regex.Match(text, "access_token=([^&'\"\\s]+)");
            Match mRefresh = Regex.Match(text, "refresh_token=([^&'\"\\s]+)");
            Match mId = Regex.Match(text, "id_token=([^&'\"\\s]+)");

            if (mAccess.Success && mId.Success)
            {
                _accessToken = Uri.UnescapeDataString(mAccess.Groups[1].Value);
                _idToken = Uri.UnescapeDataString(mId.Groups[1].Value);
                if (mRefresh.Success)
                {
                    _refreshToken = Uri.UnescapeDataString(mRefresh.Groups[1].Value);
                }
                return true;
            }
            return false;
        }

        private bool ExchangeCognitoToken()
        {
            string url = API_URL + "/auth/v1/login";
            var headers = new Dictionary<string, string>
            {
                { "id-token", _idToken },
                { "Content-Type", "application/json" }
            };
            var payload = new Dictionary<string, object>
            {
                { "appVersion", APP_VERSION },
                { "mobileId", _mobileId },
                { "os", OS_TYPE },
                { "osVersion", OS_VERSION },
                { "deviceModel", DEVICE_MODEL }
            };
            string jsonBody = _json.Serialize(payload);
            int status;
            string location;
            string resp = HttpSend("POST", url, jsonBody, headers, true, out status, out location);

            var data = _json.Deserialize<Dictionary<string, object>>(resp);
            if (data != null && data.ContainsKey("cognitoUser"))
            {
                var cog = data["cognitoUser"] as Dictionary<string, object>;
                if (cog != null && cog.ContainsKey("Token") && cog["Token"] != null)
                {
                    _cognitoToken = cog["Token"].ToString();
                    return !string.IsNullOrEmpty(_cognitoToken);
                }
            }
            return false;
        }

        public bool RefreshCloudSession(string refreshToken)
        {
            try
            {
                if (!string.IsNullOrEmpty(refreshToken))
                    _refreshToken = refreshToken;
                if (string.IsNullOrEmpty(_refreshToken))
                    return false;

                string url = string.Format(
                    "{0}/services/oauth2/token?client_id={1}&refresh_token={2}&grant_type=refresh_token",
                    AUTH_API,
                    Uri.EscapeDataString(CLIENT_ID),
                    Uri.EscapeDataString(_refreshToken)
                );

                int status;
                string location;
                string resp = HttpSend("POST", url, "", null, true, out status, out location);
                if (status >= 400 || string.IsNullOrEmpty(resp))
                    return false;

                var data = _json.Deserialize<Dictionary<string, object>>(resp);
                if (data != null && data.ContainsKey("id_token") && data.ContainsKey("access_token"))
                {
                    _idToken = data["id_token"].ToString();
                    _accessToken = data["access_token"].ToString();
                    if (ExchangeCognitoToken())
                    {
                        _tokenExpiresUtc = DateTime.UtcNow.AddHours(7);
                        IsCloudConnected = true;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private Dictionary<string, string> GetAuthenticatedHeaders()
        {
            if (DateTime.UtcNow >= _tokenExpiresUtc && !string.IsNullOrEmpty(_refreshToken))
            {
                RefreshCloudSession(_refreshToken);
            }
            return new Dictionary<string, string>
            {
                { "cognito-token", _cognitoToken },
                { "id-token", _idToken },
                { "Content-Type", "application/json" }
            };
        }

        public void LoadCloudAppliances()
        {
            if (!IsCloudConnected) return;
            try
            {
                string url = API_URL + "/commands/v1/appliance";
                int status;
                string location;
                string resp = HttpSend("GET", url, null, GetAuthenticatedHeaders(), true, out status, out location);

                var root = _json.Deserialize<Dictionary<string, object>>(resp);
                if (root != null && root.ContainsKey("payload"))
                {
                    var payload = root["payload"] as Dictionary<string, object>;
                    if (payload != null && payload.ContainsKey("appliances"))
                    {
                        var list = payload["appliances"] as ArrayList;
                        if (list != null && list.Count > 0)
                        {
                            var loadedAppliances = new List<Dictionary<string, object>>();
                            foreach (object item in list)
                            {
                                var app = item as Dictionary<string, object>;
                                if (app == null) continue;
                                app["isDemo"] = false;
                                NormalizeApplianceAttributes(app);
                                loadedAppliances.Add(app);
                            }

                            if (loadedAppliances.Count > 0)
                            {
                                lock (_lock)
                                {
                                    _appliances = loadedAppliances;
                                    Dictionary<string, object> chosen = _appliances[0];
                                    foreach (var a in _appliances)
                                    {
                                        if (GetStr(a, "applianceTypeName") == "AC")
                                        {
                                            chosen = a;
                                            break;
                                        }
                                    }
                                    SelectedMac = GetStr(chosen, "macAddress");
                                }

                                // Profile SelectedMac immediately, then profile all others in background thread
                                foreach (var app in loadedAppliances)
                                {
                                    string mac = GetStr(app, "macAddress");
                                    if (mac == SelectedMac)
                                    {
                                        RefreshApplianceAttributes(app);
                                        LoadApplianceCommands(app);
                                    }
                                }

                                ThreadPool.QueueUserWorkItem(_ =>
                                {
                                    foreach (var app in loadedAppliances)
                                    {
                                        string mac = GetStr(app, "macAddress");
                                        if (!string.IsNullOrEmpty(mac) && mac != SelectedMac)
                                        {
                                            RefreshApplianceAttributes(app);
                                            LoadApplianceCommands(app);
                                        }
                                    }
                                });
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void NormalizeApplianceAttributes(Dictionary<string, object> app)
        {
            if (app == null || !app.ContainsKey("attributes")) return;
            var attrList = app["attributes"] as ArrayList;
            if (attrList == null) return;
            foreach (object item in attrList)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                string parName = GetStr(d, "parName");
                string parVal = GetStr(d, "parValue");
                if (parName == "series" && !app.ContainsKey("series"))
                    app["series"] = parVal;
                else if (parName == "windType" && !app.ContainsKey("windType"))
                    app["windType"] = parVal;
                else if (parName == "installationType" && !app.ContainsKey("installationType"))
                    app["installationType"] = parVal;
            }
        }

        public void LoadApplianceCommands(Dictionary<string, object> app)
        {
            if (!IsCloudConnected || app == null) return;
            string mac = GetStr(app, "macAddress");
            try
            {
                string appType = GetStr(app, "applianceTypeName", "AC");
                string modelId = GetStr(app, "applianceModelId", "0");
                string code = GetStr(app, "code", "");
                if (string.IsNullOrEmpty(code))
                {
                    string sn = GetStr(app, "serialNumber", "");
                    if (!string.IsNullOrEmpty(sn))
                        code = sn.Length < 18 ? sn.Substring(0, Math.Min(8, sn.Length)) : sn.Substring(0, 11);
                }

                var qs = new List<string>
                {
                    "applianceType=" + Uri.EscapeDataString(appType),
                    "applianceModelId=" + Uri.EscapeDataString(modelId),
                    "macAddress=" + Uri.EscapeDataString(mac),
                    "os=" + OS_TYPE,
                    "appVersion=" + Uri.EscapeDataString(APP_VERSION),
                    "code=" + Uri.EscapeDataString(code)
                };
                string eepromId = GetStr(app, "eepromId");
                if (!string.IsNullOrEmpty(eepromId)) qs.Add("firmwareId=" + Uri.EscapeDataString(eepromId));
                string fwVersion = GetStr(app, "fwVersion");
                if (!string.IsNullOrEmpty(fwVersion)) qs.Add("fwVersion=" + Uri.EscapeDataString(fwVersion));
                string series = GetStr(app, "series");
                if (!string.IsNullOrEmpty(series)) qs.Add("series=" + Uri.EscapeDataString(series));

                string url = API_URL + "/commands/v1/retrieve?" + string.Join("&", qs.ToArray());
                int status;
                string location;
                string resp = HttpSend("GET", url, null, GetAuthenticatedHeaders(), true, out status, out location);

                var root = _json.Deserialize<Dictionary<string, object>>(resp);
                if (root != null && root.ContainsKey("payload"))
                {
                    var payload = root["payload"] as Dictionary<string, object>;
                    if (payload != null)
                    {
                        lock (_lock)
                        {
                            _commandSchemaByMac[mac] = payload;
                            EnsureDefaultCommandParametersAndCapabilities(app, mac, payload);
                        }
                        return;
                    }
                }
            }
            catch { }

            // Fallback model-heuristic capability detection if cloud command schema is unavailable
            lock (_lock)
            {
                ApplyHeuristicModelCapabilities(app, mac);
            }
        }

        private void EnsureDefaultCommandParametersAndCapabilities(
            Dictionary<string, object> app,
            string mac,
            Dictionary<string, object> schemaPayload)
        {
            if (!_parametersByMac.ContainsKey(mac))
                _parametersByMac[mac] = new Dictionary<string, object>();

            var target = _parametersByMac[mac];
            var caps = new Dictionary<string, bool>();
            foreach (string key in ALL_FEATURE_KEYS)
            {
                caps[key] = false;
            }

            double tempMin = 16;
            double tempMax = 30;
            double tempStep = 1;

            if (schemaPayload.ContainsKey("applianceModel"))
            {
                var appModel = schemaPayload["applianceModel"] as Dictionary<string, object>;
                if (appModel != null)
                {
                    NormalizeApplianceAttributes(appModel);
                    if (appModel.ContainsKey("series") && !app.ContainsKey("series"))
                        app["series"] = appModel["series"];
                    if (appModel.ContainsKey("windType") && !app.ContainsKey("windType"))
                        app["windType"] = appModel["windType"];
                    if (appModel.ContainsKey("installationType") && !app.ContainsKey("installationType"))
                        app["installationType"] = appModel["installationType"];
                }
            }

            if (schemaPayload.ContainsKey("settings"))
            {
                var settings = schemaPayload["settings"] as Dictionary<string, object>;
                if (settings != null && settings.ContainsKey("setParameters"))
                {
                    var setParams = settings["setParameters"] as Dictionary<string, object>;
                    if (setParams != null && setParams.ContainsKey("parameters"))
                    {
                        var paramDefs = setParams["parameters"] as Dictionary<string, object>;
                        if (paramDefs != null)
                        {
                            foreach (var kv in paramDefs)
                            {
                                var def = kv.Value as Dictionary<string, object>;
                                if (def == null) continue;

                                string typology = GetStr(def, "typology").ToLowerInvariant();
                                bool isWritable = false;

                                if (typology == "range")
                                {
                                    string minV = GetStr(def, "minimumValue", "0");
                                    string maxV = GetStr(def, "maximumValue", "0");
                                    isWritable = (minV != maxV);

                                    if (kv.Key == "tempSel")
                                    {
                                        double.TryParse(minV.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out tempMin);
                                        double.TryParse(maxV.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out tempMax);
                                        string stepV = GetStr(def, "incrementValue", "1");
                                        double.TryParse(stepV.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out tempStep);
                                    }
                                }
                                else if (typology == "enum")
                                {
                                    var enumVals = def.ContainsKey("enumValues") ? def["enumValues"] as ArrayList : null;
                                    isWritable = (enumVals != null && enumVals.Count > 1);
                                }

                                if (caps.ContainsKey(kv.Key))
                                {
                                    caps[kv.Key] = isWritable;
                                }

                                if (!target.ContainsKey(kv.Key))
                                {
                                    if (kv.Key == "onOffStatus")
                                        target[kv.Key] = "0";
                                    else if (def.ContainsKey("defaultValue") && def["defaultValue"] != null)
                                        target[kv.Key] = def["defaultValue"].ToString();
                                    else if (def.ContainsKey("fixedValue") && def["fixedValue"] != null)
                                        target[kv.Key] = def["fixedValue"].ToString();
                                    else if (def.ContainsKey("minimumValue") && def["minimumValue"] != null)
                                        target[kv.Key] = def["minimumValue"].ToString();
                                    else
                                        target[kv.Key] = "0";
                                }
                            }
                        }

                        // Respect physical windType attribute on the appliance model (e.g. Revive AS35RBAHRA-4 has windType="vertical")
                        string modelWindType = GetStr(app, "windType", "").ToLowerInvariant();
                        if (modelWindType == "vertical" && caps.ContainsKey("windDirectionHorizontal"))
                        {
                            caps["windDirectionHorizontal"] = false;
                        }

                        // Evaluate ancillaryParameters.programRules (e.g. multi-split 1toN restrictions)
                        if (setParams.ContainsKey("ancillaryParameters"))
                        {
                            var anc = setParams["ancillaryParameters"] as Dictionary<string, object>;
                            if (anc != null && anc.ContainsKey("programRules"))
                            {
                                var pr = anc["programRules"] as Dictionary<string, object>;
                                if (pr != null && pr.ContainsKey("fixedValue"))
                                {
                                    var fv = pr["fixedValue"] as Dictionary<string, object>;
                                    string instType = GetStr(app, "installationType", "1to1");
                                    if (fv != null)
                                    {
                                        foreach (var ruleKv in fv)
                                        {
                                            string targetParam = ruleKv.Key;
                                            var conds = ruleKv.Value as Dictionary<string, object>;
                                            if (conds != null && conds.ContainsKey("$installationType"))
                                            {
                                                var instMap = conds["$installationType"] as Dictionary<string, object>;
                                                if (instMap != null && instMap.ContainsKey(instType))
                                                {
                                                    var forced = instMap[instType] as Dictionary<string, object>;
                                                    if (forced != null && GetStr(forced, "typology") == "fixed" && GetStr(forced, "fixedValue") == "0")
                                                    {
                                                        if (caps.ContainsKey(targetParam))
                                                            caps[targetParam] = false;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            var modeTempLimits = new Dictionary<string, object>
            {
                { "0", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                { "1", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                { "2", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                { "4", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } },
                { "6", new Dictionary<string, object> { { "min", tempMin }, { "max", tempMax }, { "step", tempStep } } }
            };

            if (schemaPayload.ContainsKey("startProgram"))
            {
                var startProg = schemaPayload["startProgram"] as Dictionary<string, object>;
                if (startProg != null)
                {
                    var progMap = new Dictionary<string, string>
                    {
                        { "PROGRAMS.AC.IOT_AUTO", "0" },
                        { "PROGRAMS.AC.IOT_COOL", "1" },
                        { "PROGRAMS.AC.IOT_DRY",  "2" },
                        { "PROGRAMS.AC.IOT_HEAT", "4" },
                        { "PROGRAMS.AC.IOT_FAN",  "6" }
                    };
                    foreach (var pm in progMap)
                    {
                        if (!startProg.ContainsKey(pm.Key)) continue;
                        var pDef = startProg[pm.Key] as Dictionary<string, object>;
                        if (pDef == null || !pDef.ContainsKey("parameters")) continue;
                        var pParams = pDef["parameters"] as Dictionary<string, object>;
                        if (pParams == null || !pParams.ContainsKey("tempSel")) continue;
                        var tDef = pParams["tempSel"] as Dictionary<string, object>;
                        if (tDef == null) continue;

                        double mMin = tempMin;
                        double mMax = tempMax;
                        double mStep = tempStep;
                        string minStr = GetStr(tDef, "minimumValue", "");
                        string maxStr = GetStr(tDef, "maximumValue", "");
                        string stepStr = GetStr(tDef, "incrementValue", "");
                        if (!string.IsNullOrEmpty(minStr))
                            double.TryParse(minStr.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out mMin);
                        if (!string.IsNullOrEmpty(maxStr))
                            double.TryParse(maxStr.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out mMax);
                        if (!string.IsNullOrEmpty(stepStr))
                            double.TryParse(stepStr.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out mStep);

                        modeTempLimits[pm.Value] = new Dictionary<string, object>
                        {
                            { "min", mMin },
                            { "max", mMax },
                            { "step", mStep }
                        };
                    }
                }
            }

            _capabilitiesByMac[mac] = caps;
            _modelProfileByMac[mac] = BuildModelProfileSummary(app, caps, tempMin, tempMax, tempStep, modeTempLimits);
        }

        private void ApplyHeuristicModelCapabilities(Dictionary<string, object> app, string mac)
        {
            string model = GetStr(app, "modelName", "").ToUpperInvariant();
            string series = GetStr(app, "series", "").ToLowerInvariant();
            string windType = GetStr(app, "windType", "vertical").ToLowerInvariant();

            bool isHighEnd = series.Contains("flexis") || series.Contains("jade") || series.Contains("expert") || series.Contains("pearl");
            bool hasHorizontalSwing = windType.Contains("both") || windType.Contains("horizontal") || isHighEnd;

            var caps = new Dictionary<string, bool>
            {
                { "tempSel", true },
                { "machMode", true },
                { "windSpeed", true },
                { "windDirectionVertical", true },
                { "windDirectionHorizontal", hasHorizontalSwing },
                { "humanSensingStatus", series.Contains("flexis") || series.Contains("expert") || series.Contains("jade") },
                { "rapidMode", true },
                { "muteStatus", true },
                { "silentSleepStatus", true },
                { "healthMode", isHighEnd || series.Contains("revive") },
                { "energySavingStatus", isHighEnd },
                { "10degreeHeatingStatus", true },
                { "screenDisplayStatus", true },
                { "echoStatus", true },
                { "selfCleaningStatus", true },
                { "selfCleaning56Status", isHighEnd || series.Contains("revive") },
                { "freshAirStatus", series.Contains("fresh") || series.Contains("jade") },
                { "lockStatus", false }
            };

            _capabilitiesByMac[mac] = caps;
            _modelProfileByMac[mac] = BuildModelProfileSummary(app, caps, 16, 30, 1);
        }

        public void RefreshApplianceAttributes(Dictionary<string, object> app)
        {
            if (app == null) return;
            string mac = GetStr(app, "macAddress");
            bool isDemo = app.ContainsKey("isDemo") && Convert.ToBoolean(app["isDemo"]);
            if (isDemo || !IsCloudConnected)
                return;

            try
            {
                string appType = GetStr(app, "applianceTypeName", "AC");
                string url = string.Format(
                    "{0}/commands/v1/context?macAddress={1}&applianceType={2}&category=CYCLE",
                    API_URL,
                    Uri.EscapeDataString(mac),
                    Uri.EscapeDataString(appType)
                );
                int status;
                string location;
                string resp = HttpSend("GET", url, null, GetAuthenticatedHeaders(), true, out status, out location);

                var root = _json.Deserialize<Dictionary<string, object>>(resp);
                if (root != null && root.ContainsKey("payload"))
                {
                    var payload = root["payload"] as Dictionary<string, object>;
                    if (payload != null)
                    {
                        // ONLY echoStatus, screenDisplayStatus, and lightStatus are sticky user preferences
                        // because Haier's shadow.parameters never updates echoStatus (always "0") and resets screenDisplayStatus on stopProgram.
                        // All other states (especially onOffStatus, tempSel, machMode, windSpeed) are updated in real time every 1 second!
                        var stickyOnlyKeys = new HashSet<string>(new string[]
                        {
                            "echoStatus", "screenDisplayStatus", "lightStatus"
                        });

                        lock (_lock)
                        {
                            bool isInitialLoad = !_parametersByMac.ContainsKey(mac) || _parametersByMac[mac].Count == 0;
                            if (!_parametersByMac.ContainsKey(mac))
                                _parametersByMac[mac] = new Dictionary<string, object>();

                            // Brief 3-second shield only while an outgoing HTTP command is actively in flight to the AC
                            bool inFlightCommand = _lastCommandSentUtcByMac.ContainsKey(mac)
                                && (DateTime.UtcNow - _lastCommandSentUtcByMac[mac]).TotalSeconds < 3.0;

                            if (payload.ContainsKey("shadow"))
                            {
                                var shadow = payload["shadow"] as Dictionary<string, object>;
                                if (shadow != null && shadow.ContainsKey("parameters"))
                                {
                                    var shadowParams = shadow["parameters"] as Dictionary<string, object>;
                                    if (shadowParams != null)
                                    {
                                        foreach (var kv in shadowParams)
                                        {
                                            var pObj = kv.Value as Dictionary<string, object>;
                                            if (pObj == null || !pObj.ContainsKey("parNewVal") || pObj["parNewVal"] == null)
                                                continue;

                                            string val = pObj["parNewVal"].ToString();
                                            if (kv.Key == "tempSel" && val.Contains("."))
                                            {
                                                double d;
                                                if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d))
                                                    val = ((int)Math.Round(d)).ToString();
                                            }

                                            if (isInitialLoad)
                                            {
                                                _parametersByMac[mac][kv.Key] = val;
                                            }
                                            else if (!inFlightCommand && !stickyOnlyKeys.Contains(kv.Key))
                                            {
                                                // Live real-time update of onOffStatus, tempIndoor, tempSel, machMode, windSpeed, etc.
                                                _parametersByMac[mac][kv.Key] = val;
                                            }
                                        }
                                    }
                                }
                            }

                            // Extract echoStatus and screenDisplayStatus from commandHistory if not yet set by user in _userStickyParamsByMac
                            if (payload.ContainsKey("commandHistory"))
                            {
                                var cmdHist = payload["commandHistory"] as Dictionary<string, object>;
                                if (cmdHist != null && cmdHist.ContainsKey("command"))
                                {
                                    var cmdObj = cmdHist["command"] as Dictionary<string, object>;
                                    if (cmdObj != null && cmdObj.ContainsKey("parameters"))
                                    {
                                        var histParams = cmdObj["parameters"] as Dictionary<string, object>;
                                        if (histParams != null)
                                        {
                                            if (!_userStickyParamsByMac.ContainsKey(mac))
                                                _userStickyParamsByMac[mac] = new Dictionary<string, string>();

                                            foreach (string sKey in stickyOnlyKeys)
                                            {
                                                if (histParams.ContainsKey(sKey) && histParams[sKey] != null)
                                                {
                                                    if (!_userStickyParamsByMac[mac].ContainsKey(sKey))
                                                    {
                                                        _userStickyParamsByMac[mac][sKey] = histParams[sKey].ToString();
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            // Enforce sticky user preferences ONLY for echoStatus, screenDisplayStatus, and lightStatus
                            if (_userStickyParamsByMac.ContainsKey(mac))
                            {
                                foreach (var skv in _userStickyParamsByMac[mac])
                                {
                                    if (stickyOnlyKeys.Contains(skv.Key))
                                    {
                                        _parametersByMac[mac][skv.Key] = skv.Value;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public Dictionary<string, object> GetStateSnapshot(bool forceCloudPoll)
        {
            Dictionary<string, object> activeApp = null;
            lock (_lock)
            {
                foreach (var a in _appliances)
                {
                    if (GetStr(a, "macAddress") == SelectedMac)
                    {
                        activeApp = a;
                        break;
                    }
                }
                if (activeApp == null && _appliances.Count > 0)
                {
                    activeApp = _appliances[0];
                    SelectedMac = GetStr(activeApp, "macAddress");
                }
            }

            if (activeApp != null && IsCloudConnected)
            {
                string macCheck = GetStr(activeApp, "macAddress");
                bool needsProfile;
                lock (_lock)
                {
                    needsProfile = !_capabilitiesByMac.ContainsKey(macCheck);
                }
                if (needsProfile && forceCloudPoll)
                {
                    RefreshApplianceAttributes(activeApp);
                    LoadApplianceCommands(activeApp);
                }
                else if (needsProfile)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        RefreshApplianceAttributes(activeApp);
                        LoadApplianceCommands(activeApp);
                    });
                }
                else if (forceCloudPoll)
                {
                    RefreshApplianceAttributes(activeApp);
                }
            }

            lock (_lock)
            {
                string mac = activeApp != null ? GetStr(activeApp, "macAddress") : "";
                var currentParams = (!string.IsNullOrEmpty(mac) && _parametersByMac.ContainsKey(mac))
                    ? new Dictionary<string, object>(_parametersByMac[mac])
                    : new Dictionary<string, object>();

                var currentCaps = (!string.IsNullOrEmpty(mac) && _capabilitiesByMac.ContainsKey(mac))
                    ? new Dictionary<string, bool>(_capabilitiesByMac[mac])
                    : new Dictionary<string, bool>();

                var currentProfile = (!string.IsNullOrEmpty(mac) && _modelProfileByMac.ContainsKey(mac))
                    ? new Dictionary<string, object>(_modelProfileByMac[mac])
                    : BuildModelProfileSummary(activeApp ?? new Dictionary<string, object>(), currentCaps, 16, 30, 1);

                var applianceSummaries = new List<Dictionary<string, object>>();
                foreach (var a in _appliances)
                {
                    string aMac = GetStr(a, "macAddress");
                    var aProfile = _modelProfileByMac.ContainsKey(aMac) ? _modelProfileByMac[aMac] : null;
                    int supCount = aProfile != null && aProfile.ContainsKey("supportedCount") ? Convert.ToInt32(aProfile["supportedCount"]) : 0;
                    int totCount = aProfile != null && aProfile.ContainsKey("totalFeatures") ? Convert.ToInt32(aProfile["totalFeatures"]) : 15;

                    applianceSummaries.Add(new Dictionary<string, object>
                    {
                        { "macAddress", aMac },
                        { "nickName", GetStr(a, "nickName", GetStr(a, "modelName", "Haier Klima")) },
                        { "modelName", GetStr(a, "modelName", "AC") },
                        { "applianceTypeName", GetStr(a, "applianceTypeName", "AC") },
                        { "series", GetStr(a, "series", "Split AC") },
                        { "fwVersion", GetStr(a, "fwVersion", "-") },
                        { "applianceModelId", GetStr(a, "applianceModelId", "-") },
                        { "supportedCount", supCount },
                        { "totalFeatures", totCount },
                        { "isDemo", a.ContainsKey("isDemo") && Convert.ToBoolean(a["isDemo"]) }
                    });
                }

                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "isCloudConnected", IsCloudConnected },
                    { "currentEmail", CurrentEmail ?? "" },
                    { "selectedMac", mac },
                    { "uiLang", UiLang ?? "tr" },
                    { "uiTheme", UiTheme ?? "dark" },
                    { "appliances", applianceSummaries },
                    { "appliance", activeApp },
                    { "parameters", currentParams },
                    { "capabilities", currentCaps },
                    { "modelProfile", currentProfile }
                };
            }
        }

        public bool ExecuteAcAction(Dictionary<string, object> request, out string responseMessage)
        {
            responseMessage = "";
            Dictionary<string, object> activeApp = null;
            string mac = "";

            lock (_lock)
            {
                if (request != null && request.ContainsKey("macAddress") && request["macAddress"] != null)
                {
                    string reqMac = request["macAddress"].ToString();
                    if (!string.IsNullOrEmpty(reqMac))
                        SelectedMac = reqMac;
                }
                foreach (var a in _appliances)
                {
                    if (GetStr(a, "macAddress") == SelectedMac)
                    {
                        activeApp = a;
                        break;
                    }
                }
                if (activeApp == null && _appliances.Count > 0)
                {
                    activeApp = _appliances[0];
                    SelectedMac = GetStr(activeApp, "macAddress");
                }
                if (activeApp == null)
                {
                    responseMessage = "Aktif klima bulunamadı.";
                    return false;
                }
                mac = GetStr(activeApp, "macAddress");
                if (!_parametersByMac.ContainsKey(mac))
                    _parametersByMac[mac] = new Dictionary<string, object>();
            }

            string commandName = GetStr(request, "commandName", "settings");
            string programName = GetStr(request, "programName", "");
            var updates = new Dictionary<string, string>();

            if (request != null && request.ContainsKey("parameters") && request["parameters"] is Dictionary<string, object>)
            {
                var pDict = (Dictionary<string, object>)request["parameters"];
                foreach (var kv in pDict)
                {
                    if (kv.Value != null)
                        updates[kv.Key] = kv.Value.ToString();
                }
            }

            // Validate against model capabilities
            lock (_lock)
            {
                if (_capabilitiesByMac.ContainsKey(mac))
                {
                    var caps = _capabilitiesByMac[mac];
                    foreach (var kv in updates)
                    {
                        if (caps.ContainsKey(kv.Key) && !caps[kv.Key])
                        {
                            string modelName = GetStr(activeApp, "modelName", "Klima");
                            responseMessage = modelName + " modeli bu donanım özelliğini desteklemiyor.";
                            return false;
                        }
                    }
                }
            }

            Dictionary<string, object> mergedParams;
            lock (_lock)
            {
                if (!_userStickyParamsByMac.ContainsKey(mac))
                    _userStickyParamsByMac[mac] = new Dictionary<string, string>();

                mergedParams = new Dictionary<string, object>(_parametersByMac[mac]);
                string previousOnOff = mergedParams.ContainsKey("onOffStatus") && mergedParams["onOffStatus"] != null
                    ? mergedParams["onOffStatus"].ToString()
                    : "0";

                // Re-apply sticky user preferences (ONLY echoStatus, screenDisplayStatus, lightStatus)
                var stickyOnlyKeys = new HashSet<string>(new string[] { "echoStatus", "screenDisplayStatus", "lightStatus" });
                foreach (var skv in _userStickyParamsByMac[mac])
                {
                    if (stickyOnlyKeys.Contains(skv.Key))
                    {
                        mergedParams[skv.Key] = skv.Value;
                    }
                }

                // Apply the newly requested parameter updates
                foreach (var kv in updates)
                {
                    mergedParams[kv.Key] = kv.Value;
                    if (stickyOnlyKeys.Contains(kv.Key))
                    {
                        _userStickyParamsByMac[mac][kv.Key] = kv.Value;
                    }
                }

                // Enforce mode-based minimum and maximum temperature limits on tempSel
                if (updates.ContainsKey("tempSel"))
                {
                    double reqTemp;
                    if (double.TryParse(updates["tempSel"].Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out reqTemp))
                    {
                        double minAllowed = 16;
                        double maxAllowed = 30;
                        string activeMode = mergedParams.ContainsKey("machMode") && mergedParams["machMode"] != null
                            ? mergedParams["machMode"].ToString()
                            : "1";

                        if (_modelProfileByMac.ContainsKey(mac))
                        {
                            var prof = _modelProfileByMac[mac];
                            if (prof.ContainsKey("tempMin")) minAllowed = Convert.ToDouble(prof["tempMin"]);
                            if (prof.ContainsKey("tempMax")) maxAllowed = Convert.ToDouble(prof["tempMax"]);
                            if (prof.ContainsKey("modeTempLimits") && prof["modeTempLimits"] is Dictionary<string, object>)
                            {
                                var mLimits = (Dictionary<string, object>)prof["modeTempLimits"];
                                if (mLimits.ContainsKey(activeMode) && mLimits[activeMode] is Dictionary<string, object>)
                                {
                                    var mRange = (Dictionary<string, object>)mLimits[activeMode];
                                    if (mRange.ContainsKey("min")) minAllowed = Convert.ToDouble(mRange["min"]);
                                    if (mRange.ContainsKey("max")) maxAllowed = Convert.ToDouble(mRange["max"]);
                                }
                            }
                        }

                        int clampedTemp = (int)Math.Round(Math.Max(minAllowed, Math.Min(maxAllowed, reqTemp)));
                        mergedParams["tempSel"] = clampedTemp.ToString();
                        updates["tempSel"] = clampedTemp.ToString();
                        if (clampedTemp > 10 && mergedParams.ContainsKey("10degreeHeatingStatus") && mergedParams["10degreeHeatingStatus"].ToString() == "1")
                        {
                            mergedParams["10degreeHeatingStatus"] = "0";
                        }
                    }
                }

                // Keep lightStatus synchronized with screenDisplayStatus for models that inspect either key
                if (updates.ContainsKey("screenDisplayStatus"))
                {
                    mergedParams["lightStatus"] = updates["screenDisplayStatus"];
                    _userStickyParamsByMac[mac]["lightStatus"] = updates["screenDisplayStatus"];
                }

                // Mutual exclusion between Turbo (rapidMode) and Quiet (muteStatus)
                if (updates.ContainsKey("rapidMode") && updates["rapidMode"] == "1")
                {
                    mergedParams["muteStatus"] = "0";
                }
                else if (updates.ContainsKey("muteStatus") && updates["muteStatus"] == "1")
                {
                    mergedParams["rapidMode"] = "0";
                }

                if (commandName == "stopProgram")
                {
                    mergedParams["onOffStatus"] = "0";
                }
                else if (commandName == "startProgram")
                {
                    mergedParams["onOffStatus"] = "1";
                    if (!string.IsNullOrEmpty(programName))
                    {
                        string pUpper = programName.ToUpperInvariant();
                        if (pUpper.Contains("COOL")) mergedParams["machMode"] = "1";
                        else if (pUpper.Contains("DRY")) mergedParams["machMode"] = "2";
                        else if (pUpper.Contains("HEAT")) mergedParams["machMode"] = "4";
                        else if (pUpper.Contains("FAN")) mergedParams["machMode"] = "6";
                        else if (pUpper.Contains("AUTO")) mergedParams["machMode"] = "0";
                        else if (pUpper.Contains("10_HEATING"))
                        {
                            mergedParams["machMode"] = "4";
                            mergedParams["10degreeHeatingStatus"] = "1";
                            mergedParams["tempSel"] = "10";
                        }
                        else if (pUpper.Contains("SELF_CLEAN_56"))
                        {
                            mergedParams["selfCleaning56Status"] = "1";
                        }
                        else if (pUpper.Contains("SELF_CLEAN"))
                        {
                            mergedParams["selfCleaningStatus"] = "1";
                        }
                    }
                }
                else
                {
                    if (updates.ContainsKey("onOffStatus") && updates["onOffStatus"] == "0")
                    {
                        commandName = "stopProgram";
                        mergedParams["onOffStatus"] = "0";
                    }
                    else if (updates.ContainsKey("onOffStatus") && updates["onOffStatus"] == "1")
                    {
                        mergedParams["onOffStatus"] = "1";
                    }
                    else if (previousOnOff == "0")
                    {
                        // Do not falsely mark an OFF unit as ON if the user only adjusted settings while the AC is off
                        commandName = "stopProgram";
                        mergedParams["onOffStatus"] = "0";
                    }
                }

                // Optimistically update in-memory state immediately and activate brief 3s in-flight shield
                foreach (var kv in mergedParams)
                {
                    _parametersByMac[mac][kv.Key] = kv.Value;
                }
                _lastCommandSentUtcByMac[mac] = DateTime.UtcNow;
            }

            SaveStickyPrefs();

            string nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var sendParameters = FilterCommandParameters(mergedParams);
            var ancillaryParams = new Dictionary<string, object>
            {
                { "remoteActionable", "1" },
                { "programFamily", "[standard]" },
                { "remoteVisible", "1" }
            };

            var commandPayload = new Dictionary<string, object>
            {
                { "macAddress", mac },
                { "timestamp", nowIso },
                { "commandName", commandName },
                { "transactionId", mac + "_" + nowIso },
                { "applianceOptions", new Dictionary<string, object>() },
                { "device", new Dictionary<string, object> {
                    { "appVersion", APP_VERSION },
                    { "mobileId", _mobileId },
                    { "mobileOs", OS_TYPE },
                    { "osVersion", OS_VERSION },
                    { "deviceModel", DEVICE_MODEL }
                }},
                { "attributes", new Dictionary<string, object> {
                    { "channel", "mobileApp" },
                    { "origin", "standardProgram" },
                    { "energyLabel", "0" }
                }},
                { "ancillaryParameters", ancillaryParams },
                { "parameters", sendParameters },
                { "applianceType", GetStr(activeApp, "applianceTypeName", "AC") }
            };

            if (commandName == "startProgram" && !string.IsNullOrEmpty(programName))
            {
                commandPayload["programName"] = programName.ToUpperInvariant();
            }

            string payloadJson = _json.Serialize(commandPayload);
            bool isDemo = activeApp.ContainsKey("isDemo") && Convert.ToBoolean(activeApp["isDemo"]);

            if (!isDemo && IsCloudConnected)
            {
                string url = API_URL + "/commands/v1/send";
                int status;
                string location;
                string resp = HttpSend("POST", url, payloadJson, GetAuthenticatedHeaders(), true, out status, out location);

                var respObj = _json.Deserialize<Dictionary<string, object>>(resp);
                string resultCode = "";
                if (respObj != null && respObj.ContainsKey("payload"))
                {
                    var pl = respObj["payload"] as Dictionary<string, object>;
                    if (pl != null && pl.ContainsKey("resultCode") && pl["resultCode"] != null)
                        resultCode = pl["resultCode"].ToString();
                }

                if (resultCode != "0")
                {
                    responseMessage = "Haier Bulut servisi komutu reddetti (HTTP " + status + ").";
                    return false;
                }

                lock (_lock)
                {
                    foreach (var kv in sendParameters)
                    {
                        _parametersByMac[mac][kv.Key] = kv.Value;
                    }
                }
                responseMessage = "Ayarlar klimaya iletildi.";
                return true;
            }
            else
            {
                lock (_lock)
                {
                    foreach (var kv in sendParameters)
                    {
                        _parametersByMac[mac][kv.Key] = kv.Value;
                    }
                    SimulateThermalPhysics(_parametersByMac[mac]);
                }

                responseMessage = "Klima ayarları güncellendi.";
                return true;
            }
        }

        private Dictionary<string, object> FilterCommandParameters(Dictionary<string, object> allParams)
        {
            string[] readonlySensors = new string[]
            {
                "tempIndoor", "tempOutdoor", "tempAirOutdoor", "tempInAirOutdoor",
                "tempCoilerIndoor", "tempCoilerOutdoor", "tempDefrostOutdoor",
                "humidityIndoor", "compressorFrequency", "expansionValveOpenDegree",
                "fourWayValveStatus", "airQuality", "pm2p5ValueIndoor", "pm2p5LevelIndoor",
                "vocValueIndoor", "co2ValueIndoor", "ch2oValueIndoor", "errors",
                "totalWorkTime", "fanStatusOutdoor", "defrostStatus", "power",
                "stdTransRate", "highTransRate", "errAckFlag", "sensingResult"
            };
            var excluded = new HashSet<string>(readonlySensors);
            var result = new Dictionary<string, object>();
            foreach (var kv in allParams)
            {
                if (!excluded.Contains(kv.Key))
                {
                    result[kv.Key] = kv.Value != null ? kv.Value.ToString() : "0";
                }
            }
            return result;
        }

        private void SimulateThermalPhysics(Dictionary<string, object> p)
        {
            string onOff = p.ContainsKey("onOffStatus") ? p["onOffStatus"].ToString() : "0";
            if (onOff == "0")
            {
                p["compressorFrequency"] = "0";
                p["expansionValveOpenDegree"] = "0";
                return;
            }

            double targetTemp = 23.0;
            if (p.ContainsKey("tempSel"))
                double.TryParse(p["tempSel"].ToString().Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out targetTemp);

            double indoorTemp = 25.5;
            if (p.ContainsKey("tempIndoor"))
                double.TryParse(p["tempIndoor"].ToString().Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out indoorTemp);

            string mode = p.ContainsKey("machMode") ? p["machMode"].ToString() : "1";
            string rapid = p.ContainsKey("rapidMode") ? p["rapidMode"].ToString() : "0";
            string mute = p.ContainsKey("muteStatus") ? p["muteStatus"].ToString() : "0";

            if (mode == "1")
            {
                p["tempCoilerIndoor"] = Math.Max(8.0, targetTemp - 8.5).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                p["fourWayValveStatus"] = "0";
                p["compressorFrequency"] = rapid == "1" ? "78" : (mute == "1" ? "24" : "48");
            }
            else if (mode == "4")
            {
                p["tempCoilerIndoor"] = Math.Min(46.0, targetTemp + 14.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                p["fourWayValveStatus"] = "2";
                p["compressorFrequency"] = rapid == "1" ? "82" : (mute == "1" ? "28" : "52");
            }
            else if (mode == "2" || mode == "3")
            {
                p["tempCoilerIndoor"] = "16.0";
                p["compressorFrequency"] = "30";
            }
            else if (mode == "5" || mode == "6")
            {
                p["tempCoilerIndoor"] = indoorTemp.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                p["compressorFrequency"] = "0";
            }
            else
            {
                p["compressorFrequency"] = "40";
            }
        }

        private string ManualRedirect(string url)
        {
            int status;
            string location;
            HttpSend("GET", url, null, null, false, out status, out location);
            return !string.IsNullOrEmpty(location) ? location : url;
        }

        private string HttpSend(
            string method,
            string url,
            string body,
            Dictionary<string, string> headers,
            bool allowAutoRedirect,
            out int statusCode,
            out string redirectLocation)
        {
            statusCode = 0;
            redirectLocation = "";

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.CookieContainer = _cookies;
            req.UserAgent = USER_AGENT;
            req.AllowAutoRedirect = allowAutoRedirect;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;

            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        req.ContentType = kv.Value;
                    else if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                        req.UserAgent = kv.Value;
                    else if (string.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase))
                        req.Accept = kv.Value;
                    else
                        req.Headers[kv.Key] = kv.Value;
                }
            }

            if (body != null && (method == "POST" || method == "PUT"))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                if (string.IsNullOrEmpty(req.ContentType))
                    req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bytes, 0, bytes.Length);
                }
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    statusCode = (int)resp.StatusCode;
                    redirectLocation = resp.Headers["Location"] ?? "";
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse errResp = wex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    statusCode = (int)errResp.StatusCode;
                    redirectLocation = errResp.Headers["Location"] ?? "";
                    using (Stream s = errResp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
                throw;
            }
        }

        private static string GetStr(Dictionary<string, object> dict, string key, string defaultVal = "")
        {
            if (dict != null && dict.ContainsKey(key) && dict[key] != null)
                return dict[key].ToString();
            return defaultVal;
        }
    }
}
