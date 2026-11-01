# Sensor virtual de alagamento: Região Metropolitana do Recife

Solução IoT que estima o risco de alagamento em cada célula H3
(~0,74 km²) dos 14 municípios da RMR: um **Sensor** que lê chuva real e
uma **Platform** que cruza essa leitura com maré, terreno e histórico pra
calcular o risco, exposto por API.

São 3 projetos, cada um com um papel:

```
iot-virtual-sensor/
├── VirtualSensor.slnx
├── Shared/    # grade H3, código e dado comum aos outros dois
├── Sensor/    # Worker Service: lê chuva, publica a leitura
└── Platform/  # API: consome a leitura, calcula risco, expõe HTTP
```

Sensor e Platform são processos independentes — não se chamam
diretamente. O único ponto de contato é um arquivo compartilhado que o
Sensor escreve e a Platform lê (`estado-sensor/leituras_atual.json`).

## Shared

Código e dado usados pelos dois outros projetos:

- `Grid/H3GridBuilder.cs` — gera a grade de 3.210 células H3 a partir dos
  polígonos dos 14 municípios (`Data/rmr_municipios.json`, IBGE, estático).
- `Common/PathHelpers.cs` — resolve caminhos de arquivo de forma estável,
  incluindo a pasta compartilhada entre Sensor e Platform.
- `LeituraStore.cs` — contrato de troca de dado: o Sensor escreve a chuva
  do ciclo, a Platform lê.

## Sensor

Dispositivo IoT virtual: na prática são vários sensores virtuais, um por
célula H3, cada um lendo a chuva mais próxima da sua posição (feed
público da APAC/Cemaden) e publicando o valor bruto. Nenhuma
classificação de risco é feita aqui — isso é regra de negócio da
Platform, não sensoriamento.

Roda como Worker Service (`BackgroundService`), um ciclo a cada 15
minutos:

```bash
dotnet run --project Sensor            # loop contínuo
dotnet run --project Sensor -- --once  # um ciclo só, pra testar
```

## Platform

Cruza a chuva publicada pelo Sensor com dado estático e calcula o risco:

- `Processing/RainHistoryStore.cs` mantém um histórico local e calcula
  acumulados de chuva por janela (15min/1h/3h).
- `Processing/TideCalculator.cs` calcula a maré prevista a partir da
  tábua oficial da DHN para 2026 (`Data/mare_recife_2026.json`),
  interpolando por cosseno entre os extremos publicados.
- `Processing/TerrainCalculator.cs` calcula a suscetibilidade do terreno
  por célula: elevação (`Data/elevacao_rmr.json`, proxy de HAND),
  declividade e distância a curso d'água (`Data/hidrografia_rmr.json`).
- `Processing/RiskCalculator.cs` combina os três fatores na fórmula
  `R = P · M · S` (chuva × maré × terreno), classifica em 4 níveis (0 a
  3) e calcula probabilidade estimada e grau de confiança por célula.
- `RiscoIngestorService.cs` fica de olho no arquivo do Sensor; a cada
  ciclo novo, recalcula o risco e atualiza o cache em memória
  (`RiscoCache.cs`) que a API lê.

Roda como Web API (ASP.NET Core, endpoints mínimos):

```bash
dotnet run --project Platform
curl http://localhost:5128/risco/atual
```

`GET /risco/atual` retorna o ciclo mais recente processado e, por célula:
município, nível (0-3), probabilidade, confiança e os fatores (P, M, S).

## Como rodar tudo

Em dois terminais:

```bash
dotnet run --project Sensor      # publica leitura a cada 15 min
dotnet run --project Platform    # consome, calcula risco, expõe API
```

Ou builda a solução inteira de uma vez com `dotnet build`.
