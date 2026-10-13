using System;

namespace iStoreOS.PluginHelper.Utilities
{
    public static class ShellArgument
    {
        public static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid shell argument.");
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }
        public static bool IsHttpUrl(string value)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0 &&
                Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
        }
    }
}
