using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Net;
using System.Net.Sockets;

using ST.Library.IO;
using System.Threading;

namespace ST.Library.Network
{
    public class DnsClient : IDisposable
    {
        private static List<string> m_lst_buffer = new List<string>();
        private static Dictionary<string, CacheInfo> m_dic_cache = new Dictionary<string, CacheInfo>();

        private struct CacheInfo
        {
            public long ExprieTime;
            public QueryResult Result;
        }

        private bool _Disposed;

        public bool Disposed {
            get { return _Disposed; }
        }

        private bool _IsStarted;

        public bool IsStarted {
            get { return _IsStarted; }
        }

        private int _Runed;

        public int Runed {
            get { return _Runed; }
        }

        public int Running {
            get { return m_dic_runnig.Count; }
        }

        private ChanQueue<ushort> m_cq_ids;                 //dns query id
        private ChanQueue<QueryTask> m_cq_idle_task;
        private ChanQueue<QueryTask> m_cq_wait_task;
        private ChanQueue<Socket> m_cq_idle_sock;
        private List<Socket> m_lst_socks;                   //all socket
        private Dictionary<ushort, QueryTask> m_dic_runnig;
        private HashSet<EndPoint> m_hs_dns_eps;
        private EndPoint[] m_dns_eps;
        private int m_nMaxTask;

        public delegate void DnsCompletedEventHandler(object sender, DnsCompletedEventArgs e);
        public event DnsCompletedEventHandler DnsCompleted;
        protected virtual void OnDnsCompleted(DnsCompletedEventArgs e) {
            if (this.DnsCompleted != null) this.DnsCompleted(this, e);
        }

        static DnsClient() {
            m_dic_cache = new Dictionary<string, CacheInfo>();
            new Thread(DnsClient.CheckCacheTimeout) { IsBackground = true }.Start();
        }

        public DnsClient(EndPoint[] dnsServer) {
            HashSet<string> hs = new HashSet<string>();
            m_hs_dns_eps = new HashSet<EndPoint>();
            foreach (var v in dnsServer) {
                if (!hs.Add(v.ToString())) continue;
                m_hs_dns_eps.Add(v);
            }
            m_dns_eps = m_hs_dns_eps.ToArray();
        }

        public void Start(int nMaxTask) {
            lock (this) {
                if (this._IsStarted) return;
                this._IsStarted = true;
            }
            m_lst_socks = new List<Socket>(nMaxTask);
            m_cq_ids = new ChanQueue<ushort>();
            m_cq_idle_sock = new ChanQueue<Socket>(nMaxTask);
            m_cq_idle_task = new ChanQueue<QueryTask>(nMaxTask);
            m_cq_wait_task = new ChanQueue<QueryTask>(nMaxTask);
            m_dic_runnig = new Dictionary<ushort, QueryTask>();
            for (ushort i = 1; i < 65535; i++) {
                m_cq_ids.Enqueue(i);
            }
            try {
                for (int i = 0; i < nMaxTask; i++) {
                    Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sock.Bind(new IPEndPoint(IPAddress.Any, 0));

                    SocketAsyncEventArgs sae = new SocketAsyncEventArgs();
                    sae.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                    sae.SetBuffer(new byte[1440], 0, 1440);
                    sae.RemoteEndPoint = new IPEndPoint(0, 0);
                    sae.UserToken = sock;
                    if (!sock.ReceiveFromAsync(sae)) this.ProcessRecvFrom(sae);

                    sae = new SocketAsyncEventArgs();
                    sae.SetBuffer(new byte[1440], 0, 1440);
                    sae.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                    sae.UserToken = sock;
                    m_cq_idle_sock.Enqueue(sock);
                    m_cq_idle_task.Enqueue(new QueryTask() {
                        SAE = sae
                    });
                    m_lst_socks.Add(sock);
                }
            } catch (Exception ex) {
                foreach (var v in m_lst_socks) v.Close();
                throw ex;
            }
            m_nMaxTask = nMaxTask;
            new Thread(this.StartSend) { IsBackground = true }.Start();
            new Thread(this.CheckTimeout) { IsBackground = true }.Start();
        }

        public int Query(string strDomain) { return this.Query(strDomain, DnsType.A, 1, 5, null); }
        public int Query(string strDomain, DnsType type) { return this.Query(strDomain, type, 1, 5, null); }
        public int Query(string strDomain, DnsType type, int nRetries, int nTimeout) {
            return this.Query(strDomain, type, nRetries, nTimeout, null);
        }

