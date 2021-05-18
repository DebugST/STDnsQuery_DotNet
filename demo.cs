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