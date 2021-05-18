## STDnsQuery

![GO1.16](https://img.shields.io/badge/.Net-3.5-blue) [![license](https://img.shields.io/badge/License-MIT-green)](https://github.com/DebugST/STDnsQuery_DotNet/blob/main/LICENSE) 

STDnsQuery 是一个快速DNS查询工具 其中 DnsClient 是一个方便快捷的调用类 支持 A、NS、CNAME、DNAME 查询 使用简单 

![STDnsQuery](https://raw.githubusercontent.com/DebugST/STDnsQuery_DotNet/main/Images/Screen%20Shot%202021-05-18%20at%2018.09.00.png)

CUI(GO):[https://github.com/DebugST/STDnsQuery_GO](https://github.com/DebugST/STDnsQuery_GO)

![STDnsQuery](https://raw.githubusercontent.com/DebugST/STDnsQuery_GO/main/Images/Screen%20Shot%202021-05-14%20at%2000.54.29.png)

## Demo

``` cs
class Demo
{
    public void Test() {
        DnsClient dns = new DnsClient(new IPEndPoint[]{
            new IPEndPoint(IPAddress.Parse("8.8.8.8"),53)
        });
        dns.DnsCompleted += new DnsClient.DnsCompletedEventHandler(dns_DnsCompleted);
        dns.Start(1000);
        dns.Query("www.google.com", DnsType.A, 1, 3);
    }

    void dns_DnsCompleted(object sender, DnsCompletedEventArgs e) {
        if (e.Result.RCode == DnsRCode.None) {
            Console.WriteLine(e.Result.Domain + "," + e.Result.Answers.Length);
        }
    }
}
```