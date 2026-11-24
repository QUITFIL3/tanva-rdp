using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Tanva {
    public static class Backup {
        // Version 1: magic | 16-byte salt | 16-byte IV | CBC ciphertext | SHA-256 MAC.
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("TANVAB01");
        const int Header = 40;
        const int Tag = 32;

        [DllImport("bcrypt.dll", CharSet=CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr provider, string algorithm, string implementation, uint flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDeriveKeyPBKDF2(IntPtr provider, byte[] password, int passwordSize, byte[] salt, int saltSize, ulong rounds, [Out] byte[] key, int keySize, uint flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptCloseAlgorithmProvider(IntPtr provider, uint flags);

        static byte[] Derive(string password, byte[] salt) {
            IntPtr provider;
            if (BCryptOpenAlgorithmProvider(out provider, "SHA256", null, 8) != 0) throw new CryptographicException("Windows CNG ไม่พร้อมใช้งาน");
            var raw = Encoding.UTF8.GetBytes(password);
            try {
                var key = new byte[64];
                if (BCryptDeriveKeyPBKDF2(provider,raw,raw.Length,salt,salt.Length,600000,key,key.Length,0) != 0)
                    throw new CryptographicException("สร้างกุญแจเข้ารหัสไม่สำเร็จ");
                return key;
            } finally { Array.Clear(raw,0,raw.Length); BCryptCloseAlgorithmProvider(provider,0); }
        }
        static byte[] Slice(byte[] input, int start, int count) {
            var part = new byte[count]; Buffer.BlockCopy(input,start,part,0,count); return part;
        }
        static byte[] Mac(byte[] key, byte[] input, int count) {
            using (var mac = new HMACSHA256(Slice(key,32,32))) return mac.ComputeHash(input,0,count);
        }
        public static byte[] Seal(List<Profile> profiles, string password) {
            if (String.IsNullOrEmpty(password) || password.Length < 12) throw new ArgumentException("รหัสผ่านสำรองต้องมีอย่างน้อย 12 ตัวอักษร");
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            var key = Derive(password,salt);
            var data = Storage.Encode(profiles);
            try {
                using (var aes = Aes.Create()) {
                    aes.Key = Slice(key,0,32); aes.GenerateIV();
                    using (var cipher = aes.CreateEncryptor())
                    using (var buffer = new MemoryStream()) {
                        var encrypted = cipher.TransformFinalBlock(data,0,data.Length);
                        buffer.Write(Magic,0,Magic.Length); buffer.Write(salt,0,16); buffer.Write(aes.IV,0,16);
                        buffer.Write(encrypted,0,encrypted.Length);
                        var tag = Mac(key,buffer.GetBuffer(),(int)buffer.Length);
                        buffer.Write(tag,0,tag.Length); return buffer.ToArray();
                    }
                }
            } finally { Array.Clear(data,0,data.Length); Array.Clear(key,0,key.Length); }
        }
        public static List<Profile> Open(byte[] input, string password) {
            if (input.Length < Header + 16 + Tag || input.Length > Storage.Limit || (input.Length - Header - Tag) % 16 != 0 || !input.Take(Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("ไฟล์นี้ไม่ใช่ไฟล์สำรอง tanva-rdp รุ่นที่รองรับ");
            var key = Derive(password,Slice(input,8,16));
            try {
                var tag = Mac(key,input,input.Length-Tag);
                int difference = 0;
                for (int i=0;i<Tag;i++) difference |= tag[i] ^ input[input.Length-Tag+i];
                if (difference != 0) throw new InvalidDataException("รหัสผ่านสำรองไม่ถูกต้อง หรือไฟล์ถูกแก้ไข");
                using (var aes = Aes.Create()) {
                    aes.Key = Slice(key,0,32); aes.IV = Slice(input,24,16);
                    using (var cipher = aes.CreateDecryptor()) {
                        var data = cipher.TransformFinalBlock(input,Header,input.Length-Header-Tag);
                        try { return Storage.Decode(data); } finally { Array.Clear(data,0,data.Length); }
                    }
                }
            } finally { Array.Clear(key,0,key.Length); }
        }
    }
}
