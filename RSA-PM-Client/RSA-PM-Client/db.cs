using System;
using System.Collections.Generic;
#if USE_MONO
using Mono.Data.Sqlite;
using SQLiteConnection = Mono.Data.Sqlite.SqliteConnection;
#else
using System.Data.SQLite;
#endif
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using RSA_PM_Client;
using RSA_PM_Shared;

namespace RSA_PM_Client
{
    class DB
    {
        public static string filename = "RSA-PM.sqlite3";
        public static DB db;
        SQLiteConnection conn;
        public DB() { conn = MakeConn(); db = this; }
        RijndaelManaged aes = new RijndaelManaged() { Mode = CipherMode.CFB };
        TripleDESCryptoServiceProvider des = new TripleDESCryptoServiceProvider() { Padding = PaddingMode.None, Mode = CipherMode.CFB };
        List<Private_Keys> prvkeys = new List<Private_Keys>();
        public List<Public_Keys> pubkeys = new List<Public_Keys>();

        byte[] salt1 = new byte[] { 0x5F, 0x27, 0x74, 0x99, 0x47, 0x6D, 0xEA, 0xC2, 0x89, 0x12, 0x21, 0x45, 0x01 };
        byte[] enc(KeyValuePair<string, string> v) { return enc(v.Value, v.Key); }
        byte[] enc(string txt, string ivsz)
        {
            var pbkdf2 = new Rfc2898DeriveBytes(ivsz, salt1);
            aes.IV = pbkdf2.GetBytes(aes.IV.Length);
            using (var m = new MemoryStream())
            {
                using (var c = aes.CreateEncryptor())
                using (var s = new CryptoStream(m, c, CryptoStreamMode.Write))
                {
                    var buf = Encoding.UTF8.GetBytes(txt);
                    s.Write(buf, 0, buf.Length);
                }
                m.Flush();
                return m.ToArray();
            }
        }
        long enc(long v, byte[] iv)
        {
            using (var m = new MemoryStream())
            {
                using (var c = des.CreateEncryptor(des.Key, iv))
                using (var s = new CryptoStream(m, c, CryptoStreamMode.Write))
                {
                    var b = BitConverter.GetBytes(v);
                    s.Write(b, 0, b.Length);
                }
                m.Flush();
                return BitConverter.ToInt64(m.ToArray(), 0);
            }
        }
        byte[] enc(string txt, byte[] iv) { return enc(Encoding.UTF8.GetBytes(txt), iv); }
        byte[] enc(byte[] data, byte[] iv)
        {
            using (var m = new MemoryStream())
            {
                using (var c = aes.CreateEncryptor(aes.Key, iv))
                using (var s = new CryptoStream(m, c, CryptoStreamMode.Write))
                {
                    s.Write(data, 0, data.Length);
                }
                m.Flush();
                return m.ToArray();
            }
        }

