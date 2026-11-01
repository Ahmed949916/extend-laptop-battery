using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PowerDial
{
    internal sealed class AppEndTarget : IDisposable
    {
        internal SafeProcessHandle Handle;
        internal int Id;
        internal string Name, Path;
        public void Dispose() { if (Handle != null) Handle.Dispose(); }
    }

    internal sealed class AppEndGroup
    {
        internal string Name;
        internal string DisplayName { get { return AppEnding.DisplayName(Name); } }
        internal readonly List<AppEndTarget> Targets = new List<AppEndTarget>();
    }

    /// <summary>On-demand, opt-in process ending. Retained kernel handles prevent PID-reuse mistakes.</summary>
    internal sealed class AppEnding : IDisposable
    {
        static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "chrome", "msedge", "firefox", "brave", "opera", "Discord", "slack", "Spotify", "Zoom",
            "Teams", "ms-teams", "Telegram", "WhatsApp", "WhatsApp.Root", "Dropbox", "OneDrive", "steam", "EpicGamesLauncher",
            "vivaldi", "waterfox", "librewolf", "thunderbird", "Signal", "Viber", "DiscordCanary", "DiscordPTB",
            "Amazon Music", "AppleMusic", "iTunes", "vlc", "foobar2000", "Plex", "TIDAL",
            "Battle.net", "EADesktop", "UbisoftConnect", "GalaxyClient", "GOG Galaxy", "qBittorrent",
            "GoogleDriveFS", "MEGAsync", "pCloud", "obs64", "ShareX"
        };
        internal readonly List<AppEndGroup> Groups = new List<AppEndGroup>();
        internal string Problem;
        readonly string _user;
        readonly int _session;

        internal static string DisplayName(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "msedge": return "Microsoft Edge";
                case "chrome": return "Google Chrome";
                case "firefox": return "Mozilla Firefox";
                case "brave": return "Brave Browser";
                case "opera": return "Opera Browser";
                case "discord": return "Discord";
                case "slack": return "Slack";
                case "spotify": return "Spotify";
                case "zoom": return "Zoom";
                case "teams": case "ms-teams": return "Microsoft Teams";
                case "telegram": return "Telegram";
                case "whatsapp": case "whatsapp.root": return "WhatsApp";
                case "vivaldi": return "Vivaldi";
                case "waterfox": return "Waterfox";
                case "librewolf": return "LibreWolf";
                case "thunderbird": return "Mozilla Thunderbird";
                case "signal": return "Signal";
                case "viber": return "Viber";
                case "discordcanary": return "Discord Canary";
                case "discordptb": return "Discord PTB";
                case "amazon music": return "Amazon Music";
                case "applemusic": return "Apple Music";
                case "itunes": return "iTunes";
                case "vlc": return "VLC media player";
                case "foobar2000": return "foobar2000";
                case "plex": return "Plex";
                case "tidal": return "TIDAL";
                case "battle.net": return "Battle.net";
                case "eadesktop": return "EA app";
                case "ubisoftconnect": return "Ubisoft Connect";
                case "galaxyclient": case "gog galaxy": return "GOG Galaxy";
                case "qbittorrent": return "qBittorrent";
                case "googledrivefs": return "Google Drive";
                case "megasync": return "MEGA";
                case "pcloud": return "pCloud";
                case "obs64": return "OBS Studio";
                case "sharex": return "ShareX";
                case "dropbox": return "Dropbox";
                case "onedrive": return "Microsoft OneDrive";
                case "steam": return "Steam";
                case "epicgameslauncher": return "Epic Games Launcher";
                default: return name ?? "Unknown app";
            }
        }

        internal AppEnding(List<ProcInfo> observations)
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) _user = identity.User.Value;
            using (Process current = Process.GetCurrentProcess()) _session = current.SessionId;
            try
            {
                HashSet<int> services = ServiceIds();
                foreach (ProcInfo observed in observations)
                {
                    if (!observed.Background || observed.Self || !Supported.Contains(observed.Name)) continue;
                    AppEndGroup group = new AppEndGroup { Name = observed.Name };
                    Groups.Add(group);
                    foreach (Process process in Process.GetProcessesByName(observed.Name))
                        using (process)
                        {
                            AppEndTarget target = Capture(process.Id, services);
                            if (target != null) group.Targets.Add(target);
                        }
                    if (group.Targets.Count == 0) Groups.Remove(group);
                }
                Groups.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
            }
            catch (Exception ex) { Problem = "App checks could not be completed: " + ex.Message; Dispose(); Groups.Clear(); }
        }

        static HashSet<int> ServiceIds()
        {
            HashSet<int> ids = new HashSet<int>();
            using (ManagementObjectSearcher query = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service"))
            using (ManagementObjectCollection results = query.Get())
                foreach (ManagementObject item in results)
                    using (item) ids.Add(Convert.ToInt32(item["ProcessId"]));
            return ids;
        }

        internal AppEndTarget Capture(int pid, HashSet<int> services)
        {
            if (pid <= 4 || pid == Environment.ProcessId || services.Contains(pid)) return null;
            SafeProcessHandle handle = OpenProcess(0x1000 | 0x0001 | 0x00100000, false, pid);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            try
            {
                string path;
                if (!Validate(handle, out path)) { handle.Dispose(); return null; }
                return new AppEndTarget { Handle = handle, Id = pid, Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            }
            catch { handle.Dispose(); return null; }
        }

        bool Validate(SafeProcessHandle handle, out string path)
        {
            path = null;
            if (WaitForSingleObject(handle, 0) != 258 || !IsProcessCritical(handle, out bool critical) || critical) return false;
            char[] image = new char[32768];
            int size = image.Length;
            if (!QueryFullProcessImageName(handle, 0, image, ref size)) return false;
            path = new string(image, 0, size);
            if (!OpenProcessToken(handle, 8, out SafeAccessTokenHandle token)) return false;
            using (token)
            using (WindowsIdentity owner = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (!GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) ||
                    !GetTokenInformation(token, 12, out int session, sizeof(int), out _)) return false;
                return Eligible(path, owner.User.Value, _user, session, _session, elevated != 0, critical,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            }
        }

        // A conservative allowlist is deliberate: an unfamiliar helper is not offered as safe to end.
        internal static bool Eligible(string path, string owner, string user, int session, int currentSession,
                                      bool elevated, bool critical, string windows)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(user) ||
                owner != user || session <= 0 || session != currentSession || elevated || critical ||
                string.IsNullOrEmpty(windows) || !System.IO.Path.IsPathFullyQualified(path)) return false;
            if (path.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            return Supported.Contains(System.IO.Path.GetFileNameWithoutExtension(path));
        }

        internal string End(AppEndGroup group)
        {
            if (!Groups.Contains(group)) return "No app was selected from this review.";
            HashSet<int> services;
            try { services = ServiceIds(); }
            catch (Exception ex) { return "Nothing ended: service checks failed. " + ex.Message; }
            int ended = 0, exited = 0, skipped = 0, failed = 0;
            foreach (AppEndTarget target in group.Targets)
            {
                try
                {
                    if (WaitForSingleObject(target.Handle, 0) == 0) { exited++; continue; }
                    if (services.Contains(target.Id) || !Validate(target.Handle, out string path) ||
                        !string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                    if (!TerminateProcess(target.Handle, 1)) { failed++; continue; }
                    if (WaitForSingleObject(target.Handle, 1000) == 0) ended++;
                    else failed++;
                }
                catch (Exception) { failed++; }
            }
            return group.DisplayName + ": " + ended + " ended, " + exited + " already exited, " + skipped +
                " excluded by safety checks, " + failed + " failed or exit not confirmed. Apps may restart themselves.";
        }

        public void Dispose()
        {
            foreach (AppEndGroup group in Groups) foreach (AppEndTarget target in group.Targets) target.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, [Out] char[] name, ref int size);
        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool OpenProcessToken(SafeProcessHandle process, int access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out int value, int length, out int needed);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }
}
