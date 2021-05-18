using System;
using System.Net;
using System.Net.Sockets;

namespace ST.Library.Network
{
    internal class QueryTask
    {
        public ushort ID;
        public string Domain;
        public DnsType Type;
        public EndPoint DnsServer;
        public int Retries;
        public int Retried;
        public int Timeout;
        public DateTime LastTime;
        public bool Sended;
        public byte[] QueryPackage;
        public SocketAsyncEventArgs SAE;
    }

    public enum DnsType : byte
    {
        A = 0x01,
        NS = 0x02,
        //MD = 0x03,
        //MF = 0x04,
        CNAME = 0x05,
        //SOA = 0x06,
        //MB = 0x07,
        //MG = 0x08,
        //MR = 0x09,
        //NULL = 0x0A,
        //WKS = 0x0B,
        //PTR = 0x0C,
        //HINFO = 0x0D,
        //MINFO = 0x0E,
        //MX = 0x0F,
        //TXT = 0x10,
        //AAAA = 0x1C,
        DNAME = 0x27,
        //UINFO = 0x64,
        //UID = 0x65,
        //GID = 0x66,
        //AXFR = 0xFC,
        //ANY = 0xFF
    }

    public enum DnsRCode
    {
        None = 0,
        FormatError,
        ServerFailure,
        NotFoundDomain,
        UnknowSearchType,
        RejectQuery,
        TimeOut
    }

    public enum Class
    {
        IN = 0x01,
        CSNET = 0x02,
        CHAOS = 0x03,
        HESIOD = 0x04,
        ANY = 0xFF
    }

    public struct DnsAnswer
    {
        public string Domain;
        public DnsType Type;
        public Class Class;
        public int TTL;
        public string Data;
    }

    public struct QueryResult
    {
        public ushort ID;
        public DnsRCode RCode;
        public string Domain;
        public DnsType Type;
        public EndPoint DnsServer;
        public bool IsCache;
        public DnsAnswer[] Answers;
    }

    public class DnsCompletedEventArgs : EventArgs {
        private QueryResult _Result;

        public QueryResult Result {
            get { return _Result; }
        }

        public DnsCompletedEventArgs(QueryResult result) {
            this._Result = result;
        }
    }
}
