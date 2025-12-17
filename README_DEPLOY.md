# 🚀 Guia de Deploy - Monitor de Serviços

## 📋 Mudanças Realizadas

### ✅ Limpeza e Otimização
1. **Removidas TODAS as rotas legadas** sem `clientId`:
   - ❌ `/api/host-info`
   - ❌ `/api/availability`
   - ❌ `/api/items`
   - ❌ `/api/services`
   - ❌ `/api/triggers`
   - ❌ `/api/problems`
   - ❌ `/api/dashboard`

2. **Rotas Mantidas** (todas requerem `clientId`):
   - ✅ `/api/{clientId}/host-info`
   - ✅ `/api/{clientId}/services`
   - ✅ `/api/{clientId}/problems`
   - ✅ `/api/{clientId}/dashboard`
   - ✅ `/api/clients` (lista todos os clientes configurados)

3. **Removido** `_defaultTargetIp` do MonitorController
4. **Removido** `TargetServerIP` do appsettings.json
5. **Apagados** arquivos obsoletos `servicos-monitorados.txt`

### ✅ Garantia de Filtragem
**TUDO** que aparece no site agora vem **APENAS** dos serviços listados em `/clientes/{clientId}/servicos.txt`:
- Dashboard: agrega dados de múltiplos hosts
- Serviços: mostra apenas os do arquivo
- Problemas: filtra por `IsServicoMonitorado()`
- Disponibilidade: calcula baseado nos serviços monitorados

---

## 🔧 Configuração para Produção

### 1️⃣ Arquivos de Configuração

**appsettings.json** (mínimo necessário):
```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://*:4000",
        "Certificate": {
          "Path": "certificado\\contactfycloud.pfx",
          "Password": "4400Alc@#$%"
        }
      }
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Zabbix": {
    "Server": "https://monitoramento.alctel.com.br/zabbix",
    "ApiToken": "21c645d50f97672ecbda4159d4362c9f2ac2214f19949d34cffff6607a912afb"
  },
  "Certificate": {
    "Path": "certificado\\contactfycloud.pfx",
    "Password": "4400Alc@#$%"
  }
}
```

### 2️⃣ Estrutura de Clientes

Para cada novo cliente, criar:
```
/clientes/{clientId}/
  ├── config.json
  └── servicos.txt
```

**config.json**:
```json
{
  "ClientId": "materdei",
  "ClientName": "Hospital Materdei",
  "ZabbixServer": null,
  "ZabbixApiToken": null
}
```
> ℹ️ Se `ZabbixServer` e `ZabbixApiToken` forem `null`, usa valores globais do appsettings.json

**servicos.txt** (formato: `NomeDoServico;IP.DO.HOST`):
```
CFY-Agente-MaterdeiGrupo;172.31.202.250
CFY-Roteamento-MaterdeiGrupo;172.31.202.250
CFY-Ura-MaterdeiGrupo;172.31.202.250
CFY-Agente-Materdei;172.31.202.251
```

### 3️⃣ IIS Rewrite (web.config em wwwroot/)

Para cada cliente adicionar regra:
```xml
<rule name="Materdei" stopProcessing="true">
  <match url="^materdei$" />
  <action type="Rewrite" url="MonitorV3.html?cliente=materdei" />
</rule>
```

---

## 📦 Deploy

### Opção 1: Publicação Manual
```powershell
cd "c:\...\Status c#\monitor-services-api"
dotnet publish -c Release -o publish
```

### Opção 2: Usando Profile do Visual Studio
1. Clique com botão direito no projeto
2. **Publish** → **FolderProfile**
3. Arquivos gerados em `/bin/Release/net8.0/publish/`

### Arquivos que DEVEM ir para produção:
```
✅ appsettings.json (configuração global)
✅ /clientes/ (TODA a pasta com todos os clientes)
✅ /certificado/ (certificado SSL)
✅ /wwwroot/ (arquivos estáticos + web.config)
✅ DLLs e executável
```

---

## ⚙️ Instalação como Windows Service

```powershell
# Criar o serviço
sc.exe create MonitorClienteAlctel binPath="C:\path\to\monitor-services-api.exe"

# Configurar para iniciar automaticamente
sc.exe config MonitorClienteAlctel start=auto

# Iniciar o serviço
sc.exe start MonitorClienteAlctel

# Parar o serviço
sc.exe stop MonitorClienteAlctel

# Remover o serviço (se necessário)
sc.exe delete MonitorClienteAlctel
```

---

## 🔍 Verificação Pós-Deploy

