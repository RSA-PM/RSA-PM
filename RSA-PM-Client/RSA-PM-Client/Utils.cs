using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RSA_PM_Shared;

namespace RSA_PM_Client
{
    static class Utils
    {
        static string openssl_bin = "openssl";
        static Utils()
        {
            if (System.IO.Directory.Exists("./openssl"))
            {
                openssl_bin = "./openssl/bin/openssl";
            }
        }
        public static byte[] PemToPrivate_NoChecks(string pem)
        {
            var m = Regex.Match(pem, @"^-----BEGIN RSA PRIVATE KEY-----([^-]+)-----END RSA PRIVATE KEY-----$");
            if (m.Success == false) throw new Exception();
            var sz64 = Regex.Replace(m.Groups[1].Value, @"\s+", "");
            var buf = Convert.FromBase64String(sz64);
            return buf;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="pem"></param>
        /// <returns>Returns True if its fine</returns>
        public static bool CheckPem(string pem)
        {
            //sanity check. It does fail sometimes which is why we have a loop checking and regen in the offchance it does
            RSAParameters p;
            Shared.LoadKey2(pem, null, out p);
            using (var rsa = new RSACryptoServiceProvider())
            {
                try
                {
                    rsa.ImportParameters(p);
                }
                catch (CryptographicException ex)
                {
                    if (ex.Message.StartsWith("Bad Data."))
                        return false;
                    throw;
                }
            }
            return true;
        }
        public static byte[] openssl_CreatePrvKey(string key = "1024")
        {
            while (true)
            {
                var process = new Process();
                process.StartInfo.FileName = openssl_bin;
                process.StartInfo.Arguments = @"genrsa " + key.ToString();
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
                process.WaitForExit();
                var res = process.StandardOutput.ReadToEnd();
                if(CheckPem(res))
                    return PemToPrivate_NoChecks(res);
            }
        }
        public static byte[] ExtractPublicKey2(string pubsz)
        {
            var m = Regex.Match(pubsz, @"^-----BEGIN PUBLIC KEY-----([^-]+)-----END PUBLIC KEY-----\s*$");
            if (m.Success == false) throw new Exception();
            var sz64 = Regex.Replace(m.Groups[1].Value, @"\s+", "");
            var buf = Convert.FromBase64String(sz64);
            return buf;
        }

        public class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] left, byte[] right) { return left.ArraysEqual(right); }
            public int GetHashCode(byte[] key)
            {
                if (key == null)
                    throw new ArgumentNullException("key");
                int sum = 0;
                foreach (byte cur in key)
                {
                    sum += cur;
                }
                return sum;
            }
        }

        static Dictionary<byte[], string> keyCache = new Dictionary<byte[], string>(new ByteArrayComparer());
        public static string ExtractPublicKeyAsPem(byte[] prv)
        {
            string sz;
            if (keyCache.TryGetValue(prv, out sz))
            {
                return sz;
            }
            else
            {
                sz = ExtractPublicKeyAsPem_Real(prv);
                keyCache[prv] = sz;
                return sz;
            }
        }
        static string ExtractPublicKeyAsPem_Real(byte[] prv)
        {
            var pemsz = Shared.prvToPem(prv);
            var process = new Process();
            process.StartInfo.FileName = openssl_bin;
            process.StartInfo.Arguments = @"rsa -pubout";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.StandardInput.Write(pemsz);
            process.StandardInput.Close();
            process.WaitForExit();
            var res = process.StandardOutput.ReadToEnd();
            var res2 = process.StandardError.ReadToEnd();
            return res;
        }
        public static byte[] ExtractPublicKey(byte[] prv)
        {
            return ExtractPublicKey2(ExtractPublicKeyAsPem(prv));
        }
        public static bool ArraysEqual<T>(this T[] a1, T[] a2)
        {
            if (ReferenceEquals(a1, a2))
                return true;

            if (a1 == null || a2 == null)
                return false;

            if (a1.Length != a2.Length)
                return false;

            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a1.Length; i++)
            {
                if (!comparer.Equals(a1[i], a2[i])) return false;
            }
            return true;
        }
    }
}
