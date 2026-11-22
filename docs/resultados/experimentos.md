# Resultados dos experimentos — 2026-11-22 18:55 (UTC-03:00)

Broker `localhost:1883` · Platform `http://localhost:5128` · grade com 3210 células · 16 núcleos · .NET 10.0.15

## E1 · Latência ponta a ponta (publish MQTT → visível em `GET /risco/atual`)

30 leituras na célula `888183995bfffff` (Recife), uma por vez.

| mín | mediana | p95 | máx | média |
|---|---|---|---|---|
| 2 ms | 7 ms | 11 ms | 19 ms | 7 ms |

## E3 · Resposta do modelo a uma tempestade (uma célula, passos de 15 min)

Célula `8881838349fffff` (Recife). Cada linha é uma leitura publicada; os valores são os que a API devolveu.

| passo | chuva (mm) | acum. 1 h | P | M (maré) | S (terreno) | nível | prob. |
|---|---|---|---|---|---|---|---|
| 0 | 0 | 0 | 0.00 | 1.42 | 0.33 | 0 | 0% |
| 1 | 2 | 2 | 0.20 | 1.34 | 0.33 | 0 | 10% |
| 2 | 6 | 8 | 0.80 | 1.25 | 0.33 | 1 | 36% |
| 3 | 12 | 20 | 1.80 | 1.16 | 0.33 | 1 | 65% |
| 4 | 20 | 40 | 3.20 | 1.06 | 0.33 | 2 | 94% |
| 5 | 14 | 54 | 3.40 | 1.00 | 0.33 | 2 | 94% |
| 6 | 6 | 58 | 2.00 | 1.00 | 0.33 | 1 | 63% |
| 7 | 2 | 54 | 1.80 | 1.00 | 0.33 | 1 | 57% |
| 8 | 0 | 42 | 1.40 | 1.00 | 0.33 | 1 | 46% |
| 9 | 0 | 22 | 1.03 | 1.00 | 0.33 | 1 | 36% |
| 10 | 0 | 8 | 1.03 | 1.00 | 0.33 | 1 | 36% |
| 11 | 0 | 2 | 1.03 | 1.00 | 0.33 | 1 | 36% |

## E4 · Confiança pela idade da última leitura

| idade da leitura | confiança devolvida |
|---|---|
| 0 min | alta |
| 15 min | alta |
| 45 min | média |
| 120 min | sem dado recente |

## E2 · Carga: um ciclo com as 3.210 células publicando

| métrica | valor |
|---|---|
| mensagens publicadas (QoS 1) | 3210 |
| tempo para publicar todas | 690 ms |
| tempo até todas visíveis na API | 1.04 s |
| vazão ponta a ponta | 3082 leituras/s |
| tamanho da resposta `GET /risco/atual` | 1.48 MB |
| tempo de resposta da API (mediana de 10) | 8 ms |
