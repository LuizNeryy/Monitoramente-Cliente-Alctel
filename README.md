# Monitoramento de Clientes Alctel

## Descrição

Este sistema é uma API desenvolvida em .NET 8 para monitoramento de ambientes de clientes da Alctel. Ele centraliza e automatiza a coleta de informações de disponibilidade, incidentes e históricos de downtime dos serviços monitorados, integrando-se a sistemas como o Zabbix e armazenando relatórios por cliente.

## Funcionalidades
- Consulta de status de serviços monitorados por cliente
- Histórico de downtime e geração de relatórios
- Integração com Zabbix para coleta de dados
- Configuração individualizada por cliente
- API REST para consumo externo

## Estrutura
- **Controllers/**: Endpoints da API
- **Services/**: Lógica de negócio e integrações
- **Models/**: Modelos de dados e contratos
- **clientes/**: Configurações e relatórios por cliente
- **wwwroot/**: Arquivos estáticos (ex: página de status)

## Como executar
1. Configure os arquivos `appsettings.json` e os arquivos de cada cliente em `clientes/`
2. Compile o projeto com `dotnet build`
3. Execute com `dotnet run` ou publique para produção

## Observações
- O sistema foi projetado para ser extensível e seguro, facilitando a inclusão de novos clientes e integrações.
- Consulte os arquivos `README_DEPLOY.md` e `README_DOWNTIME.md` para detalhes de deploy e funcionamento do cálculo de downtime.

---
Alctel Telecomunicações | 2025
# Monitoramente-Cliente-Alctel