using System;
using System.Linq;
using System.Text;
using iStoreOS.PluginHelper.Utilities;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public static class MaintenanceMenuCommands
    {
        public const string ProtocolName = "ISTORE_MENU_V1";
        public const int MaximumStartCommandBytes = 256;

        public static string StartCommand(RemoteMaintenanceWorkspace workspace)
        {
            Require(workspace);
            string command = "exec sh " + ShellArgument.Quote(workspace.FilePath(RemoteMaintenanceWorkspace.MenuLauncherFile));
            if (Encoding.UTF8.GetByteCount(command) > MaximumStartCommandBytes) throw new ArgumentException("Menu start command exceeds the PTY bound.");
            return command;
        }
        public static string StartPayload(RemoteMaintenanceWorkspace workspace) { return StartCommand(workspace) + "\n"; }

        public static byte[] LauncherBytes(RemoteMaintenanceWorkspace workspace, Guid runId, MaintenanceNetworkPath? networkPath = null)
        {
            string helpers = Helpers(workspace, runId);
            string body = "#!/bin/sh\numask 077\n" + helpers + @"
owner_ok || exit 76
payload_only || exit 76
for f in ""$pid_file"" ""$start_file"" ""$exit_file"" ""$exit_tmp""; do
 test ! -e ""$f"" && test ! -L ""$f"" || exit 73
done
sh -n ""$menu"" || exit 65
identity=$(process_identity $$) || exit 76
ticks=${identity#*|}
started=$(date +%s) || exit 76
is_uint ""$ticks"" && is_uint ""$started"" || exit 76
# noclobber is the atomic claim: a second launcher cannot overwrite this task's PID.
(set -C; printf '%s\n' $$ > ""$pid_file"") || exit 73
record_started=0
menu_returned=0
interrupted=0
finish() {
 rc=$?
 trap - EXIT HUP INT TERM
 if test ""$record_started"" = 1 && test ""$menu_returned"" = 1 && test ""$interrupted"" = 0 && owner_ok &&
    test ""$(read_small ""$pid_file"")"" = ""$$"" &&
    test ""$(read_small ""$start_file"")"" = ""$wire|START|$$|$ticks|$started""; then
  record=""$wire|EXIT|$$|$ticks|$started|$rc""
  if test ! -e ""$exit_file"" && test ! -L ""$exit_file"" &&
     (set -C; printf '%s\n' ""$record"" > ""$exit_tmp"") &&
     mv -- ""$exit_tmp"" ""$exit_file""; then
   printf '\033]777;%s\007' ""$record""
  fi
 fi
 exit ""$rc""
}
trap finish EXIT
trap 'interrupted=1; exit 129' HUP
trap 'interrupted=1; exit 130' INT
trap 'interrupted=1; exit 143' TERM
record=""$wire|START|$$|$ticks|$started""
(set -C; printf '%s\n' ""$record"" > ""$start_file"") || exit 76
record_started=1
printf '\033]777;%s\007' ""$record""
# Foreground invocation preserves the original menu's stdin/stdout/stderr and bytes.
sh ""$menu""
rc=$?
menu_returned=1
exit ""$rc""
";
            body = ToLf(body);
            if (networkPath.HasValue) {
                if (networkPath != MaintenanceNetworkPath.Direct && networkPath != MaintenanceNetworkPath.ComputerProxy) throw new ArgumentOutOfRangeException("networkPath");
                string address = networkPath == MaintenanceNetworkPath.ComputerProxy ? "http://127.0.0.1:17890" : "";
                string environment = "http_proxy='" + address + "' https_proxy='" + address + "' HTTP_PROXY='" + address + "' HTTPS_PROXY='" + address + "' ALL_PROXY='' all_proxy='' no_proxy='" +
                    (networkPath == MaintenanceNetworkPath.ComputerProxy ? "" : "*") + "' NO_PROXY='" + (networkPath == MaintenanceNetworkPath.ComputerProxy ? "" : "*") + "' ";
                body = body.Replace("\nsh \"$menu\"\n", "\n" + environment + "sh \"$menu\"\n");
            }
            return new UTF8Encoding(false).GetBytes(ToLf(body));
        }

        public static string ReadStateCommand(RemoteMaintenanceWorkspace workspace, Guid runId)
        {
            return ToLf(Helpers(workspace, runId) + @"
load_state || exit 76
case ""$state"" in
 RUNNING) printf '%s|STATE|RUNNING|%s|%s|%s\n' ""$wire"" ""$pid"" ""$ticks"" ""$started"";;
 EXITED) printf '%s|STATE|EXITED|%s|%s|%s|%s\n' ""$wire"" ""$pid"" ""$ticks"" ""$started"" ""$code"";;
 *) printf '%s|STATE|%s\n' ""$wire"" ""$state"";;
esac
");
        }

        public static string CleanupFinishedCommand(RemoteMaintenanceWorkspace workspace, Guid runId)
        {
            string helpers = Helpers(workspace, runId);
            string targets = string.Join(" ", workspace.Files.Concat(workspace.RuntimeFiles).Select(f => ShellArgument.Quote(workspace.OwnedFilePath(f))));
            return ToLf(helpers + @"
load_state || exit 76
test ""$state"" = EXITED || exit 79
# Recheck every file before removing any data or its owner. Never delete unknown entries.
owned_entries || exit 76
test ! -e ""$exit_tmp"" && test ! -L ""$exit_tmp"" || exit 79
" + "rm -f -- " + targets + " " + ShellArgument.Quote(workspace.Directory + "/.owner") + " || exit 1\n" +
                "rmdir " + ShellArgument.Quote(workspace.Directory) + " || exit 1\n" +
                "rmdir " + ShellArgument.Quote(RemoteMaintenanceWorkspace.BaseDirectory) + " 2>/dev/null || :\n" +
                "printf '%s|CLEANED\\n' \"$wire\"\n");
        }

        private static string ToLf(string text) { return text.Replace("\r\n", "\n").Replace('\r', '\n'); }

        private static void Require(RemoteMaintenanceWorkspace workspace)
        {
            if (workspace == null) throw new ArgumentNullException("workspace");
            if (!workspace.Files.Contains(RemoteMaintenanceWorkspace.MenuLauncherFile, StringComparer.Ordinal))
                throw new ArgumentException("Menu runtime requires an explicit launcher workspace.");
        }

        private static string Helpers(RemoteMaintenanceWorkspace workspace, Guid runId)
        {
            Require(workspace);
            if (runId == Guid.Empty) throw new ArgumentException("A unique menu run ID is required.");
            Func<string, string> quote = ShellArgument.Quote;
            string payloads = string.Join("|", workspace.Files.Select(f => quote(workspace.FilePath(f))).Concat(new[] { quote(workspace.Directory + "/.owner") }));
            string owned = string.Join("|", workspace.OwnedFiles.Select(f => quote(workspace.OwnedFilePath(f))).Concat(new[] { quote(workspace.Directory + "/.owner") }));
            return "d=" + quote(workspace.Directory) + "\nmenu=" + quote(workspace.FilePath("menu.sh")) +
                "\nowner=" + quote(workspace.Directory + "/.owner") + "\nbase=" + quote(RemoteMaintenanceWorkspace.BaseDirectory) +
                "\npid_file=" + quote(workspace.OwnedFilePath("menu.pid")) + "\nstart_file=" + quote(workspace.OwnedFilePath("menu.start")) +
                "\nexit_file=" + quote(workspace.OwnedFilePath("menu.exit")) + "\nexit_tmp=" + quote(workspace.OwnedFilePath("menu.exit.tmp")) +
                "\ntask=" + quote(workspace.OperationId.ToString("N")) + "\nrun=" + quote(runId.ToString("N")) +
                "\nwire=" + quote(ProtocolName + "|" + workspace.OperationId.ToString("N") + "|" + runId.ToString("N")) + "\n" + @"
is_uint() {
 case ""$1"" in ''|*[!0-9]*|0[0-9]*) return 1;; esac
 test ""${#1}"" -le 19 && test ""$1"" -ge 0 2>/dev/null
}
is_pid() { is_uint ""$1"" && test ""$1"" -gt 0 && test ""$1"" -le 2147483647; }
read_small() (
 test -f ""$1"" && test ! -L ""$1"" || exit 1
 size=$(wc -c < ""$1"") || exit 1
 test ""$size"" -le 512 || exit 1
 cat ""$1""
)
owner_ok() {
 test ! -L ""$base"" && test ! -L ""$d"" &&
 test ""$(read_small ""$owner"")"" = ""$task""
}
process_identity() (
 is_pid ""$1"" || exit 1
 line=$(cat ""/proc/$1/stat"" 2>/dev/null) || exit 1
 # Remove the full parenthesized comm field, including spaces or parentheses.
 rest=${line##*) }
 test ""$rest"" != ""$line"" || exit 1
 set -f
 set -- $rest
 test ""$#"" -ge 20 || exit 1
 state_char=$1
 shift 19
 is_uint ""$1"" || exit 1
 printf '%s|%s' ""$state_char"" ""$1""
)
payload_only() {
 for f in ""$d""/* ""$d""/.[!.]* ""$d""/..?*; do
  test -e ""$f"" || test -L ""$f"" || continue
  test -f ""$f"" && test ! -L ""$f"" || return 1
  case ""$f"" in " + payloads + @") ;; *) return 1;; esac
 done
}
owned_entries() {
 for f in ""$d""/* ""$d""/.[!.]* ""$d""/..?*; do
  test -e ""$f"" || test -L ""$f"" || continue
  test -f ""$f"" && test ! -L ""$f"" || return 1
  case ""$f"" in " + owned + @") ;; *) return 1;; esac
 done
}
load_state() {
 state=UNKNOWN
 owner_ok || return 76
 if test ! -e ""$pid_file"" && test ! -L ""$pid_file"" &&
    test ! -e ""$start_file"" && test ! -L ""$start_file"" &&
    test ! -e ""$exit_file"" && test ! -L ""$exit_file"" &&
    test ! -e ""$exit_tmp"" && test ! -L ""$exit_tmp""; then state=NOT_STARTED; return 0; fi
 if test -e ""$exit_tmp"" || test -L ""$exit_tmp""; then return 0; fi
 pid=$(read_small ""$pid_file"") || return 0
 is_pid ""$pid"" || return 0
 start_record=$(read_small ""$start_file"") || return 0
 IFS='|' read -r tag start_task start_run kind record_pid ticks started extra <<ISTORE_RECORD
$start_record
ISTORE_RECORD
 test ""$tag|$start_task|$start_run|$kind"" = ""$wire|START"" &&
 test ""$record_pid"" = ""$pid"" && is_uint ""$ticks"" && is_uint ""$started"" &&
 test -z ""$extra"" && test ""$start_record"" = ""$wire|START|$pid|$ticks|$started"" || return 0
 has_exit=0
 if test -e ""$exit_file"" || test -L ""$exit_file""; then
  exit_record=$(read_small ""$exit_file"") || return 0
  IFS='|' read -r tag exit_task exit_run kind exit_pid exit_ticks exit_started code extra <<ISTORE_RECORD
$exit_record
ISTORE_RECORD
  test ""$tag|$exit_task|$exit_run|$kind"" = ""$wire|EXIT"" &&
  test ""$exit_pid|$exit_ticks|$exit_started"" = ""$pid|$ticks|$started"" &&
  is_uint ""$code"" && test ""$code"" -le 255 && test -z ""$extra"" &&
  test ""$exit_record"" = ""$wire|EXIT|$pid|$ticks|$started|$code"" || return 0
  has_exit=1
 fi
 # A missing /proc mount or unreadable process metadata is not proof that a PID died.
 process_identity $$ >/dev/null || return 0
 if test -d ""/proc/$pid""; then
  current=$(process_identity ""$pid"") || return 0
  current_ticks=${current#*|}
  current_state=${current%%|*}
  if test ""$current_ticks"" = ""$ticks"" && test ""$current_state"" != Z; then state=RUNNING; return 0; fi
 fi
 if test ""$has_exit"" = 1; then state=EXITED; fi
}
";
        }
    }
}

