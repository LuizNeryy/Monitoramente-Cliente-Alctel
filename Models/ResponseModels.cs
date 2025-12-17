namespace monitor_services_api.Models
{
    public class HostInfoResponse
    {
        public string Hostid { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string Ip { get; set; } = "";
        public string Status { get; set; } = "";
        public string Available { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public class AvailabilityResponse
    {
        public double Availability { get; set; }
        public double DowntimeMinutes { get; set; }
        public double TotalMinutes { get; set; }
        public double UptimeMinutes { get; set; }
        public int ServicesCount { get; set; }
        public int ServicesWithDowntime { get; set; }
        public string CalculationMethod { get; set; } = "";
    }

    public class ItemResponse
    {
        public string Name { get; set; } = "";
        public object Value { get; set; } = "";
        public string Units { get; set; } = "";
        public string Lastcheck { get; set; } = "";
    }

    public class ServiceResponse
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Active { get; set; }
        public string Lastcheck { get; set; } = "";
    }

    public class TriggerResponse
    {
        public string Description { get; set; } = "";
        public string Priority { get; set; } = "";
        public string PriorityLevel { get; set; } = "";
        public string Status { get; set; } = "";
        public string Lastchange { get; set; } = "";
    }

    public class ProblemResponse
    {
        public string Name { get; set; } = "";
        public string Severity { get; set; } = "";
        public string SeverityLevel { get; set; } = "";
        public string Status { get; set; } = "";
        public string Started { get; set; } = "";
        public double DurationMinutes { get; set; }
    }

    public class DashboardResponse
    {
        public DashboardHost Host { get; set; } = new();
        public DashboardAvailability Availability { get; set; } = new();
        public DashboardProblems Problems { get; set; } = new();
        public DashboardTriggers Triggers { get; set; } = new();
    }

    public class DashboardHost
    {
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public string Status { get; set; } = "";
        public string Available { get; set; } = "";
    }

    public class DashboardAvailability
    {
        public double Percent { get; set; }
        public double DowntimeMinutes { get; set; }
        public double UptimeMinutes { get; set; }
        public double TotalMinutes { get; set; }
    }

    public class DashboardProblems
    {
        public int Total { get; set; }
        public int Active { get; set; }
        public int Resolved { get; set; }
    }

    public class DashboardTriggers
    {
        public int Total { get; set; }
        public int Critical { get; set; }
        public int Warning { get; set; }
    }

    // ===== MODELOS PARA DOWNTIME CALCULATION =====
    
    public class DowntimeReportResponse
    {
        public string ClientId { get; set; } = "";
        public int PeriodDays { get; set; }
        public DateTime GeneratedAt { get; set; }
        public long TotalDowntimeSeconds { get; set; }
        public string TotalDowntimeFormatted { get; set; } = "";
        public int ServicesCount { get; set; }
        public int ServicesWithDowntime { get; set; }
        public List<ServiceDowntimeDetail> Services { get; set; } = new();
    }

    public class ServiceDowntimeDetail
    {
        public string ServiceName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public long TotalDowntimeSeconds { get; set; }
        public string TotalDowntimeFormatted { get; set; } = "";
        public int IncidentCount { get; set; }
        public List<IncidentDetail> Incidents { get; set; } = new();
    }

    public class IncidentDetail
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public long DurationSeconds { get; set; }
        public string DurationFormatted { get; set; } = "";
        public string TriggerName { get; set; } = "";
        public bool IsActive { get; set; }
    }
}
