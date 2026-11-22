#!/usr/bin/env bash
# Testes experimentais E1–E4 e E6 num ambiente limpo, com um comando:
#
#   ./scripts/experimentos.sh            # resultados em docs/resultados/
#
# Compila, sobe o broker sem nenhuma mensagem retida e uma Platform com
# histórico temporário, roda o projeto Experimentos (~4 min) e gera os
# gráficos. Saída: CSVs brutos, experimentos.md e graficos/.
#
# Atenção: recria o broker (docker compose down -v), o que apaga as
# leituras retidas de execuções anteriores do Sensor.
set -euo pipefail
cd "$(dirname "$0")/.."

SAIDA=${1:-docs/resultados}
TMP=$(mktemp -d)
PLATFORM_PID=

if (echo > /dev/tcp/127.0.0.1/5128) 2>/dev/null; then
  echo "A porta 5128 está em uso: pare a Platform que estiver rodando e tente de novo." >&2
  exit 1
fi
trap '[ -n "$PLATFORM_PID" ] && kill $PLATFORM_PID 2>/dev/null || true' EXIT

echo "Compilando..."
dotnet build --nologo -v q >/dev/null

echo "Subindo broker limpo e Platform..."
docker compose down -v >/dev/null 2>&1 || true
docker compose up -d >/dev/null 2>&1
(cd Platform && exec dotnet bin/Debug/net10.0/VirtualSensor.Platform.dll \
    --urls http://localhost:5128 --Estado:HistoricoPath="$TMP/historico.jsonl") > "$TMP/platform.log" 2>&1 &
PLATFORM_PID=$!
until grep -q "Assinando" "$TMP/platform.log"; do
  kill -0 "$PLATFORM_PID" 2>/dev/null || { cat "$TMP/platform.log" >&2; exit 1; }
  sleep 0.2
done

echo "Rodando os experimentos..."
PLATFORM_PID=$PLATFORM_PID dotnet Experimentos/bin/Debug/net10.0/VirtualSensor.Experimentos.dll "$SAIDA" >/dev/null

if python3 -c "import matplotlib" 2>/dev/null; then
  python3 scripts/graficos.py "$SAIDA"
else
  echo "matplotlib não encontrado: gráficos não gerados (pip install matplotlib)."
fi
echo "Pronto: $SAIDA/experimentos.md"
