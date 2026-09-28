# Sensor virtual de alagamento: Região Metropolitana do Recife

Sensor virtual de IoT que reporta a chuva em cada célula H3 (~0,74 km²)
dos 14 municípios da RMR, usando dado real da APAC/Cemaden.

Cada célula é, na prática, um sensor independente: lê a chuva mais
próxima da sua posição e reporta o valor bruto. São 3.210 células no
total, cobrindo toda a RMR em resolução H3 8. Os municípios usados pra
montar essa grade vêm de dado estático do IBGE.

O dispositivo roda em duas camadas:

1. Sensoriamento (`Sensing/CemadenClient.cs` + `Sensing/CellReadingAssigner.cs`).
   Busca as leituras de chuva no feed público da APAC/Cemaden, filtra
   pelas estações dentro dos 14 municípios e associa cada uma à célula
   que a contém.
2. Ciclo de vida (`Program.cs`). Repete sensoriamento e saída a cada 15
   minutos, com `while(true)` / `Task.Delay`.

O dashboard mostra a chuva máxima lida por município, agregando as
células daquele município.

## Escopo atual

Essa versão só lê e reporta a chuva bruta (mm) de cada estação. Nenhuma
classificação de risco é feita aqui: isso é regra de negócio, não
sensoriamento, e fica pra uma camada de interpretação separada.

## Como rodar

```bash
dotnet run --once   # um ciclo só
dotnet run           # loop contínuo, a cada 15 min
```

Requer .NET 8+. O NuGet (`H3Lib`) é restaurado automaticamente no
primeiro `dotnet run`/`dotnet build`.

## Estrutura

```
iot-virtual-sensor/
├── Data/rmr_municipios.json         # polígonos dos 14 municípios (IBGE), estático
├── Common/PathHelpers.cs            # acha Data/ a partir de quem chama
├── Grid/H3GridBuilder.cs            # grade H3 a partir dos polígonos, nomes dos municípios
├── Sensing/CemadenClient.cs         # feed APAC/Cemaden, dado ao vivo
├── Sensing/CellReadingAssigner.cs   # associa cada leitura à célula que a contém
├── Program.cs                       # ciclo de vida
└── VirtualSensor.csproj
```
