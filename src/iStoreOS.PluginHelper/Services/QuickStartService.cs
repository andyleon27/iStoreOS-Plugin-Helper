using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class QuickStartService
    {
        public SoftwareSourceInfo ReadCurrent(ISoftwareSourceSession session, Action<string> log)
        {
            try
            {
                ProbeResult response = session.QuickStartGet(false);
                if (response.ExitStatus != 0) throw new InvalidOperationException("GET 退出码 " + response.ExitStatus);
                return ParseCurrent(response.Output);
            }
            catch (Exception exception) { log("QuickStart 当前源不可读：" + session.Sanitize(exception.Message)); return null; }
        }
        public List<SoftwareSourceInfo> ReadCandidates(ISoftwareSourceSession session, Action<string> log)
        {
            try
            {
                ProbeResult response = session.QuickStartGet(true);
                if (response.ExitStatus != 0) throw new InvalidOperationException("GET 退出码 " + response.ExitStatus);
                return ParseCandidates(response.Output);
            }
            catch (Exception exception) { log("QuickStart 候选源不可读：" + session.Sanitize(exception.Message)); return new List<SoftwareSourceInfo>(); }
        }
        public bool Switch(ISoftwareSourceSession session, string identity, Action<string> log)
        {
            ProbeResult response = session.QuickStartSwitch(identity);
            bool success = response.ExitStatus == 0 && IsSuccessfulPost(response.Output);
            log("QuickStart POST " + identity + "：" + (success ? "接口返回成功" : "接口未确认成功") + "；退出码 " + response.ExitStatus);
            return success;
        }
        public static SoftwareSourceInfo ParseCurrent(string json)
        {
            object value = ReadPayload(json);
            var map = value as IDictionary<string, object>;
            if (map != null && Get(map, "softSource") != null) value = Get(map, "softSource");
            SoftwareSourceInfo source = ParseSource(value);
            if (source == null) throw new ArgumentException("当前源 JSON 缺少有效 URL。");
            return source;
        }
        public static List<SoftwareSourceInfo> ParseCandidates(string json)
        {
            object value = ReadPayload(json);
            var map = value as IDictionary<string, object>;
            if (map != null) value = Get(map, "softSourceList") ?? Get(map, "sources") ?? Get(map, "list");
            var array = value as IList;
            if (array == null) throw new ArgumentException("候选源 JSON 缺少列表。");
            var result = new List<SoftwareSourceInfo>();
            foreach (object item in array)
            {
                SoftwareSourceInfo source = ParseSource(item);
                if (source != null && !string.IsNullOrWhiteSpace(source.Identity) && source.Identity.Length <= 256 &&
                    source.Identity.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0 && !result.Any(s => s.Identity == source.Identity)) result.Add(source);
            }
            return result;
        }
        public static bool IsSuccessfulPost(string json)
        {
            try
            {
                var map = Deserialize(json) as IDictionary<string, object>;
                if (map == null || !object.Equals(Get(map, "success"), true)) return false;
                ReadPayload(json); return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        private static object ReadPayload(string json)
        {
            object value = Deserialize(json);
            var map = value as IDictionary<string, object>;
            if (map == null) return value;
            object error = Get(map, "error");
            if (error != null && !object.Equals(error, false) && !string.IsNullOrEmpty(Convert.ToString(error))) throw new ArgumentException("QuickStart 返回错误。");
            if (object.Equals(Get(map, "success"), false)) throw new ArgumentException("QuickStart 请求失败。");
            object code = Get(map, "code");
            if (code != null && !object.Equals(code, 0) && !object.Equals(code, 200)) throw new ArgumentException("QuickStart 返回非成功状态。");
            return Get(map, "result") ?? Get(map, "data") ?? value;
        }
        private static object Deserialize(string json)
        { return new JavaScriptSerializer { MaxJsonLength = 262144, RecursionLimit = 32 }.DeserializeObject(json ?? ""); }
        private static object Get(IDictionary<string, object> map, string name)
        { foreach (var pair in map) if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value; return null; }
        private static SoftwareSourceInfo ParseSource(object value)
        {
            var map = value as IDictionary<string, object>;
            if (map == null) return null;
            string url = Convert.ToString(Get(map, "url"));
            if (!ShellArgument.IsHttpUrl(url) || !string.IsNullOrEmpty(new Uri(url).Query)) return null;
            return new SoftwareSourceInfo { Identity = Convert.ToString(Get(map, "identity")), Name = Convert.ToString(Get(map, "name")), Url = url.TrimEnd('/') + "/" };
        }
    }
}
