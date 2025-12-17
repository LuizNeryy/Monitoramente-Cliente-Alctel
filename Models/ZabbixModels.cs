namespace monitor_services_api.Models
{
    public class ZabbixRequest
    {
        public string Jsonrpc { get; set; } = "2.0";
        public string Method { get; set; } = "";
        public object Params { get; set; } = new { };
        public int Id { get; set; } = 1;
    }

    public class ZabbixResponse<T>
    {
        public string? Jsonrpc { get; set; }
        public T? Result { get; set; }
        public ZabbixError? Error { get; set; }
        public int Id { get; set; }
    }

    public class ZabbixError
    {
        public int Code { get; set; }
        public string Message { get; set; } = "";
        public string Data { get; set; } = "";
    }

    public class ZabbixHost
    {
        public string Hostid { get; set; } = "";
        public string Host { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Available { get; set; } = "";
        public List<ZabbixInterface> Interfaces { get; set; } = new();
    }

    public class ZabbixInterface
    {
        public string Ip { get; set; } = "";
        public string Available { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public class ZabbixItem
    {
        public string Itemid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Lastvalue { get; set; } = "";
        public string Units { get; set; } = "";
        public string Lastclock { get; set; } = "";
        public string Status { get; set; } = "";
        public string Key_ { get; set; } = "";
    }

    public class ZabbixTrigger
    {
        public string Triggerid { get; set; } = "";
        public string Description { get; set; } = "";
        public string Priority { get; set; } = "";
        public string Value { get; set; } = "";
        public string Lastchange { get; set; } = "";
    }

    public class ZabbixProblem
    {
        public string Eventid { get; set; } = "";
        public string Clock { get; set; } = "";
        public string R_clock { get; set; } = "";
        public string Name { get; set; } = "";
        public string Severity { get; set; } = "";
    }

    public class ZabbixEvent
    {
        public string Eventid { get; set; } = "";
        public string Clock { get; set; } = "";
        public string R_eventid { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
