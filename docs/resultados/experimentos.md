# Resultados dos experimentos — 2026-11-22 19:28 (UTC-03:00)

Broker `localhost:1883` · Platform `http://localhost:5128` · grade com 3210 células · 16 núcleos · .NET 10.0.15 · Ubuntu 26.04.1 LTS

Dados brutos nos `.csv` desta pasta; gráficos em `graficos/`.

## E1 · Latência de uma leitura, com as 3.210 células ativas

30 leituras na célula `888183995bfffff` (Recife), uma por vez. Arquivo: `latencia.csv`.

| medida | p50 | p95 | máx |
|---|---|---|---|
| publish → risco calculado na Platform | 1.2 ms | 1.8 ms | 1.9 ms |
| publish → visível em `GET /risco/atual` | 60 ms | 126 ms | 139 ms |

## E2 · Escalabilidade: N sensores publicando no mesmo ciclo (5 repetições)

Latência por mensagem = publish → risco calculado (todas as mensagens das repetições juntas). Ciclo, vazão e API = mediana das repetições. Arquivos: `escalabilidade.csv` (por execução) e `escalabilidade_mensagens.csv` (por mensagem).

| sensores | latência p50 | latência p95 | latência máx | ciclo no pipeline | visível na API | vazão | resposta da API | tamanho da API |
|---|---|---|---|---|---|---|---|---|
| 1 | 1.9 ms | 2.3 ms | 2.3 ms | 1.9 ms | 4.7 ms | — | 0.6 ms | 3 KB |
| 10 | 1.2 ms | 2.3 ms | 3.6 ms | 6.9 ms | 20 ms | — | 0.3 ms | 8 KB |
| 100 | 0.2 ms | 1.0 ms | 2.1 ms | 21 ms | 37 ms | 4677 msg/s | 1.0 ms | 51 KB |
| 500 | 0.3 ms | 1.5 ms | 14 ms | 112 ms | 139 ms | 4482 msg/s | 5.1 ms | 251 KB |
| 1000 | 0.3 ms | 0.7 ms | 7.8 ms | 265 ms | 293 ms | 3777 msg/s | 8.8 ms | 500 KB |
| 2000 | 0.3 ms | 0.6 ms | 6.5 ms | 516 ms | 571 ms | 3874 msg/s | 20 ms | 1003 KB |
| 3210 | 0.3 ms | 0.7 ms | 13 ms | 833 ms | 919 ms | 3851 msg/s | 34 ms | 1.57 MB |

## E3 · Resposta do modelo a uma tempestade (uma célula, passos de 15 min)

Célula `8881839807fffff` (Recife). Cada linha é uma leitura publicada; os valores são os que a API devolveu. Arquivo: `modelo.csv`.

| passo | chuva (mm) | acum. 1 h | P | M (maré) | S (terreno) | R | nível | prob. |
|---|---|---|---|---|---|---|---|---|
| 0 | 0 | 0 | 0.00 | 1.24 | 0.71 | 0.00 | 0 | 0% |
| 1 | 2 | 2 | 0.20 | 1.15 | 0.71 | 0.16 | 0 | 18% |
| 2 | 6 | 8 | 0.80 | 1.05 | 0.71 | 0.60 | 1 | 58% |
| 3 | 12 | 20 | 1.80 | 1.00 | 0.71 | 1.28 | 3 | 100% |
| 4 | 20 | 40 | 3.20 | 1.00 | 0.71 | 2.28 | 3 | 100% |
| 5 | 14 | 54 | 3.40 | 1.00 | 0.71 | 2.43 | 3 | 100% |
| 6 | 6 | 58 | 2.00 | 1.00 | 0.71 | 1.43 | 3 | 100% |
| 7 | 2 | 54 | 1.80 | 1.00 | 0.71 | 1.28 | 3 | 100% |
| 8 | 0 | 42 | 1.40 | 1.00 | 0.71 | 1.00 | 2 | 87% |
| 9 | 0 | 22 | 1.03 | 1.00 | 0.71 | 0.74 | 2 | 69% |
| 10 | 0 | 8 | 1.03 | 1.00 | 0.71 | 0.74 | 2 | 69% |
| 11 | 0 | 2 | 1.03 | 1.00 | 0.71 | 0.74 | 2 | 69% |

## E4 · Confiança pela idade da última leitura

| idade da leitura | confiança devolvida |
|---|---|
| 0 min | alta |
| 15 min | alta |
| 45 min | média |
| 120 min | sem dado recente |

## E6 · Estabilidade: 20 ciclos seguidos com as 3.210 células

Um ciclo a cada ~2 s (o real é a cada 15 min). Arquivo: `estabilidade.csv`.

| ciclo | latência p50 | latência p95 | ciclo no pipeline | visível na API | memória da Platform |
|---|---|---|---|---|---|
| 1 | 0.2 ms | 0.4 ms | 489 ms | 513 ms | 152 MB |
| 2 | 0.3 ms | 0.5 ms | 789 ms | 839 ms | 152 MB |
| 3 | 0.3 ms | 0.5 ms | 733 ms | 795 ms | 152 MB |
| 4 | 0.3 ms | 0.5 ms | 709 ms | 770 ms | 152 MB |
| 5 | 0.2 ms | 0.7 ms | 560 ms | 600 ms | 152 MB |
| 6 | 0.3 ms | 0.6 ms | 695 ms | 765 ms | 152 MB |
| 7 | 0.3 ms | 0.7 ms | 853 ms | 915 ms | 152 MB |
| 8 | 0.3 ms | 0.8 ms | 658 ms | 716 ms | 157 MB |
| 9 | 0.2 ms | 0.4 ms | 547 ms | 607 ms | 157 MB |
| 10 | 0.3 ms | 0.5 ms | 711 ms | 757 ms | 157 MB |
| 11 | 0.3 ms | 0.4 ms | 683 ms | 733 ms | 157 MB |
| 12 | 0.3 ms | 0.5 ms | 710 ms | 767 ms | 157 MB |
| 13 | 0.3 ms | 0.5 ms | 735 ms | 782 ms | 157 MB |
| 14 | 0.3 ms | 0.4 ms | 661 ms | 797 ms | 157 MB |
| 15 | 0.2 ms | 0.4 ms | 663 ms | 727 ms | 161 MB |
| 16 | 0.2 ms | 0.4 ms | 695 ms | 757 ms | 161 MB |
| 17 | 0.2 ms | 0.4 ms | 677 ms | 735 ms | 161 MB |
| 18 | 0.2 ms | 0.5 ms | 546 ms | 564 ms | 161 MB |
| 19 | 0.2 ms | 0.4 ms | 615 ms | 675 ms | 161 MB |
| 20 | 0.2 ms | 0.5 ms | 650 ms | 711 ms | 161 MB |