        public int Query(string strDomain, DnsType type, int nRetries, int nTimeout, EndPoint epDnsServer) {
            QueryResult qr = DnsClient.GetCachedResult(strDomain, type);
            if (qr.RCode != DnsRCode.NotFoundDomain) {
                this.EndTask(qr);
                return 0;
            }

            byte[] byBuffer = DnsClient.GetDnsRequest(strDomain, type);
            QueryTask qt = m_cq_idle_task.Dequeue();
            ushort id = m_cq_ids.Dequeue();
            qt.ID = id;
            qt.Type = type;
            qt.Domain = strDomain;
            qt.Retried = 0;
            qt.Retries = nRetries;
            qt.Timeout = nTimeout;
            qt.DnsServer = epDnsServer;
            qt.Sended = false;
            byBuffer[0] = (byte)(id >> 8);
            byBuffer[1] = (byte)id;
            qt.SAE.SetBuffer(byBuffer, 0, byBuffer.Length);

            if (qt.DnsServer == null) {
                lock (m_hs_dns_eps) m_hs_dns_eps.Add(qt.DnsServer);
            }
            lock (m_dic_runnig) {
                m_dic_runnig.Add(id, qt);
            }
            m_cq_wait_task.Enqueue(qt);
            return id;
        }

        private void StartSend() {
            while (!this._Disposed) {
                //Get a task. This code will be blocked if the queue is empty.
                QueryTask task = m_cq_wait_task.Dequeue();
                //Get a idle socket. This code will be blocked if the queue is empty.
                Socket sock = m_cq_idle_sock.Dequeue();
                if (task.DnsServer != null) {
                    task.SAE.RemoteEndPoint = task.DnsServer;
                } else {
                    task.SAE.RemoteEndPoint = m_dns_eps[(task.ID + task.Retried) % m_dns_eps.Length];
                }
                try {
                    if (!sock.SendToAsync(task.SAE)) {
                        IOProcessPool.QueueWork(this.ProcessSendTo, task.SAE);
                    }
                } catch { /*Not process. CheckTimeout() will resend it until [Retried == Retries]*/ }
                task.Sended = true;
                task.LastTime = DateTime.Now;
            }
        }

        void IO_Completed(object sender, SocketAsyncEventArgs e) {
            switch (e.LastOperation) {
                case SocketAsyncOperation.SendTo:
                    this.ProcessSendTo(e);
                    break;
                case SocketAsyncOperation.ReceiveFrom:
                    this.ProcessRecvFrom(e);
                    break;
            }
        }

        private void ProcessSendTo(SocketAsyncEventArgs e) {
            Socket sock = e.UserToken as Socket;
            if (this._Disposed) {
                sock.Close();
                return;
            }
            m_cq_idle_sock.Enqueue(sock);
        }

        private void ProcessRecvFrom(SocketAsyncEventArgs e) {
            Socket sock = e.UserToken as Socket;
            if (e.BytesTransferred < 0 || e.SocketError != SocketError.Success) {
                sock.Close();
                return;
            }
            bool bValidEP = true;
            lock (m_hs_dns_eps) {
                bValidEP = m_hs_dns_eps.Contains(e.RemoteEndPoint);
            }
            if (bValidEP) {
                try {
                    QueryResult qr = DnsClient.GetDnsResponse(e.Buffer);
                    qr.DnsServer = e.RemoteEndPoint;
                    DnsClient.CacheResult(qr);
                    this.EndTask(qr);
                } catch { /*Can not format this package to dns response*/ }
            }
            if (this._Disposed) {
                sock.Close();
                return;
            }
            try {
                if (!sock.ReceiveFromAsync(e)) IOProcessPool.QueueWork(this.ProcessRecvFrom, e);
            } catch {
                sock.Close();
            }
        }

        private void CheckTimeout() {
            List<QueryTask> lst_timeout = new List<QueryTask>(m_nMaxTask);
            while (!this._Disposed) {
                Thread.Sleep(1000);
                lst_timeout.Clear();
                DateTime dt = DateTime.Now;
                lock (m_dic_runnig) {
                    foreach (var v in m_dic_runnig.Values) {
                        if (dt.Subtract(v.LastTime).TotalSeconds < v.Timeout || !v.Sended) continue;
                        v.Sended = false;
                        lst_timeout.Add(v);
                    }
                    foreach (var v in lst_timeout) {
                        if (v.Retried == v.Retries) {
                            this.EndTask(new QueryResult() {
                                ID = v.ID,
                                RCode = DnsRCode.TimeOut,
                                Domain = v.Domain,
                                Type = v.Type
                            });
                            continue;
                        }
                        v.Retried++;
                        m_cq_wait_task.Enqueue(v);
                    }
                }
            }
        }

