# Sensor virtual de alagamento: Região Metropolitana do Recife

Solução IoT que estima o risco de alagamento em cada célula H3
(~0,74 km²) dos 14 municípios da RMR: um **Sensor** que lê chuva real e
uma **Platform** que cruza essa leitura com maré, terreno e histórico pra
calcular o risco, exposto por API.

São 4 projetos, cada um com um papel:

```
iot-virtual-sensor/
├── VirtualSensor.slnx
├── Shared/    # grade H3, código e dado comum
├── Sensor/    # Worker Service: lê chuva, publica a leitura de cada célula
├── Platform/  # API: consome as leituras, calcula risco, expõe HTTP
└── Dashboard/ # ASP.NET mínimo: serve o mapa, que consulta a Platform
```

Sensor e Platform são processos independentes — não se chamam
diretamente. O único ponto de contato é um broker MQTT: o Sensor publica
em `rmr/chuva/{h3}` (um tópico por célula) e a Platform assina. Detalhes e diagrama em
[`docs/arquitetura.md`](docs/arquitetura.md).

## Shared

Código e dado usados pelos dois outros projetos:

- `Grid/H3GridBuilder.cs` — gera a grade de 3.210 células H3 a partir dos
  polígonos dos 14 municípios (`Data/rmr_municipios.json`, IBGE, estático).
- `Common/PathHelpers.cs` — resolve caminhos de arquivo de forma estável
  (usado pra achar `Data/` e o histórico local).
- `LeituraMqtt.cs` — contrato de troca de dado: tópico MQTT por célula e formato
  JSON da leitura.

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
- `RiscoIngestorService.cs` assina os tópicos MQTT dos sensores; a cada
  leitura, recalcula só aquela célula o risco e atualiza o cache em memória
  (`RiscoCache.cs`) que a API lê.

Roda como Web API (ASP.NET Core, endpoints mínimos):

```bash
dotnet run --project Platform
curl http://localhost:5128/risco/atual
```

`GET /risco/atual` retorna um GeoJSON (`FeatureCollection`, com
`atualizado_em` e `total_celulas`) com uma `Feature` Polygon por célula
com leitura; nas `properties`:
município, nível (0-3), probabilidade, confiança (cai com a idade da
última leitura), `ultima_leitura` e os fatores (P, M, S).

## Como rodar tudo

```bash
docker compose up -d             # broker MQTT (Mosquitto, porta 1883)
dotnet run --project Platform    # consome, calcula risco, expõe a API
dotnet run --project Dashboard   # mapa em http://localhost:5200
dotnet run --project Sensor      # publica leitura a cada 15 min
```

O dashboard (mapa H3 colorido por nível de risco, células sem leitura
recente esmaecidas) fica em `http://localhost:5200/` e chama a Platform em
`http://localhost:5128` (configurável em `Dashboard/appsettings.json`).

Ou builda a solução inteira de uma vez com `dotnet build`.
