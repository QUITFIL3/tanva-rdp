using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Tanva {
    public sealed class Profile {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string Group { get; set; }
        public bool Pinned { get; set; }
        public string Display { get; set; }
        public bool Clipboard { get; set; }
        public long LastLaunch { get; set; }

        public Profile() {
            Id = Guid.NewGuid().ToString("N"); Name = Host = User = Password = Group = "";
            Port = 3389; Display = "full";
        }
        public Profile Copy() { return (Profile)MemberwiseClone(); }
        [ScriptIgnore] public string Endpoint {
            get { return (Host.Contains(":") ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture); }
        }
        [ScriptIgnore] public string GroupLabel { get { return String.IsNullOrEmpty(Group) ? "ไม่มีกลุ่ม" : Group; } }
        [ScriptIgnore] public string PinMark { get { return Pinned ? "★" : ""; } }
        [ScriptIgnore] public string When {
            get { return LastLaunch == 0 ? "ยังไม่เคยเปิด" : new DateTime(LastLaunch, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yy · HH:mm", CultureInfo.GetCultureInfo("th-TH")); }
        }

        static bool SingleLine(string s, int max) { return s != null && s.Length <= max && !s.Any(Char.IsControl); }
        public void Validate() {
            Guid id;
            if (!Guid.TryParseExact(Id, "N", out id)) throw new InvalidDataException("รหัสโปรไฟล์ไม่ถูกต้อง");
            if (!SingleLine(Name, 100) || String.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("กรุณาระบุชื่อเครื่อง (สูงสุด 100 ตัวอักษร)");
            if (!SingleLine(Host, 253) || Host.Length == 0) throw new InvalidDataException("กรุณาระบุชื่อโฮสต์หรือ IP");
            IPAddress ip;
            if (IPAddress.TryParse(Host, out ip)) Host = ip.ToString();
            else if (!Regex.IsMatch(Host, @"\A(?=.{1,253}\z)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.?\z"))
                throw new InvalidDataException("ระบุชื่อโฮสต์หรือ IP และใส่พอร์ตในช่องแยก");
            if (Port < 1 || Port > 65535) throw new InvalidDataException("พอร์ตต้องอยู่ระหว่าง 1 ถึง 65535");
            if (!SingleLine(User, 256) || !SingleLine(Group, 100)) throw new InvalidDataException("ชื่อผู้ใช้หรือกลุ่มยาวเกินไป หรือมีการขึ้นบรรทัดใหม่");
            if (Password == null || Password.Length > 512 || Password.IndexOf('\0') >= 0) throw new InvalidDataException("รหัสผ่านไม่ถูกต้องหรือยาวเกิน 512 ตัวอักษร");
            if (Password.Length > 0 && String.IsNullOrWhiteSpace(User)) throw new InvalidDataException("กรุณาระบุชื่อผู้ใช้สำหรับรหัสผ่านนี้");
            if (Display != "full" && Display != "window" && Display != "multi") throw new InvalidDataException("รูปแบบจอภาพไม่ถูกต้อง");
            if (LastLaunch < 0 || LastLaunch > DateTime.MaxValue.Ticks) throw new InvalidDataException("เวลาเปิดโปรไฟล์ไม่ถูกต้อง");
        }
    }

    public sealed class Library {
        public int Version { get; set; }
        public List<Profile> Profiles { get; set; }
    }

    public static class Storage {
        public const int Limit = 16 * 1024 * 1024;
        public static List<Profile> Missing(List<Profile> existing, List<Profile> incoming) {
            var ids = new HashSet<string>(existing.Select(p=>p.Id),StringComparer.OrdinalIgnoreCase);
            return incoming.Where(p=>ids.Add(p.Id)).ToList();
        }
        static JavaScriptSerializer Codec() { return new JavaScriptSerializer { MaxJsonLength = Limit }; }
        public static byte[] Encode(List<Profile> items) {
            var bytes = Encoding.UTF8.GetBytes(Codec().Serialize(new Library { Version=1, Profiles=items }));
            if (bytes.Length > Limit - 65536) throw new InvalidDataException("ข้อมูลเกินขนาดที่รองรับ");
            return bytes;
        }
        public static List<Profile> Decode(byte[] data) {
            var lib = Codec().Deserialize<Library>(Encoding.UTF8.GetString(data));
            if (lib == null || lib.Version != 1 || lib.Profiles == null) throw new InvalidDataException("รูปแบบคลังไม่รองรับ");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in lib.Profiles) {
                if (p == null || !ids.Add(p.Id)) throw new InvalidDataException("พบโปรไฟล์ไม่ถูกต้องหรือซ้ำกันในไฟล์");
                p.Validate();
            }
            return lib.Profiles;
        }
        public static byte[] Read(string file) {
            using (var stream = File.OpenRead(file)) {
                if (stream.Length > Limit) throw new InvalidDataException("ไฟล์ใหญ่เกิน 16 MiB");
                using (var reader = new BinaryReader(stream)) return reader.ReadBytes((int)stream.Length);
            }
        }
        public static void Write(string file, byte[] data) {
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(data,0,data.Length); stream.Flush(true);
                }
                if (File.Exists(file)) File.Replace(temp,file,null);
                else File.Move(temp,file);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static List<Profile> Load(string file) {
            if (!File.Exists(file)) return new List<Profile>();
            var bytes = ProtectedData.Unprotect(Read(file),null,DataProtectionScope.CurrentUser);
            try { return Decode(bytes); } finally { Array.Clear(bytes,0,bytes.Length); }
        }
        public static void Save(string file, List<Profile> items) {
            var bytes = Encode(items);
            try { Write(file,ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(bytes,0,bytes.Length); }
        }
    }

    public static class RemoteDesktop {
        public static string Settings(Profile p) {
            p.Validate();
            var s = new StringBuilder();
            s.AppendLine("full address:s:" + p.Endpoint);
            s.AppendLine("username:s:" + p.User);
            s.AppendLine("screen mode id:i:" + (p.Display == "window" ? "1" : "2"));
            if (p.Display == "window") s.AppendLine("desktopwidth:i:1280\r\ndesktopheight:i:720\r\nsmart sizing:i:1");
            s.AppendLine("use multimon:i:" + (p.Display == "multi" ? "1" : "0"));
            s.AppendLine("redirectclipboard:i:" + (p.Clipboard ? "1" : "0"));
            s.AppendLine("redirectprinters:i:0\r\ndrivestoredirect:s:\r\nauthentication level:i:2\r\nenablecredsspsupport:i:1");
            if (p.Password.Length > 0) {
                byte[] raw = Encoding.Unicode.GetBytes(p.Password);
                try { s.AppendLine("password 51:b:" + BitConverter.ToString(ProtectedData.Protect(raw,null,DataProtectionScope.CurrentUser)).Replace("-","")); }
                finally { Array.Clear(raw,0,raw.Length); }
            }
            return s.ToString();
        }
        public static void Launch(Profile p, string dir) {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".rdp");
            try {
                File.WriteAllText(file, Settings(p), Encoding.Unicode);
                var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"mstsc.exe"), "\"" + file + "\"") { UseShellExecute=false } };
                process.Exited += delegate { TryRemove(file); process.Dispose(); };
                process.EnableRaisingEvents = true;
                process.Start();
            } catch { TryRemove(file); throw; }
        }
        public static void TryRemove(string file) {
            try { File.Delete(file); } catch (IOException) {} catch (UnauthorizedAccessException) {}
        }
    }
}