        private void EndTask(QueryResult qr) {
            QueryTask task = null;
            if (qr.ID == 0) {
                this.OnDnsCompleted(new DnsCompletedEventArgs(qr));
                return;
            }
            lock (m_dic_runnig) {
                if (!m_dic_runnig.ContainsKey(qr.ID)) {
                    return;
                }
                task = m_dic_runnig[qr.ID];
                m_dic_runnig.Remove(qr.ID);
                this._Runed++;
            }
            this.OnDnsCompleted(new DnsCompletedEventArgs(qr));
            if (this._Disposed) return;
            m_cq_ids.Enqueue(qr.ID);
            m_cq_idle_task.Enqueue(task);
        }

        public void Stop() { this.Dispose(); }

        public void Dispose() {
            foreach (var v in m_lst_socks) v.Close();
            this._Disposed = true;
        }

        public static byte[] GetDnsRequest(string strDomain, DnsType type) {
            List<byte> lst = new List<byte>();
            byte[] byData = new byte[]{
                        0x00, 0x00, //ID
		                0x01, 0x00, //Standard query
		                0x00, 0x01, //questions :1
		                0x00, 0x00, //answer rrs :0
		                0x00, 0x00, //authority rrs:0
		                0x00, 0x00, //additional rrs:0
                    };
            lst.AddRange(byData);
            string[] strs = strDomain.Split('.');
            foreach (var s in strs) {
                if (s.Length > 0xFF || s.Length == 0) throw new ArgumentException("Invalid domain");
                lst.Add((byte)s.Length);
                lst.AddRange(Encoding.Default.GetBytes(s));
            }
            lst.AddRange(new byte[] {
                0x00                //End
                , 0x00, (byte)type, //Type
                0x00, 0x01          //Class
            });
            return lst.ToArray();
        }

        public static QueryResult GetDnsResponse(byte[] byBuffer) {
            //The dnsResponse minsize is 21. [4][5] = questions = 1
            if (byBuffer.Length < 21 || byBuffer[4] != 0 || byBuffer[5] != 1) {
                throw new InvalidCastException("can not format the package to dns response");
            }
            //1 0000... .0.. 0000 , response:1bit opcode:4bit resvered:1bit rcode:4bit
            if ((byBuffer[3] & 0xF8) != 0x80 || (byBuffer[3] & 0x40) != 0 || (byBuffer[3] & 0xF) > 5) {
                throw new InvalidCastException("can not format the package to dns response");
            }
            ushort uid = (ushort)(((ushort)byBuffer[0] << 8) + byBuffer[1]);
            DnsRCode dr = (DnsRCode)(byBuffer[3] & 0x0F);
            int nIndex = 12;
            string strDomain = DnsClient.GetDomainFromBuffer(byBuffer, m_lst_buffer, ref nIndex, 0);
            DnsType dt = (DnsType)(byBuffer[nIndex + 1]);
            if (dr != DnsRCode.None) {
                return new QueryResult() {
                    ID = uid,
                    RCode = dr,
                    Domain = strDomain,
                    Type = dt
                };
            }
            nIndex += 4;
            DnsAnswer[] answers = DnsClient.GetAnswers(byBuffer, nIndex);
            return new QueryResult() {
                ID = uid,
                RCode = dr,
                Domain = strDomain,
                Type = dt,
                Answers = answers
            };
        }

