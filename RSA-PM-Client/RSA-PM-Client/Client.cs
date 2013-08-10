using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ionic.BZip2;
using RSA_PM_Shared;

namespace RSA_PM_Client
{
    public class Client
    {
        static string SHA512OID = "2.16.840.1.101.3.4.2.3";//https://developer.mozilla.org/en-US/docs/Cert_override.txt
        public class MyException : Exception { public MyException(string msg) : base(msg) { } }
        TcpClient c = new TcpClient();
        BinaryReader sr;
        BinaryWriter sw;
        NetworkStream s;
        public Client(string server_addr, Int16 server_port, string proxy_addr, Int16 proxy_port, RSAParameters server_pubkey)
        {
            if (!String.IsNullOrEmpty(proxy_addr))
                s = SOCKS.ConnectSocksProxy(proxy_addr, proxy_port, server_addr, server_port, c);
            else
            {
                c.Connect(server_addr, server_port);
                s = c.GetStream();
            }
            sr = new BinaryReader(s);
            sw = new BinaryWriter(s);
            if (Shared.TestRemoteOnPubkey(sr, sw, server_pubkey) == false)
            {
                throw new MyException("Server Failed Test.");
            }
        }
        void SendPublicKey(byte[] pubkey)
        {
            sw.Write(BitConverter.GetBytes(pubkey.Length), 0, 2);
            sw.Write(pubkey, 0, pubkey.Length);
            sw.Flush();
            var resp = sr.ReadByte();
            if (resp != (byte)ClientServerCmd.KeyLenOk)
            {
                if (resp == (byte)ClientServerCmd.LengthToBig)
                    throw new MyException("The key is to big");
                throw new Exception();
            }
        }

        public void RegisterPublicKey(byte[] prvkey, byte[] pubkey)
        {
            sw.WriteByte((byte)ClientServerCmd.RegisterPubkey);
            SendPublicKey(pubkey);

            RSAParameters rsap;
            Shared.LoadKey2(Shared.prvToPem(prvkey), null, out rsap);
            Shared.AnswerRemotePubkeyTest(sr, sw, rsap);

            var resp = sr.ReadByte();
            if (resp != (byte)ClientServerCmd.RegisterPubkeyOk && resp != (byte)ClientServerCmd.RegisterPubkeyAlreadyReg)
                throw new Exception();
        }
        public void UnregisterPublicKey(byte[] prvkey, byte[] pubkey)
        {
            s.WriteByte((byte)ClientServerCmd.UnregisterPubkey);
            SendPublicKey(pubkey);

            RSAParameters rsap;
            Shared.LoadKey2(Shared.prvToPem(prvkey), null, out rsap);
            Shared.AnswerRemotePubkeyTest(sr, sw, rsap);

            var resp = s.ReadByte();
            if (resp != (byte)ClientServerCmd.Success)
                throw new Exception();
        }

        byte[] EncodeMessage(byte[] recipient_pubkey, byte[] msgid, byte[] replyTo, string txt, byte[] prvkey, byte[] pubkey, out byte[] aes_key, out byte[] aes_iv)
        {
            if (replyTo == null)
                replyTo = new byte[16];

            var txtbuf = Encoding.UTF8.GetBytes(txt);
            var SignMessage = prvkey != null;
            byte[] hash = null;
            if (SignMessage)
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    RSAParameters rsap;
                    Shared.LoadKey2(Shared.prvToPem(prvkey), null, out rsap);
                    rsa.ImportParameters(rsap);
                    using (var ms = new MemoryStream()) //sign
                    {
                        ms.Write(msgid, 0, msgid.Length);
                        ms.Write(replyTo, 0, replyTo.Length);
                        ms.WriteShort((short)txtbuf.Length);
                        ms.Write(txtbuf, 0, txtbuf.Length);
                        ms.WriteShort((short)pubkey.Length);
                        ms.Write(pubkey, 0, pubkey.Length);
                        ms.WriteShort((short)recipient_pubkey.Length);
                        ms.Write(recipient_pubkey, 0, recipient_pubkey.Length);
                        ms.Position = 0;
                        hash = rsa.SignData(ms, SHA512OID);
                    }
                }
            }

            byte[] c1;
            using (var ms1 = new MemoryStream())
            using (var ms = new BZip2OutputStream(ms1))
            {
                ms.Write(txtbuf, 0, txtbuf.Length);
                ms.Close();
                c1 = ms1.ToArray();
            }

            var compressText = c1.Length < txtbuf.Length;

