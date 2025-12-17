# Funcionalidade de Cálculo de Downtime

## Visão Geral

Implementação da funcionalidade de cálculo de downtime baseada no script Python `calculaDowntime.py`. O sistema calcula o tempo de indisponibilidade de cada serviço monitorado nos últimos 30 dias, identificando incidentes através da API do Zabbix.

## Arquitetura

### 1. **DowntimeCalculationService.cs**
Serviço responsável pelo cálculo de downtime, adaptado da lógica do script Python.

**Principais funcionalidades:**
- `CalculateClientDowntimeAsync()`: Calcula downtime de todos os serviços do cliente
- `GetDowntimeForServiceAsync()`: Busca eventos específicos de um serviço
- `SaveDowntimeReportAsync()`: Salva relatório em JSON na pasta do cliente
- `GetSavedReportAsync()`: Recupera relatório salvo anteriormente

**Lógica de cálculo:**
1. Busca eventos do Zabbix com filtro: `nome do serviço` AND `"is not running"`
2. Para cada evento:
   - Se tem `r_eventid` (evento de recuperação): calcula duração exata
   - Se NÃO tem recuperação: considera ativo até o momento atual
3. Soma todas as durações para obter downtime total

### 2. **Endpoints da API**

#### `GET /api/{clientId}/downtime/calculate?days=30`
Calcula o downtime de todos os serviços do cliente.

**Parâmetros:**
- `days`: Período de análise (1-90 dias, padrão: 30)

**Resposta:**
```json
{
  "clientId": "materdei",
  "periodDays": 30,
  "generatedAt": "2025-12-16T10:30:00",
  "totalDowntimeSeconds": 3600,
  "totalDowntimeFormatted": "1h 0m",
  "servicesCount": 10,
  "servicesWithDowntime": 3,
  "services": [
    {
      "serviceName": "CFY - Web Service",
      "ipAddress": "192.168.1.10",
      "totalDowntimeSeconds": 1800,
      "totalDowntimeFormatted": "30m",
      "incidentCount": 2,
      "incidents": [
        {
          "startTime": "2025-12-10T14:30:00",
          "endTime": "2025-12-10T15:00:00",
          "durationSeconds": 1800,
          "durationFormatted": "30m",
          "triggerName": "CFY - Web Service is not running",
          "isActive": false
        }
      ]
    }
  ]
}
```

#### `GET /api/{clientId}/downtime/report`
Retorna o último relatório calculado (do arquivo JSON).

#### `GET /api/{clientId}/downtime/summary`
Retorna resumo simplificado. Se não houver relatório salvo, calcula automaticamente.

**Resposta:**
```json
{
  "clientId": "materdei",
  "periodDays": 30,
  "generatedAt": "2025-12-16T10:30:00",
  "totalDowntime": "1h 0m",
  "totalDowntimeSeconds": 3600,
  "servicesCount": 10,
  "servicesWithDowntime": 3,
  "availability": 99.86
}
```

### 3. **Front-end (Monitor.html)**

**Novo card adicionado:**
- Exibe resumo de downtime com métricas principais
- Botão "Recalcular" para forçar novo cálculo
- Seção expansível com detalhes por serviço
- Lista de incidentes por serviço (últimos 3)
- Atualização automática a cada 10 segundos

**Funções JavaScript:**
- `fetchDowntimeSummary()`: Busca resumo na inicialização
- `refreshDowntimeReport()`: Recalcula relatório completo
- `toggleDowntimeDetails()`: Expande/colapsa detalhes
- `renderDowntimeServicesList()`: Renderiza lista de serviços

## Modelos de Dados

### ZabbixEvent
```csharp
public class ZabbixEvent
{
    public string Eventid { get; set; }
    public string Clock { get; set; }
    public string R_eventid { get; set; }  // ID do evento de recuperação
    public string Name { get; set; }
}
```

### DowntimeReportResponse
```csharp
public class DowntimeReportResponse
{
    public string ClientId { get; set; }
    public int PeriodDays { get; set; }
    public DateTime GeneratedAt { get; set; }
    public long TotalDowntimeSeconds { get; set; }
    public string TotalDowntimeFormatted { get; set; }
    public int ServicesCount { get; set; }
    public int ServicesWithDowntime { get; set; }
    public List<ServiceDowntimeDetail> Services { get; set; }
}
```

