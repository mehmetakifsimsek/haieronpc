using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HaierHonDesktop
{
    public class LocalWebServer
    {
        private readonly HonCloudClient _client;
        private readonly string _uiHtmlPath;
        private readonly string _embeddedHtml;
        private readonly JavaScriptSerializer _json;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        public int Port { get; private set; }
        public string BaseUrl { get { return "http://127.0.0.1:" + Port; } }

        public LocalWebServer(HonCloudClient client, string uiHtmlPath, int preferredPort = 5890)
        {
            _client = client;
            _uiHtmlPath = uiHtmlPath;
            _embeddedHtml = ReadEmbeddedUiHtml();
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Port = preferredPort;
        }

        private static string ReadEmbeddedUiHtml()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("HaierHonDesktop.ui.index.html"))
                {
                    if (s != null)
                    {
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private string LoadUiHtml()
        {
            try
            {
                if (!string.IsNullOrEmpty(_uiHtmlPath) && File.Exists(_uiHtmlPath))
                {
                    return File.ReadAllText(_uiHtmlPath, Encoding.UTF8);
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(_embeddedHtml))
            {
                return _embeddedHtml;
            }

            return "<h1>UI file not found</h1>";
        }

        public void Start()
        {
            for (int p = Port; p < Port + 25; p++)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, p);
                    _listener.Start();
                    Port = p;
                    _running = true;
                    break;
                }
                catch
                {
                    _listener = null;
                }
            }

            if (!_running || _listener == null)
                throw new InvalidOperationException("Yerel sunucu portu açılamadı.");

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "HonLocalWebServer"
            };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient tcp = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(tcp));
                }
                catch
                {
                    if (!_running) break;
                }
            }
        }

        private void HandleClient(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    tcp.ReceiveTimeout = 10000;
                    tcp.SendTimeout = 10000;
                    using (NetworkStream stream = tcp.GetStream())
                    {
                        string method;
                        string rawPath;
                        string body;
                        if (!ReadHttpRequest(stream, out method, out rawPath, out body))
                            return;

                        string path = rawPath;
                        string query = "";
                        int qIdx = rawPath.IndexOf('?');
                        if (qIdx >= 0)
                        {
                            path = rawPath.Substring(0, qIdx);
                            query = rawPath.Substring(qIdx + 1);
                        }

                        if (method == "OPTIONS")
                        {
                            WriteResponse(stream, 200, "text/plain; charset=utf-8", "");
                            return;
                        }

                        if (path == "/" || path == "/index.html")
                        {
                            string html = LoadUiHtml();
                            WriteResponse(stream, 200, "text/html; charset=utf-8", html);
                            return;
                        }

                        if (path == "/api/state")
                        {
                            bool poll = query.Contains("poll=1") || query.Contains("force=1");
                            var state = _client.GetStateSnapshot(poll);
                            WriteResponse(stream, 200, "application/json; charset=utf-8", _json.Serialize(state));
                            return;
                        }

                        if (path == "/api/login" && method == "POST")
                        {
                            var req = _json.Deserialize<Dictionary<string, object>>(body);
                            string email = req != null && req.ContainsKey("email") && req["email"] != null ? req["email"].ToString() : "";
                            string password = req != null && req.ContainsKey("password") && req["password"] != null ? req["password"].ToString() : "";
                            string msg;
                            bool ok = _client.LoginWithCredentials(email, password, out msg);
                            var resp = _client.GetStateSnapshot(false);
                            resp["ok"] = ok;
                            resp["message"] = msg;
                            WriteResponse(stream, ok ? 200 : 400, "application/json; charset=utf-8", _json.Serialize(resp));
                            return;
                        }

                        if (path == "/api/logout" && method == "POST")
                        {
                            _client.LogoutAndUseDemo();
                            var resp = _client.GetStateSnapshot(false);
                            resp["ok"] = true;
                            resp["message"] = "Demo klima moduna geçildi.";
                            WriteResponse(stream, 200, "application/json; charset=utf-8", _json.Serialize(resp));
                            return;
                        }

                        if (path == "/api/command" && method == "POST")
                        {
                            var req = _json.Deserialize<Dictionary<string, object>>(body);
                            string msg;
                            bool ok = _client.ExecuteAcAction(req, out msg);
                            var resp = _client.GetStateSnapshot(false);
                            resp["ok"] = ok;
                            resp["message"] = msg;
                            WriteResponse(stream, ok ? 200 : 400, "application/json; charset=utf-8", _json.Serialize(resp));
                            return;
                        }

                        if (path == "/api/select-appliance" && method == "POST")
                        {
                            var req = _json.Deserialize<Dictionary<string, object>>(body);
                            if (req != null && req.ContainsKey("macAddress") && req["macAddress"] != null)
                            {
                                _client.SelectedMac = req["macAddress"].ToString();
                            }
                            var resp = _client.GetStateSnapshot(true);
                            WriteResponse(stream, 200, "application/json; charset=utf-8", _json.Serialize(resp));
                            return;
                        }

                        if (path == "/api/ui-prefs" && method == "POST")
                        {
                            var req = _json.Deserialize<Dictionary<string, object>>(body);
                            string lang = req != null && req.ContainsKey("lang") && req["lang"] != null ? req["lang"].ToString() : null;
                            string theme = req != null && req.ContainsKey("theme") && req["theme"] != null ? req["theme"].ToString() : null;
                            _client.SetUiPreferences(lang, theme);
                            var resp = _client.GetStateSnapshot(false);
                            WriteResponse(stream, 200, "application/json; charset=utf-8", _json.Serialize(resp));
                            return;
                        }

                        WriteResponse(stream, 404, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"Not found\"}");
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        using (NetworkStream stream = tcp.GetStream())
                        {
                            string errJson = _json.Serialize(new Dictionary<string, object>
                            {
                                { "ok", false },
                                { "message", ex.Message }
                            });
                            WriteResponse(stream, 500, "application/json; charset=utf-8", errJson);
                        }
                    }
                    catch { }
                }
            }
        }

        private static bool ReadHttpRequest(NetworkStream stream, out string method, out string path, out string body)
        {
            method = "GET";
            path = "/";
            body = "";

            List<byte> headerBytes = new List<byte>();
            byte[] buf = new byte[1];
            int matchState = 0;

            while (headerBytes.Count < 32768)
            {
                int n = stream.Read(buf, 0, 1);
                if (n <= 0) break;
                byte b = buf[0];
                headerBytes.Add(b);

                if ((matchState == 0 || matchState == 2) && b == (byte)'\r')
                    matchState++;
                else if ((matchState == 1 || matchState == 3) && b == (byte)'\n')
                    matchState++;
                else
                    matchState = 0;

                if (matchState == 4)
                    break;
            }

            if (headerBytes.Count == 0)
                return false;

            string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return false;

            string[] firstLine = lines[0].Split(' ');
            if (firstLine.Length >= 2)
            {
                method = firstLine[0].Trim().ToUpperInvariant();
                path = firstLine[1].Trim();
            }

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) break;
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string hName = line.Substring(0, colon).Trim();
                    string hVal = line.Substring(colon + 1).Trim();
                    if (string.Equals(hName, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(hVal, out contentLength);
                    }
                }
            }

            if (contentLength > 0 && contentLength < 4 * 1024 * 1024)
            {
                byte[] bodyBuf = new byte[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    int r = stream.Read(bodyBuf, totalRead, contentLength - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }
                body = Encoding.UTF8.GetString(bodyBuf, 0, totalRead);
            }

            return true;
        }

        private static void WriteResponse(NetworkStream stream, int statusCode, string contentType, string content)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(content ?? "");
            string statusText = statusCode == 200 ? "OK" : (statusCode == 400 ? "Bad Request" : (statusCode == 404 ? "Not Found" : "Internal Server Error"));
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("HTTP/1.1 {0} {1}\r\n", statusCode, statusText);
            sb.AppendFormat("Content-Type: {0}\r\n", contentType);
            sb.AppendFormat("Content-Length: {0}\r\n", bodyBytes.Length);
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            sb.Append("Cache-Control: no-store, no-cache, must-revalidate\r\n");
            sb.Append("Connection: close\r\n\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0)
            {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }
            stream.Flush();
        }
    }
}
