using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Threading;

namespace ST.Library.IO
{
    public class ChanQueue<T>
    {
        private Queue<T> m_que;
        private int m_nCount;
        private ManualResetEvent m_mre_en;
        private ManualResetEvent m_mre_de;
        private object m_obj_sync_en;
        private object m_obj_sync_de;

        public int Count { get { return m_que.Count; } }

        public int MaxCount { get { return m_nCount; } }

        public ChanQueue()
            : this(0) {
        }

        public ChanQueue(int nCount) {
            m_nCount = nCount;
            m_que = new Queue<T>(nCount);
            m_que = new Queue<T>();
            m_mre_de = new ManualResetEvent(false);
            m_mre_en = new ManualResetEvent(false);
            m_obj_sync_en = new object();
            m_obj_sync_de = new object();
        }

        public void Enqueue(T t) {
            bool bWait = false;
            lock (m_obj_sync_en) {
                lock (m_que) {
                    bWait = m_nCount != 0 && m_que.Count == m_nCount;
                    m_mre_en.Reset();
                }
                if (bWait) {
                    m_mre_en.WaitOne();
                }
                lock (m_que) {
                    m_que.Enqueue(t);
                    m_mre_de.Set();
                }
            }
        }

        public T Dequeue() {
            bool bWait = false;
            lock (m_obj_sync_de) {
                lock (m_que) {
                    bWait = m_que.Count == 0;
                    m_mre_de.Reset();
                }
                if (bWait) {
                    m_mre_de.WaitOne();
                }
                lock (m_que) {
                    var ret = m_que.Dequeue();
                    m_mre_en.Set();
                    return ret;
                }
            }
        }
    }
}