### ServiceDowntimeDetail
```csharp
public class ServiceDowntimeDetail
{
    public string ServiceName { get; set; }
    public string IpAddress { get; set; }
    public long TotalDowntimeSeconds { get; set; }
    public string TotalDowntimeFormatted { get; set; }
    public int IncidentCount { get; set; }
    public List<IncidentDetail> Incidents { get; set; }
}
```

### IncidentDetail
```csharp
public class IncidentDetail
{
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }  // null = ainda ativo
    public long DurationSeconds { get; set; }
    public string DurationFormatted { get; set; }
    public string TriggerName { get; set; }
    public bool IsActive { get; set; }
}
```

## Armazenamento

Os relatórios são salvos em arquivos JSON na estrutura:
```
clientes/
  └── {clientId}/
      ├── config.json
      ├── servicos.txt
      └── downtime_report.json  ← Novo arquivo
```

**Exemplo de conteúdo:**
```json
{
  "clientId": "materdei",
  "periodDays": 30,
  "generatedAt": "2025-12-16T10:30:00",
  "totalDowntimeSeconds": 3600,
  "totalDowntimeFormatted": "1h 0m",
  "servicesCount": 10,
  "servicesWithDowntime": 3,
  "services": [...]
}
```

## Integração com Script Python

A lógica foi adaptada do script `calculaDowntime.py`:

| Python | C# |
|--------|-----|
| `get_downtime_for_service()` | `GetDowntimeForServiceAsync()` |
| `format_duration()` | `FormatDuration()` |
| `downtime_30_dias.txt` | `downtime_report.json` |
| Eventos de "is not running" | Mesmo filtro via API |
| Incidentes ativos (sem r_eventid) | `IsActive = true` |

## Como Usar

### 1. Calcular Downtime Manualmente
```bash
# Via API
GET https://seu-servidor/api/materdei/downtime/calculate?days=30

# Via front-end
Clicar no botão "Recalcular" no card de Downtime
```

### 2. Visualizar no Dashboard
Acesse o dashboard do cliente e o card de "Análise de Downtime" exibirá:
- Total de downtime formatado
- Número de serviços monitorados
- Serviços com downtime
- Disponibilidade calculada
- Detalhes expandíveis por serviço

### 3. Automatizar Cálculo
O endpoint `/downtime/summary` calcula automaticamente se não houver relatório:
```javascript
// No front-end, é chamado automaticamente a cada 10s
await fetchDowntimeSummary();
```

## Considerações Técnicas

### Performance
- O cálculo pode levar alguns segundos (10-30s) dependendo do número de serviços
- Relatórios são salvos em cache (arquivo JSON)
- Front-end usa resumo salvo para atualizações frequentes

### Filtros Zabbix
```csharp
search = new {
    name = new[] { serviceName, "is not running" }
},
searchByAny = false  // AND lógico
```

### Cálculo de Disponibilidade
```csharp
availability = (1 - (totalDowntimeSeconds / (periodDays * 24 * 3600 * servicesCount))) * 100
```

## Troubleshooting

### "Nenhum incidente encontrado"
- Verifique se o nome do serviço está correto em `servicos.txt`
- Confirme que existem triggers com "is not running" no Zabbix
- Verifique se há eventos no período de 30 dias

### "Erro ao calcular downtime"
- Verifique logs do servidor: `logs/monitor-services-api.log`
- Confirme conectividade com API do Zabbix
- Valide token de autenticação em `config.json`

### Relatório não atualiza
- Clique em "Recalcular" para forçar novo cálculo
- Verifique permissões de escrita na pasta `clientes/{clientId}/`

## Próximos Passos

Possíveis melhorias:
1. Agendamento automático de cálculos (ex: diariamente)
2. Exportação de relatórios em PDF/Excel
3. Comparação de períodos (mês anterior vs atual)
4. Alertas quando downtime exceder threshold
5. Gráficos de tendência de downtime
6. Análise por horário (identificar padrões)
