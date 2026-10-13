using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class QuickStartDetectionService
    {
        public bool Detect(IReadOnlyRouterSession session, Action<string> log, CancellationToken cancellationToken)
        {
            RouterProbe[] probes = { RouterProbe.QuickStartSources, RouterProbe.QuickStartCurrentSource };
            foreach (RouterProbe probe in probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ProbeResult result = session.Execute(probe);
                    if (result.ExitStatus == 0 && IsReasonableResponse(result.Output))
                    {
                        log("QuickStart：可用（路由器 loopback GET 返回有效 JSON）。");
                        return true;
                    }
                    log("QuickStart GET " + probe + "：无有效响应；退出码 " + result.ExitStatus + "。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    // Remote output may contain secrets. Log type only; QuickStart is optional.
                    log("QuickStart GET " + probe + "：" + exception.GetType().Name);
                }
            }
            log("QuickStart：不可用（不影响 SSH 连接成功）。");
            return false;
        }

        public static bool IsReasonableResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content) || content.Length > 262144) return false;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 262144, RecursionLimit = 32 };
                object root = serializer.DeserializeObject(content);
                return (root is IDictionary<string, object> || root is IList) && HasPayload(root, 0);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool HasPayload(object value, int depth)
        {
            if (value == null || depth > 16) return false;
            var map = value as IDictionary<string, object>;
            if (map != null)
            {
                if (map.Count == 0) return false;
                object field;
                if (map.TryGetValue("error", out field) && field != null &&
                    !string.IsNullOrEmpty(Convert.ToString(field)) && !object.Equals(field, false)) return false;
                if (map.TryGetValue("success", out field) && object.Equals(field, false)) return false;
                if (map.TryGetValue("code", out field) && !(object.Equals(field, 0) || object.Equals(field, 200))) return false;
                if (map.TryGetValue("status", out field))
                {
                    string status = Convert.ToString(field);
                    if (status == "error" || status == "failed" || status == "failure") return false;
                }
                foreach (string wrapper in new[] { "data", "result", "sources", "list" })
                    if (map.TryGetValue(wrapper, out field)) return HasPayload(field, depth + 1);
                foreach (var entry in map)
                {
                    if (entry.Key == "code" || entry.Key == "status" || entry.Key == "success" ||
                        entry.Key == "message" || entry.Key == "error") continue;
                    if (HasPayload(entry.Value, depth + 1)) return true;
                }
                return false;
            }
            var array = value as IList;
            if (array != null)
            {
                foreach (object entry in array) if (HasPayload(entry, depth + 1)) return true;
                return false;
            }
            return value is string && !string.IsNullOrWhiteSpace((string)value);
        }
    }
}