### Teste 1: Verificar se aplicação iniciou
```powershell
# Ver status do serviço
sc.exe query MonitorClienteAlctel

# Ver logs (se configurado)
Get-Content "C:\path\to\logs\log.txt" -Tail 50
```

### Teste 2: Verificar endpoints
```powershell
# Listar clientes
Invoke-RestMethod -Uri "https://localhost:4000/api/clients" -SkipCertificateCheck

# Testar dashboard de um cliente
Invoke-RestMethod -Uri "https://localhost:4000/api/materdei/dashboard" -SkipCertificateCheck

# Testar serviços
Invoke-RestMethod -Uri "https://localhost:4000/api/materdei/services" -SkipCertificateCheck
```

### Teste 3: Verificar acesso web
1. Abrir navegador: `https://servidor:4000/materdei`
2. Deve redirecionar para: `https://servidor:4000/MonitorV3.html?cliente=materdei`
3. Dashboard deve carregar com os serviços do cliente

---

## 🆕 Adicionar Novo Cliente

### Passo 1: Criar estrutura de arquivos
```powershell
mkdir "c:\...\clientes\novocliente"
```

### Passo 2: Criar config.json
```json
{
  "ClientId": "novocliente",
  "ClientName": "Novo Cliente SA",
  "ZabbixServer": null,
  "ZabbixApiToken": null
}
```

### Passo 3: Criar servicos.txt
```
Servico1;192.168.1.10
Servico2;192.168.1.10
Servico3;192.168.1.20
```

### Passo 4: Adicionar regra IIS (wwwroot/web.config)
```xml
<rule name="NovoCliente" stopProcessing="true">
  <match url="^novocliente$" />
  <action type="Rewrite" url="MonitorV3.html?cliente=novocliente" />
</rule>
```

### Passo 5: Reiniciar aplicação
```powershell
sc.exe stop MonitorClienteAlctel
sc.exe start MonitorClienteAlctel
```

---

## 🐛 Troubleshooting

### Problema: "Cliente não encontrado"
- ✅ Verificar se pasta `/clientes/{clientId}` existe
- ✅ Verificar se `config.json` está válido (JSON bem formado)
- ✅ Verificar se `ClientId` no JSON bate com nome da pasta

### Problema: "Host não encontrado para IP"
- ✅ Verificar se IP no `servicos.txt` está correto
- ✅ Verificar se host existe no Zabbix com esse IP
- ✅ Ver logs: console deve mostrar `⚠️ Host não encontrado para IP: X.X.X.X`

### Problema: Nenhum serviço aparecendo
- ✅ Verificar formato do `servicos.txt`: `NomeExato;IP` (sem espaços extras)
- ✅ Nome do serviço no txt deve ser **EXATAMENTE** igual ao nome no Zabbix
- ✅ Verificar se serviço existe no Zabbix com item "State of service"

### Problema: Certificado SSL inválido
- ✅ Verificar caminho do certificado em appsettings.json
- ✅ Verificar se arquivo `.pfx` existe na pasta `/certificado/`
- ✅ Verificar se senha do certificado está correta

---

## 📊 Monitoramento

### Logs importantes:
```
✓ Cliente 'materdei' carregado: Hospital Materdei
✓ Zabbix configurado para cliente 'materdei' (30 serviços)
⚠️ Host não encontrado para IP: 172.31.202.251
```

### Métricas de performance:
- Cada requisição ao dashboard faz múltiplas chamadas ao Zabbix (uma por host único)
- Tempo médio: 2-5 segundos para resposta completa
- Cache: atualmente não implementado (considerar Redis no futuro)

---

## 🔐 Segurança

### Recomendações:
1. ✅ Usar HTTPS sempre (já configurado)
2. ⚠️ Proteger `/api/clients` se necessário (auth)
3. ⚠️ Não expor token Zabbix no frontend
4. ✅ Validar entrada de `clientId` (já implementado)

---

## 📝 Checklist de Deploy

- [ ] Build em Release compilou sem erros
- [ ] Certificado SSL presente e válido
- [ ] appsettings.json configurado corretamente
- [ ] Pasta `/clientes/` copiada para produção
- [ ] web.config com regras IIS para todos clientes
- [ ] Serviço Windows instalado e iniciado
- [ ] Firewall liberado na porta 4000
- [ ] Testes de endpoint funcionando
- [ ] Dashboard web carregando corretamente
- [ ] Todos os serviços aparecem (verificar IPs no Zabbix)

---

**✨ Código otimizado, pronto para produção!**