        private static DnsAnswer[] GetAnswers(byte[] byBuffer, int nIndex) {
            DnsAnswer[] answers = new DnsAnswer[byBuffer[7] + (((int)byBuffer[6]) << 8)];
            for (int i = 0; i < answers.Length; i++) {
                bool bBreak = false;
                answers[i].Domain = DnsClient.GetDomainFromBuffer(byBuffer, m_lst_buffer, ref nIndex);
                answers[i].Type = (DnsType)(byBuffer[nIndex + 1] + (((int)byBuffer[nIndex]) << 8));
                nIndex += 2;
                answers[i].Class = (Class)(byBuffer[nIndex + 1] + (((int)byBuffer[nIndex]) << 8));
                nIndex += 2;
                answers[i].TTL = byBuffer[nIndex + 3] + ((int)byBuffer[nIndex + 2] << 8) + ((int)byBuffer[nIndex + 1] << 16) + ((int)byBuffer[nIndex] << 24);
                nIndex += 4;
                switch (answers[i].Type) {
                    case DnsType.A:
                        nIndex += 2;
                        answers[i].Data = byBuffer[nIndex] + "." + byBuffer[nIndex + 1] + "." + byBuffer[nIndex + 2] + "." + byBuffer[nIndex + 3];
                        nIndex += 4;
                        break;
                    case DnsType.NS:
                    case DnsType.DNAME:
                    case DnsType.CNAME:
                        nIndex += 2;
                        answers[i].Data = DnsClient.GetDomainFromBuffer(byBuffer, m_lst_buffer, ref nIndex, byBuffer[nIndex - 1] + ((int)byBuffer[nIndex - 2] << 8));
                        break;
                    default:
                        if (i == 0) {
                            answers = null;
                        } else {
                            DnsAnswer[] an = new DnsAnswer[i];
                            Array.Copy(answers, an, i);
                        }
                        bBreak = true;
                        break;
                }
                if (bBreak) break;
            }
            return answers;
        }

        private static string GetDomainFromBuffer(byte[] byBuffer, List<string> lstTemp, ref int nIndex, int nDataLen = 0) {
            lstTemp.Clear();
            int nOldIndex = 0, nMaxIndex = nIndex + nDataLen;
            while (byBuffer[nIndex] != 0) {
                if (nDataLen != 0 && nIndex >= nMaxIndex) break;
                if ((byBuffer[nIndex] & 0xC0) == 0xC0) {
                    if (nOldIndex == 0) nOldIndex = nIndex;
                    nIndex = byBuffer[nIndex + 1] + ((int)(byBuffer[nIndex] & ~0xC0) << 8);
                }
                lstTemp.Add(Encoding.UTF8.GetString(byBuffer, nIndex + 1, byBuffer[nIndex]));
                nIndex += byBuffer[nIndex] + 1;
            }
            nIndex++;
            if (nOldIndex != 0) nIndex = nOldIndex + 2;
            return string.Join(".", lstTemp.ToArray()).ToLower();
        }

        public static void CacheResult(QueryResult qr) {
            if (qr.Answers == null || qr.Answers.Length == 0) return;
            qr.ID = 0;
            qr.IsCache = true;
            string strKey = qr.Type + "|" + qr.Domain.ToLower();
            lock (m_dic_cache) {
                CacheInfo ci = new CacheInfo() {
                    ExprieTime = DateTime.Now.AddSeconds(qr.Answers[0].TTL).Ticks,
                    Result = qr
                };
                if (m_dic_cache.ContainsKey(strKey)) {
                    m_dic_cache[strKey] = ci;
                } else {
                    m_dic_cache.Add(strKey, ci);
                }
                foreach (var v in qr.Answers) {
                    strKey = v.Type + "|" + v.Domain.ToLower();
                    ci = new CacheInfo() {
                        ExprieTime = DateTime.Now.AddSeconds(v.TTL).Ticks,
                        Result = qr
                    };
                    if (m_dic_cache.ContainsKey(strKey)) {
                        m_dic_cache[strKey] = ci;
                    } else {
                        m_dic_cache.Add(strKey, ci);
                    }
                }
            }
        }

        public static QueryResult GetCachedResult(string strDomain, DnsType type) {
            QueryResult qr = new QueryResult();
            qr.RCode = DnsRCode.NotFoundDomain;
            string strKey = type + "|" + strDomain.ToLower();
            lock (m_dic_cache) {
                if (!m_dic_cache.ContainsKey(strKey)) return qr;
                CacheInfo ci = m_dic_cache[strKey];
                if (DateTime.Now.Ticks > ci.ExprieTime) {
                    m_dic_cache.Remove(strKey);
                    return qr;
                }
                qr = ci.Result;
            }
            return qr;
        }

        public static void CheckCacheTimeout() {
            while (true) {
                List<string> lst = new List<string>();
                Thread.Sleep(1000);
                long dt_now = DateTime.Now.Ticks;
                lock (m_dic_cache) {
                    foreach (var v in m_dic_cache) {
                        if (dt_now > v.Value.ExprieTime) lst.Add(v.Key);
                    }
                    foreach (var v in lst) m_dic_cache.Remove(v);
                }
            }
        }

        public static void ClearCache() {
            lock (m_dic_cache) {
                m_dic_cache.Clear();
            }
        }
    }
}
