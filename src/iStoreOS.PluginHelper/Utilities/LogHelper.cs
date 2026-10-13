using System;

namespace iStoreOS.PluginHelper.Utilities
{
    public static class LogHelper
    {
        public static string RedactPreservingLines(string text, string secret)
        {
            string result = text ?? string.Empty;
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(secret, "[已隐藏]");
                result = result.Replace(Uri.EscapeDataString(secret), "[已隐藏]");
            }
            return result;
        }

        public static string Redact(string text, string secret)
        {
            string result = text ?? string.Empty;
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(secret, "[已隐藏]");
                result = result.Replace(Uri.EscapeDataString(secret), "[已隐藏]");
            }
            return result.Replace("\r", " ").Replace("\n", " ");
        }

        public static string ExceptionDetails(Exception exception, string secret)
        {
            // Never use Exception.ToString() or serialize connection/authentication objects.
            string details = exception.GetType().Name + ": " + Redact(exception.Message, secret);
            if (exception.InnerException != null)
                details += " | " + ExceptionDetails(exception.InnerException, secret);
            return details.Length > 2000 ? details.Substring(0, 2000) : details;
        }

        public static string Timestamp(string text)
        {
            return "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
        }
    }
}
