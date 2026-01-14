namespace monitor_services_api.Services
{
    public class DowntimeHistoryService
    {
        private readonly string _clientsFolder;

        public DowntimeHistoryService()
        {
            _clientsFolder = Path.Combine(AppContext.BaseDirectory, "clientes");
        }

        public bool IsIncidentAlreadyOpened(string clientId, string serviceName, long startTime)
        {
            var clientFolder = Path.Combine(_clientsFolder, clientId);
            var clientFile = Path.Combine(clientFolder, "downtime.txt");
            
            if (!File.Exists(clientFile))
                return false;
            
            try
            {
                var lines = File.ReadAllLines(clientFile);
                var searchPattern = $"ABERTURA - Serviço: {serviceName} | Início: {DateTimeOffset.FromUnixTimeSeconds(startTime).LocalDateTime:yyyy-MM-dd HH:mm:ss}";
                
                // Verifica se já existe uma linha de ABERTURA para este incidente específico
                return lines.Any(line => line.Contains(searchPattern));
            }
            catch
            {
                return false;
            }
        }

        public void SaveIncidentOpened(string clientId, string serviceName, long startTime)
        {
            // Verifica se já salvamos este incidente antes
            if (IsIncidentAlreadyOpened(clientId, serviceName, startTime))
            {
                Console.WriteLine($"[HISTORY] Incidente já registrado: {serviceName} em {DateTimeOffset.FromUnixTimeSeconds(startTime).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                return;
            }
            
            var clientFolder = Path.Combine(_clientsFolder, clientId);
            Directory.CreateDirectory(clientFolder);
            
            var clientFile = Path.Combine(clientFolder, "downtime.txt");
            
            var startDate = DateTimeOffset.FromUnixTimeSeconds(startTime).LocalDateTime;
            var recordedDate = DateTime.Now;
            
            var logEntry = $"[{recordedDate:yyyy-MM-dd HH:mm:ss}] ABERTURA - Serviço: {serviceName} | Início: {startDate:yyyy-MM-dd HH:mm:ss} | UnixStart: {startTime}";
            
            // Lê todas as linhas existentes
            var allLines = File.Exists(clientFile) ? File.ReadAllLines(clientFile).ToList() : new List<string>();
            
            // Adiciona a nova linha
            allLines.Add(logEntry);
            
            // Apaga e reescreve todo o arquivo
            File.WriteAllLines(clientFile, allLines);
            Console.WriteLine($"[HISTORY] Incidente ABERTO: {serviceName} em {startDate:yyyy-MM-dd HH:mm:ss}");
        }

        public void SaveDowntimeRecord(string clientId, string serviceName, long startTime, long endTime, double downtimeMinutes)
        {
            var clientFolder = Path.Combine(_clientsFolder, clientId);
            Directory.CreateDirectory(clientFolder);
            
            var clientFile = Path.Combine(clientFolder, "downtime.txt");
            
            var startDate = DateTimeOffset.FromUnixTimeSeconds(startTime).LocalDateTime;
            var endDate = DateTimeOffset.FromUnixTimeSeconds(endTime).LocalDateTime;
            var recordedDate = DateTime.Now;
            
            var logEntry = $"[{recordedDate:yyyy-MM-dd HH:mm:ss}] RESOLUÇÃO - Serviço: {serviceName} | " +
                          $"Início: {startDate:yyyy-MM-dd HH:mm:ss} | " +
                          $"Fim: {endDate:yyyy-MM-dd HH:mm:ss} | " +
                          $"Downtime: {downtimeMinutes:F2} min | " +
                          $"UnixStart: {startTime} | " +
                          $"UnixEnd: {endTime}";
            
            // Lê todas as linhas existentes
            var allLines = File.Exists(clientFile) ? File.ReadAllLines(clientFile).ToList() : new List<string>();
            
            // Adiciona a nova linha
            allLines.Add(logEntry);
            
            // Apaga e reescreve todo o arquivo
            File.WriteAllLines(clientFile, allLines);
            Console.WriteLine($"[HISTORY] Incidente RESOLVIDO: {serviceName} - Downtime: {downtimeMinutes:F2} min");
        }

        public List<DowntimeRecord> LoadDowntimeRecords(string clientId)
        {
            var clientFolder = Path.Combine(_clientsFolder, clientId);
            var clientFile = Path.Combine(clientFolder, "downtime.txt");
            
            if (!File.Exists(clientFile))
                return new List<DowntimeRecord>();
            
            var records = new List<DowntimeRecord>();
            
            try
            {
                var lines = File.ReadAllLines(clientFile);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        // Novo formato: [RESOLVIDO] Serviço: ... | Início: ... | Fim: ... | Duração: XXm | Trigger: ...
                        if (line.StartsWith("[RESOLVIDO]"))
                        {
                            var parts = line.Split('|');
                            if (parts.Length < 5) continue;
                            var serviceName = parts[0].Split(':')[1].Trim();
                            var startStr = parts[1].Split(':')[1].Trim();
                            var endStr = parts[2].Split(':')[1].Trim();
                            var durStr = parts[3].Split(':')[1].Replace("m", "").Replace("h", ":").Trim();
                            // Suporta "27m" ou "2h 30m"
                            double downtimeMin = 0;
                            if (durStr.Contains(':'))
                            {
                                var hmparts = durStr.Split(':');
                                if (hmparts.Length == 2 && int.TryParse(hmparts[0], out var h) && int.TryParse(hmparts[1], out var m))
                                    downtimeMin = h * 60 + m;
                            }
                            else
                            {
                                double.TryParse(durStr, out downtimeMin);
                            }
                            if (DateTime.TryParse(startStr, out var startDt) && DateTime.TryParse(endStr, out var endDt))
                            {
                                var startUnix = new DateTimeOffset(startDt).ToUnixTimeSeconds();
                                var endUnix = new DateTimeOffset(endDt).ToUnixTimeSeconds();
                                records.Add(new DowntimeRecord
                                {
                                    ServiceName = serviceName,
                                    StartTime = startUnix,
                                    EndTime = endUnix,
                                    DowntimeMinutes = downtimeMin,
                                    RecordedAt = endUnix
                                });
                            }
                        }
                        // Formato antigo: ... | Downtime: XX min | UnixStart: ... | UnixEnd: ...
                        else if (line.Contains("Downtime:"))
                        {
                            var parts = line.Split('|');
                            if (parts.Length < 6) continue;
                            var serviceName = parts[1].Split(':')[1].Trim();
                            var downtimeStr = parts[4].Split(':')[1].Replace("min", "").Trim();
                            var unixStartStr = parts[5].Split(':')[1].Trim();
                            var unixEndStr = parts[6].Split(':')[1].Trim();
                            if (long.TryParse(unixStartStr, out var startTime) &&
                                long.TryParse(unixEndStr, out var endTime) &&
                                double.TryParse(downtimeStr, out var downtimeMin))
                            {
                                records.Add(new DowntimeRecord
                                {
                                    ServiceName = serviceName,
                                    StartTime = startTime,
                                    EndTime = endTime,
                                    DowntimeMinutes = downtimeMin,
                                    RecordedAt = endTime
                                });
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch
            {
                return new List<DowntimeRecord>();
            }
            return records;
        }

        public double GetHistoricalDowntime(string clientId, string serviceName, long timeFrom, long timeTill)
        {
            var records = LoadDowntimeRecords(clientId);
            
            // Filtra registros do serviço que se sobrepõem com a janela de tempo
            var relevantRecords = records
                .Where(r => r.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
                .Where(r => r.EndTime >= timeFrom && r.StartTime <= timeTill)
                .ToList();
            
            double totalDowntime = 0;
            
            foreach (var record in relevantRecords)
            {
                // Calcula apenas a parte do downtime que está dentro da janela
                var effectiveStart = Math.Max(record.StartTime, timeFrom);
                var effectiveEnd = Math.Min(record.EndTime, timeTill);
                totalDowntime += (effectiveEnd - effectiveStart) / 60.0; // Converte para minutos
            }
            
            return totalDowntime;
        }

        public void CleanOldRecords(string clientId, long olderThan)
        {
            var clientFolder = Path.Combine(_clientsFolder, clientId);
            var clientFile = Path.Combine(clientFolder, "downtime.txt");
            
            if (!File.Exists(clientFile))
                return;
            
            var lines = File.ReadAllLines(clientFile);
            var validLines = new List<string>();
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try
                {
                    // Verifica se o UnixEnd é mais recente que o olderThan
                    var parts = line.Split('|');
                    if (parts.Length >= 6)
                    {
                        var unixEndStr = parts[6].Split(':')[1].Trim();
                        if (long.TryParse(unixEndStr, out var endTime) && endTime >= olderThan)
                        {
                            validLines.Add(line);
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
            
            if (validLines.Count < lines.Length)
            {
                File.WriteAllLines(clientFile, validLines);
                Console.WriteLine($"[HISTORY] Limpeza: {lines.Length - validLines.Count} registros antigos removidos");
            }
        }

        public void CleanRemovedServices(string clientId, HashSet<string> currentServices)
        {
            var clientFolder = Path.Combine(_clientsFolder, clientId);
            var clientFile = Path.Combine(clientFolder, "downtime.txt");
            
            if (!File.Exists(clientFile))
                return;
            
            var lines = File.ReadAllLines(clientFile);
            var validLines = new List<string>();
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try
                {
                    // Extrai o nome do serviço da linha
                    string serviceName = "";
                    
                    if (line.Contains("Serviço:"))
                    {
                        var parts = line.Split('|');
                        foreach (var part in parts)
                        {
                            if (part.Contains("Serviço:"))
                            {
                                serviceName = part.Split(':')[1].Trim();
                                break;
                            }
                        }
                    }
                    
                    // Se o serviço ainda existe na lista atual, mantém a linha
                    if (!string.IsNullOrEmpty(serviceName) && 
                        currentServices.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
                    {
                        validLines.Add(line);
                    }
                }
                catch
                {
                    continue;
                }
            }
            
            if (validLines.Count < lines.Length)
            {
                File.WriteAllLines(clientFile, validLines);
                Console.WriteLine($"[HISTORY] Limpeza: {lines.Length - validLines.Count} registros de serviços removidos excluídos");
            }
        }
    }

    public class DowntimeRecord
    {
        public string ServiceName { get; set; } = string.Empty;
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public double DowntimeMinutes { get; set; }
        public long RecordedAt { get; set; }
    }
}
