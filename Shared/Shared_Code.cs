using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.OpenSsl;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RSA_PM_Shared
{
    public enum ClientServerCmd
    {
        KeyLenOk = 0x4C,
        Success,
        RegisterPubkey = 0x50,
        RegisterPubkeyOk, RegisterPubkeyAlreadyReg, RegisterPubkeyError,
        UnregisterPubkey,
        SendMessage, GetMessage, HasMessage, NoMessage,
        //error
        LengthToBig = 0x87, KeyToBig, Error, NotRegistered
    }
    public static class Shared
    {
        public static byte[] FixedIV_16bytes = new byte[16] { 220, 209, 44, 59, 27, 69, 216, 25, 71, 95, 33, 27, 11, 47, 45, 240 };
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

        //cause i'm too lazy to use binaryreader everywhere although i should...
        public static short ReadShort(this Stream s)
        {
            var buf = new byte[2];
            if (s.Read(buf, 0, 2) != 2)
                throw new Exception();
            return BitConverter.ToInt16(buf, 0);
        }
        public static int ReadInt(this Stream s)
        {
            var buf = new byte[4];
            if (s.Read(buf, 0, 4) != 4)
                throw new Exception();
            return BitConverter.ToInt32(buf, 0);
        }
        public static byte[] ReadArray(this Stream s, int len)
        {
            var buf = new byte[len];
            int l = 0, off = 0;
            while (len != 0 && (l = s.Read(buf, off, len)) != 0)
            {
                off += l;
                len -= l;
            }
            return buf;
        }
        public static short ReadShort(this BinaryReader s)
        {
            var buf = new byte[2];
            if (s.Read(buf, 0, 2) != 2)
                throw new Exception();
            return BitConverter.ToInt16(buf, 0);
        }
        public static void WriteByte(this BinaryWriter s, byte v) { s.Write(v); }
        public static void WriteShort(this BinaryWriter s, int v) { s.Write((short)v); }
        public static void WriteShort(this Stream s, int v) { WriteShort(s, (short)v); }
        public static void WriteShort(this Stream s, short v)
        {
            s.Write(BitConverter.GetBytes(v), 0, 2);
        }
        public static void WriteInt(this Stream s, int v)
        {
            s.Write(BitConverter.GetBytes(v), 0, 4);
        }


        public static bool LoadKey(string fn, IPasswordFinder pw, out RSAParameters p)
        {
            var sr = new StreamReader(fn);
            return LoadKey(sr, pw, out p);
        }
        public static bool LoadKey2(string pemString, IPasswordFinder pw, out RSAParameters p)
        {
            return LoadKey(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(pemString))), pw, out p);
        }

        public static bool LoadKey(StreamReader s, IPasswordFinder pw, out RSAParameters p)
        {
            PemReader pr = new PemReader(s, pw);
            var o = pr.ReadObject();
            if (o is RsaKeyParameters)
            {
                var oo = (RsaKeyParameters)o;
                var dsl = oo.IsPrivate;
                p = DotNetUtilities.ToRSAParameters(oo);
                return false;
            }
            else if (o is AsymmetricCipherKeyPair)
            {
                AsymmetricCipherKeyPair KeyPair = (AsymmetricCipherKeyPair)o;
                p = DotNetUtilities.ToRSAParameters((RsaPrivateCrtKeyParameters)KeyPair.Private);
                return true;
            }
            else
            {
                throw new Exception("I don't know how to load this as a key");
            }
        }
        static public string pubToPem(byte[] pub)
        {
            return (string.Format("-----BEGIN PUBLIC KEY-----\n{0}\n-----END PUBLIC KEY-----\n",
                Regex.Replace(Convert.ToBase64String(pub), "(.{64})", "$1\n")).Replace("\n\n", "\n"));
        }
        static public string prvToPem(byte[] prv)
        {
            return (string.Format("-----BEGIN RSA PRIVATE KEY-----\n{0}\n-----END RSA PRIVATE KEY-----\n",
                Regex.Replace(Convert.ToBase64String(prv), "(.{64})", "$1\n")).Replace("\n\n", "\n"));
        }
        public static bool TestRemoteOnPubkey(BinaryReader sr, BinaryWriter sw, RSAParameters pub)
        {
            byte[] buf = new byte[16];
            var rng = new Random();
            rng.NextBytes(buf);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(pub);
                var res = rsa.Encrypt(buf, false);
                sw.Write(BitConverter.GetBytes((short)res.Length), 0, 2);
                sw.Write(res, 0, res.Length);
                sw.Flush();
                int status = sr.ReadByte();
                if (status != (int)ClientServerCmd.KeyLenOk)
                    return false;

                var readlen = sr.Read(res, 0, buf.Length);
                return buf.ArraysEqual(res.Take(readlen).ToArray());
            }
        }
        public static void AnswerRemotePubkeyTest(BinaryReader sr, BinaryWriter sw, RSAParameters prvkey)
        {
            byte[] buf = new byte[1024 * 4];
            var readlen = sr.Read(buf, 0, 2);
            if (readlen != 2)
                throw new Exception();

            var len = BitConverter.ToInt16(buf, 0);
            if (len > buf.Length)
            {
                sw.WriteByte((byte)ClientServerCmd.LengthToBig);
                sw.Flush();
                sw.Close();
                throw new Exception();
            }
            readlen = sr.Read(buf, 0, len);
            if (readlen != len)
                throw new Exception();
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(prvkey);
                var res = rsa.Decrypt(buf.Take(readlen).ToArray(), false);
                sw.WriteByte((byte)ClientServerCmd.KeyLenOk);
                sw.Write(res, 0, res.Length);
            }
        }
    }
    class PasswordFinder : IPasswordFinder
    {
        char[] pw;
        public PasswordFinder(char[] pw1) { pw = pw1; }

        public char[] GetPassword() { return pw; }
    }
}