            byte[] aesmsg;
            using (var aes = new RijndaelManaged())
            {
                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (var encryptor = aes.CreateEncryptor())
                    using (CryptoStream sw2 = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        aes_key = aes.Key;
                        aes_iv = aes.IV;
                        sw2.WriteByte((Byte)((compressText ? 1 : 0) | (SignMessage ? 2 : 0)));
                        sw2.Write(msgid, 0, msgid.Length);
                        sw2.Write(replyTo, 0, replyTo.Length);
                        if (compressText)
                        {
                            sw2.WriteShort((short)c1.Length);
                            sw2.Write(c1, 0, c1.Length);
                        }
                        else
                        {
                            sw2.WriteShort((short)txtbuf.Length);
                            sw2.Write(txtbuf, 0, txtbuf.Length);
                        }
                        if (SignMessage)
                        {
                            sw2.WriteShort((short)pubkey.Length);
                            sw2.Write(pubkey, 0, pubkey.Length);
                            sw2.WriteShort((short)hash.Length);
                            sw2.Write(hash, 0, hash.Length);
                        }
                    }
                    msEncrypt.Flush();
                    aesmsg = msEncrypt.ToArray();
                }
            }
            return aesmsg;
        }
        public byte[] SendMessage(byte[] recipient_pubkey, byte[] replyTo, string txt, byte[] prvkey, byte[] pubkey)
        {
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            var msgid = new byte[16];
            rng.GetBytes(msgid);

            byte[] rsa_data, aeskey, aesIV;

            var aesmsg = EncodeMessage(recipient_pubkey, msgid, replyTo, txt, prvkey, pubkey, out aeskey, out aesIV);

            RSAParameters recipient_rsap;
            Shared.LoadKey2(Shared.pubToPem(recipient_pubkey), null, out recipient_rsap);
            using (var rsa = new RSACryptoServiceProvider())
            using (var ms = new MemoryStream())
            {
                rsa.ImportParameters(recipient_rsap);
                ms.Write(aeskey, 0, aeskey.Length);
                ms.Write(aesIV, 0, aesIV.Length);
                rsa_data = rsa.Encrypt(ms.ToArray(), false);
            }
            if (rsa_data.Length != 128) throw new Exception();
            if (rsa_data.Length + aesmsg.Length > 1024 * 7) throw new Exception();

            sw.WriteByte((byte)ClientServerCmd.SendMessage);
            sw.WriteShort((short)recipient_pubkey.Length);
            sw.Write(recipient_pubkey, 0, recipient_pubkey.Length);
            sw.WriteShort(rsa_data.Length + aesmsg.Length);
            sw.Write(rsa_data, 0, rsa_data.Length);
            sw.Write(aesmsg, 0, aesmsg.Length);
            sw.Flush();

            var resp = sr.ReadByte();
            if (resp != (byte)ClientServerCmd.KeyLenOk)
                throw new Exception();
            resp = sr.ReadByte();
            if (resp == (byte)ClientServerCmd.NotRegistered)
                throw new MyException("User you're writing to does not exist");
            if (resp != (byte)ClientServerCmd.Success)
                throw new Exception();
            
            return msgid;
        }
        public int GetMessage(byte[] prvkey, byte[] pubkey, Action<Message>store)
        {
            int count = 0;
            sw.WriteByte((byte)ClientServerCmd.GetMessage);
            SendPublicKey(pubkey);

            RSAParameters rsap;
            Shared.LoadKey2(Shared.prvToPem(prvkey), null, out rsap);
            Shared.AnswerRemotePubkeyTest(sr, sw, rsap);

            while (true)
            {
                var resp = sr.ReadByte();
                if (resp == (byte)ClientServerCmd.NoMessage)
                    return count;
                else if (resp != (byte)ClientServerCmd.HasMessage)
                    throw new Exception();
                var len = sr.ReadShort();
                if (len > 1024)
                    throw new Exception();
                var buf = new Byte[len];
                //sr.Read(buf, 0, 8);
                //var ts = BitConverter.ToInt64(buf, 0);
                //DateTime.FromFileTimeUtc(ts);
                if (sr.Read(buf, 0, len) != len)
                    throw new Exception();

                byte[] aeskey,aesIV,aesbuf;
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(rsap);
                    var rsalen = 128;
                    var rsabuf = buf.Take(rsalen).ToArray();
                    aesbuf = buf.Skip(rsalen).ToArray();
                    var aes_keyivdata = rsa.Decrypt(rsabuf, false);
                    aeskey = aes_keyivdata.Take(32).ToArray();
                    aesIV = aes_keyivdata.Skip(32).Take(16).ToArray();
                }
                var msg=DecodeMessage(aesbuf,aeskey, aesIV, rsap, pubkey);
                store(msg);
                count++;
                sw.WriteByte((Byte)ClientServerCmd.Success);
            }
        }
        Message DecodeMessage(byte[] aesbuf, byte[] aeskey, byte[] aesIV, RSAParameters rsap, byte[] mypub)
        {
            using (MemoryStream ms2 = new MemoryStream())
            using (var aes = new RijndaelManaged())
            {
                using (var dec = aes.CreateDecryptor(aeskey, aesIV))
                using (MemoryStream ms = new MemoryStream(aesbuf))
                using (CryptoStream sr2 = new CryptoStream(ms, dec, CryptoStreamMode.Read))
                {
                    byte[] buf = new byte[1024 * 4];
                    int l;
                    while ((l = sr2.Read(buf, 0, buf.Length)) != 0)
                    {
                        ms2.Write(buf, 0, l);
                    }
                }
                ms2.Flush();
                var arr = ms2.ToArray();
                ms2.Position = 0;
                var msg = DecodeMessage2(ms2, rsap, mypub);
                msg.unencrypted = arr;
                return msg;
            }
        }
        static public Message DecodeMessage2(Stream sr2, RSAParameters rsaprv, byte[] mypub)
        {
            Message msg = new Message();

            if(false)
            {
                var ms4 = new MemoryStream();
                var buf = new byte[4096];
                int len2;
                while((len2=sr2.Read(buf, 0, buf.Length))>0){
                    ms4.Write(buf, 0, len2);
                }
                var data = ms4.ToArray();
                System.Diagnostics.Trace.WriteLine(BitConverter.ToString(data));
                System.Diagnostics.Trace.WriteLine("Pub: " + BitConverter.ToString(mypub));
                sr2 = new MemoryStream(data);
            }

            var flags = sr2.ReadByte();
            var compressText = (flags & 1) != 0;
            var SignMessage = (flags & 2) != 0;

            sr2.Read(msg.msgid, 0, msg.msgid.Length);
            sr2.Read(msg.replyTo, 0, msg.replyTo.Length);

            var len = sr2.ReadShort();
            var szbuf = new byte[len];
            sr2.Read(szbuf, 0, szbuf.Length);
            if (compressText == false)
            {
                msg.msg = Encoding.UTF8.GetString(szbuf);
            }
            else
            {
                var b = new Byte[1024 * 4];
                using (var dec_sr = new BZip2InputStream(new MemoryStream(szbuf)))
                using (var ms2 = new MemoryStream())
                {
                    var readlen = 0;
                    while ((readlen = dec_sr.Read(b, 0, b.Length)) > 0)
                    {
                        ms2.Write(b, 0, readlen);
                    }
                    ms2.Flush();
                    msg.msg = Encoding.UTF8.GetString(ms2.ToArray());
                }
            }
            if (SignMessage)
            {
                var theirkeylen = sr2.ReadShort();
                var theirpubkey = new Byte[theirkeylen];
                sr2.Read(theirpubkey, 0, theirpubkey.Length);
                var hashlen = sr2.ReadShort();
                var hash = new Byte[hashlen];
                sr2.Read(hash, 0, hash.Length);

                //System.Diagnostics.Trace.WriteLine("Hash: " +BitConverter.ToString(hash));

                using (var ms2 = new MemoryStream())
                using (var rsa = new RSACryptoServiceProvider())
                {
                    ms2.Write(msg.msgid, 0, msg.msgid.Length);
                    ms2.Write(msg.replyTo, 0, msg.replyTo.Length);
                    var msgarr = Encoding.UTF8.GetBytes(msg.msg);
                    ms2.WriteShort(msgarr.Length);
                    ms2.Write(msgarr, 0, msgarr.Length);
                    ms2.WriteShort(theirpubkey.Length);
                    ms2.Write(theirpubkey, 0, theirpubkey.Length);
                    ms2.WriteShort(mypub.Length);
                    ms2.Write(mypub, 0, mypub.Length);
                    RSAParameters rsap2;
                    Shared.LoadKey2(Shared.pubToPem(theirpubkey), null, out rsap2);
                    rsa.ImportParameters(rsap2);
                    var b = rsa.VerifyData(ms2.ToArray(), SHA512OID, hash);
                    if (b)
                        msg.signed = true;
                    else
                        msg.forged = true;
                }
                msg.their_pubkey = theirpubkey;
            }

            return msg;
        }
        public class Message
        {
            public string msg;
            public bool signed, forged;
            //unencrypted is so we don't have to serialize for the database.
            public byte[] their_pubkey, msgid = new byte[16], replyTo = new byte[16], unencrypted;
            public long MyPrvId;
            public override string ToString()
            {
                if(forged||signed)
                    return Form1.GetNamePublicId(their_pubkey, true);
                else
                    return @"Anonymous/Unsigned";
            }
        }
    }
}