        long dec(long v, byte[] iv)
        {
            using (var m = new MemoryStream())
            {
                using (var c = des.CreateDecryptor(des.Key, iv))
                using (var s = new CryptoStream(new MemoryStream(BitConverter.GetBytes(v)), c, CryptoStreamMode.Read))
                {
                    var buf = new byte[1024];
                    var l = 0;
                    while ((l = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        m.Write(buf, 0, l);
                    }
                }
                m.Flush();
                var arr = m.ToArray();
                if (arr.Length != 8)
                    throw new Exception();
                return BitConverter.ToInt64(arr, 0);
            }
        }

        string dec(Info v) { return dec(v.v, v.k); }
        string dec_sz(byte[] txt, byte[] iv) { return Encoding.UTF8.GetString(dec(txt, iv)); }
        string dec(byte[] buf_in, string ivsz)
        {
            var pbkdf2 = new Rfc2898DeriveBytes(ivsz, salt1);
            aes.IV = pbkdf2.GetBytes(aes.IV.Length);
            using (var m = new MemoryStream())
            {
                using (var c = aes.CreateDecryptor())
                using (var s = new CryptoStream(new MemoryStream(buf_in), c, CryptoStreamMode.Read))
                {
                    var buf = new byte[1024];
                    var l = 0;
                    while ((l = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        m.Write(buf, 0, l);
                    }
                }
                m.Flush();
                return Encoding.UTF8.GetString(m.ToArray());
            }
        }
        byte[] dec(byte[] buf_in, byte[] iv)
        {
            using (var m = new MemoryStream())
            {
                using (var c = aes.CreateDecryptor(aes.Key, iv))
                using (var s = new CryptoStream(new MemoryStream(buf_in), c, CryptoStreamMode.Read))
                {
                    var buf = new byte[1024];
                    var l = 0;
                    while ((l = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        m.Write(buf, 0, l);
                    }
                }
                m.Flush();
                return m.ToArray();
            }
        }
        public Dictionary<string, byte[]> LoadInfo()
        {
            var d = new Dictionary<string, byte[]>();
            foreach (var v in conn.Query<Info>("select * from Info", new { }))
            {
                d[v.k] = v.v;
            }
            return d;
        }
        void LoadAESKey(string pw, byte[] pwaes)
        {
            using (var aes2 = new RijndaelManaged())
            {
                var pbkdf2 = new Rfc2898DeriveBytes(pw, salt1);
                aes2.Key = pbkdf2.GetBytes(aes2.Key.Length);//should be 256bits which is the current largest
                aes2.IV = pbkdf2.GetBytes(aes2.IV.Length);
                using (var m = new MemoryStream())
                {
                    using (var c = aes2.CreateDecryptor())
                    using (var s = new CryptoStream(new MemoryStream(pwaes), c, CryptoStreamMode.Read))
                    {
                        if (aes.Key.Length != 32 && des.Key.Length != 24)
                            throw new Exception();
                        var buf = new byte[1024];
                        var l = 0;
                        while ((l = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            m.Write(buf, 0, l);
                        }
                    }
                    m.Flush();
                    var arr = m.ToArray();
                    aes.Key = arr.Take(aes.Key.Length).ToArray();
                    des.Key = arr.Skip(aes.Key.Length).Take(des.Key.Length).ToArray();
                }
            }
        }
        bool IsLoaded_ = false;
        public bool IsLoaded { get { return IsLoaded_; } }
        public bool Load(string pw)
        {
            var d= LoadInfo();
            if (d["version"][0] != (byte)'0')
                throw new Exception();

            LoadAESKey(pw, d["pw"]);
            IsLoaded_ = true;
            return true;
        }
        public void Setup(string pw, string pemfn, int pemopt, string sockproxy, int proxyport, string server_url, int server_port, byte[] server_pub, RSAParameters rsa_prv, bool useRsa)
        {
            var tblExist = conn.Query<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='info';", new { }).First()>0;
            
            using (var tns = conn.BeginTransaction())
            {
                conn.Execute(@"create table if not exists info(id INTEGER PRIMARY KEY, k TEXT UNIQUE NOT NULL, v BLOB NOT NULL);", new { });
                conn.Execute(@"create table if not exists info2(id INTEGER PRIMARY KEY, k TEXT UNIQUE NOT NULL, v BLOB NOT NULL);", new { });
                conn.Execute(@"create table if not exists prvkeys(id INTEGER PRIMARY KEY, key BLOB NOT NULL, name BLOB NOT NULL, last_date INTEGER NOT NULL);", new { });
                conn.Execute(@"create table if not exists pubkeys(id INTEGER PRIMARY KEY, key BLOB NOT NULL, name BLOB NOT NULL);", new { });
                conn.Execute(@"create table if not exists inbox(id INTEGER PRIMARY KEY, msg BLOB);", new { });
                conn.Execute(@"create table if not exists outbox(id INTEGER PRIMARY KEY, msg BLOB);", new { });
                Dictionary<string, string> d = new Dictionary<string, string>();
                conn.Execute(@"delete from info", new { });
                if (pw != null)
                {
                    byte[] pwaeskey = null;
                    using (var aes2 = new RijndaelManaged())
                    {
                        var pbkdf2 = new Rfc2898DeriveBytes(pw, salt1);
                        aes2.Key = pbkdf2.GetBytes(aes2.Key.Length);//should be 256bits which is the current largest
                        aes2.IV = pbkdf2.GetBytes(aes2.IV.Length);
                        using (var m = new MemoryStream())
                        {
                            using (var c = aes2.CreateEncryptor())
                            using (var s = new CryptoStream(m, c, CryptoStreamMode.Write))
                            {
                                if (aes.Key.Length != 32 && des.Key.Length != 24)
                                    throw new Exception();
                                s.Write(aes.Key, 0, aes.Key.Length);
                                s.Write(des.Key, 0, des.Key.Length);
                            }
                            m.Flush();
                            pwaeskey = m.ToArray();
                        }
                    }
                    conn.Execute("replace into info(k,v) select @k, @v", new { k = "pw", v = pwaeskey });
                }
                
                if (string.IsNullOrEmpty(pemfn)==false)
                {
                    if (pemopt == 1)
                    {
                        conn.Execute(@"REPLACE into info(k,v) select ""pempath"", @v", new { v = pemfn });
                    }
                    using (var rsa = new RSACryptoServiceProvider())
                    {
                        rsa.ImportParameters(rsa_prv);
                        byte[] bufin;
                        using (var ms = new MemoryStream())
                        {
                            if (aes.Key.Length != 32 && des.Key.Length != 24)
                                throw new Exception();
                            ms.Write(aes.Key, 0, aes.Key.Length);
                            ms.Write(des.Key, 0, des.Key.Length);
                            ms.Flush();
                            bufin = ms.ToArray();
                        }
                        var res = rsa.Encrypt(bufin, false);
                        conn.Execute(@"REPLACE into info(k,v) select ""pem_data"", @v", new { v = res });
                    }
                }

                conn.Execute(@"insert into info(k,v) select ""version"", ""0""", new { });
                d.Add("proxy_addr", sockproxy == null ? "" : sockproxy);
                d.Add("proxy_port", proxyport.ToString());
                d.Add("server_addr", server_url);
                d.Add("server_port", server_port.ToString());
                d.Add("server_pubkey", Convert.ToBase64String(server_pub));

                foreach (var v in d)
                {
                    conn.Execute("REPLACE into info2(k,v) select @k, @v", new { k = v.Key, v = enc(v) });
                }
                tns.Commit();
            }
        }
        static SQLiteConnection MakeConn()
        {
            var conn = new SQLiteConnection(string.Format("data source={0};version=3;", filename));
            conn.Open();
            conn.Execute("PRAGMA foreign_keys=ON", new { });
            return conn;
        }
        byte[] prvkey_iv = new byte[16] { 238, 2, 128, 65, 57, 197, 48, 173, 67, 118, 49, 100, 89, 106, 1, 115 };
        byte[] pubkey_iv = new byte[16] { 55, 117, 222, 226, 104, 228, 20, 229, 186, 192, 239, 193, 172, 15, 44, 142 };
        byte[] inbox_iv = new byte[16] { 72, 255, 115, 38, 112, 27, 66, 199, 41, 211, 150, 188, 207, 32, 197, 14 };
        byte[] outbox_iv = new byte[16] { 125, 133, 73, 148, 108, 74, 88, 157, 141, 18, 91, 75, 34, 51, 122, 252 };

        byte[] ArrayrXor(byte[] arr_orig, long v)
        {
            byte[] arr = (byte[])arr_orig.Clone();
            var varr = BitConverter.GetBytes(v);
            for (int i = 0; i < varr.Length; ++i)
            {
                arr[i] ^= varr[i];
            }
            return arr;
        }
        public bool newprv(byte[] prv, string name, Client client)
        {
            foreach (var v in GetPrivateKeys())
            {
                if (prv.ArraysEqual(v.key))
                {
                    return false;//already exist
                }
            }

            var id1 = conn.Query<long>("select id from prvkeys order by id desc limit 1", new { });
            long id = id1.Count() == 0 ? 1 : id1.First() + 1;
            var iv = ArrayrXor(prvkey_iv, id);
            var obj = new { id = id, k = enc(prv, iv), n = enc(name, iv), d = enc(0, iv) };
            client.RegisterPublicKey(prv, Utils.ExtractPublicKey(prv));
            conn.Execute("insert into prvkeys(id, key, name, last_date) select @id, @k, @n, @d", obj);
            prvkeys.Add(new Private_Keys() { id = id, key = prv, name = name, visit_date = 0 });
            return true;
        }
        public Private_Keys[] GetPrivateKeys()
        {
            var keys = conn.Query<Private_KeysDB>("select * from prvkeys where id", new { });
            prvkeys = keys.Select(v => { var iv = ArrayrXor(prvkey_iv, v.id); return new Private_Keys() { id = v.id, key = dec(v.key, iv), name = dec_sz(v.name, iv), visit_date = dec(v.last_date, iv) }; }).ToList();
            return prvkeys.ToArray();
        }
        public Public_Keys[] GetPubKeys()
        {
            var keys = conn.Query<Public_KeysDB>("select * from pubkeys where id", new { });
            pubkeys = keys.Select(v => { var iv = ArrayrXor(prvkey_iv, v.id); return new Public_Keys() { id = v.id, key = dec(v.key, iv), name = dec_sz(v.name, iv) }; }).ToList();
            return pubkeys.ToArray();
        }
        public void renamePrv(long id, string name)
        {
            var iv = ArrayrXor(prvkey_iv, id);
            var res = conn.Execute("update prvkeys set name=@name where id=@id", new { id = id, name = enc(name, iv) });
            if (res != 1)
                throw new Exception();
        }
        public void getMsgs(long id)
        {
            var keys = conn.Query<Private_KeysDB>("select * from prvkeys", new { });
            //return keys.Select(v => { var iv = ArrayrXor(prvkey_iv, v.id); return new Private_Keys() { id = v.id, key = dec(v.key, iv), name = dec_sz(v.name, iv), visit_date = dec(v.last_date, iv) }; }).ToArray();
        }
        public Dictionary<string, string> GetSettings()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (var v in conn.Query<Info>("select * from Info2", new { }))
            {
                d[v.k] = dec(v);
            }
            return d;
        }
        public long newpub(byte[] pubkey, string name)
        {
            var id1 = conn.Query<long>("select id from pubkeys order by id desc limit 1", new { });
            long id = id1.Count() == 0 ? 1 : id1.First() + 1;
            var iv = ArrayrXor(prvkey_iv, id);
            var obj = new { id = id, k = enc(pubkey, iv), n = enc(name, iv) };
            conn.Execute("insert into pubkeys(id, key, name) select @id, @k, @n", obj);
            pubkeys.Add(new Public_Keys() { id = id, key = pubkey, name = name });
            return id;
        }
        public long SentMessage(string their_name, byte[] their_pubkey, string txt, byte[] myprv, byte[] msgid, byte[]replyTo)
        {
            if (replyTo == null)
                replyTo = new byte[16];
            long foundId = 0, prvId=0;
            foreach (var v in pubkeys)
            {
                if(their_pubkey.ArraysEqual(v.key))
                {
                    foundId=v.id;
                    break;
                }
            }
            if (myprv != null)
            {
                foreach (var v in prvkeys)
                {
                    if (myprv.ArraysEqual(v.key))
                    {
                        prvId = v.id;
                        break;
                    }
                }
                if (prvId == 0)
                    throw new Exception();
            }
            
            if (foundId == 0) { foundId = newpub(their_pubkey, their_name); }

            byte[] msgbuf;
            using (var ms = new MemoryStream())
            {
                ms.WriteInt((int)foundId);
                ms.WriteInt((int)prvId);
                ms.Write(msgid, 0, msgid.Length);
                ms.Write(replyTo, 0, replyTo.Length);
                var txtbuf = Encoding.UTF8.GetBytes(txt);
                ms.Write(txtbuf, 0, txtbuf.Length);
                ms.Flush();
                msgbuf = ms.ToArray();
            }

            var idq = conn.Query<long>("select id from outbox order by id desc limit 1",new{});
            long id = idq.Count() == 0 ? 1 : idq.First() + 1;
            var iv = ArrayrXor(outbox_iv, id);
            if (conn.Execute("insert into outbox(id, msg) select @id, @msg", new { id = id, msg = enc(msgbuf, iv) }) != 1)
                throw new Exception();
            return foundId;
        }
        public List<OutboxMsg> outbox = null;
        public OutboxMsg[] GetOutbox()
        {
            var ls= new List<OutboxMsg>();
            foreach (var v in conn.Query<OutboxDB>("select * from outbox order by id desc", new { }))
            {
                var iv = ArrayrXor(outbox_iv, v.id);
                var buf = dec(v.msg, iv);
                using(var ms = new MemoryStream(buf))
                using (var br = new BinaryReader(ms))
                using (var rd = new StreamReader(ms))
                {
                    ls.Add(new OutboxMsg() { id = v.id, theirpub = br.ReadInt32(), myprv = br.ReadInt32(), msgId = br.ReadBytes(16), replyTo = br.ReadBytes(16), txt = rd.ReadToEnd() });
                }
            }
            outbox = ls;
            return ls.ToArray();
        }

        public Client.Message[] GetInbox()
        {
            var ls = new List<Client.Message>();
            foreach (var v in conn.Query<OutboxDB>("select * from inbox order by id desc", new { }))
            {
                var iv = ArrayrXor(inbox_iv, v.id);
                var buf = dec(v.msg, iv);
                using(var ms = new MemoryStream(buf))
                using (var rd = new StreamReader(ms))
                {
                    using(var ms2=new MemoryStream(dec(v.msg,iv)))
                    using(var br=new BinaryReader(ms2))
                    {
                        var prvId = br.ReadInt32();
                        var prvbytes = prvkeys.Find(s => s.id == prvId).key;
                        var pub = Utils.ExtractPublicKey(prvbytes);
                        RSAParameters rsa;
                        Shared.LoadKey2(Shared.prvToPem(prvbytes), null, out rsa);
                        var msg = Client.DecodeMessage2(ms2, rsa, pub);
                        msg.MyPrvId = prvId;
                        ls.Add(msg);
                    }
                }
            }
            return ls.ToArray();
        }
        Private_Keys GetMsgPrv =null;
        public Action<Client.Message> GetStore(Private_Keys prvkey)
        {
            GetMsgPrv = prvkey;
            return Store;
        }
        void Store(Client.Message msg)
        {
            var idq = conn.Query<long>("select id from inbox order by id desc limit 1", new { });
            long id = idq.Count() == 0 ? 1 : idq.First() + 1;
            var iv = ArrayrXor(inbox_iv, id);
            var buf2 = new byte[msg.unencrypted.Length + 4];
            Array.Copy(BitConverter.GetBytes(GetMsgPrv.id), buf2, 4);
            Array.Copy(msg.unencrypted, 0, buf2, 4, msg.unencrypted.Length);
            conn.Execute("insert into inbox(id, msg) select @id, @msg", new { id = id, msg = enc(buf2, iv) });
        }
        public string GetName(byte[] pubkey)
        {
            foreach (var v in pubkeys)
            {
                if (pubkey.ArraysEqual(v.key))
                {

                    return v.name;
                }
            }
            return null;
        }
        public bool Load(RSAParameters rsap, byte[]pemdata)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(rsap);
                var arr = rsa.Decrypt(pemdata, false);
                aes.Key = arr.Take(aes.Key.Length).ToArray();
                des.Key = arr.Skip(aes.Key.Length).Take(des.Key.Length).ToArray();
            }
            IsLoaded_ = true;
            return true;
        }
    }
    class OutboxDB{
        public long id;
        public byte[]msg;
    }
    class InboxMsg
    {
        public long id;
        public byte[] buf;
    }
    class OutboxMsg
    {
        public long id, theirpub, myprv;
        public string txt;
        public byte[] msgId, replyTo;
        public override string ToString()
        {
            foreach (var v in DB.db.pubkeys)
            {
                if (v.id == theirpub)
                {
                    var sz = Convert.ToBase64String(v.key);
                    return string.Format("{0} ({1})", v.name, sz.Substring(sz.Length - 24).Substring(0, 16));
                }
            }
            throw new Exception();
        }
    }
    class Private_Keys
    {
        public long id;
        public byte[] key;
        public string name;
        public long visit_date;
        public override string ToString()
        {
            var sz = Convert.ToBase64String(Utils.ExtractPublicKey(key));
            return string.Format("{0} ({1})", name, sz.Substring(sz.Length - 24).Substring(0, 16));
        }
    }
    class Private_KeysDB
    {
        public long id;
        public byte[] key, name;
        public long last_date;
    }
    class Public_Keys
    {
        public long id;
        public byte[] key;
        public string name;
        public override string ToString()
        {
            var sz = Convert.ToBase64String(key);
            return string.Format("{0} ({1})", name, sz.Substring(sz.Length - 24).Substring(0, 16));
        }
    }
    class Public_KeysDB
    {
        public long id;
        public byte[] key, name;
    }
    class Info
    {
        public long id;
        public string k;
        public byte[] v;
    }
}
