#!/usr/bin/env bash
# E5 · Resiliência: o que acontece quando a Platform ou o broker reiniciam.
#
#   dotnet build && ./scripts/resiliencia.sh
#
# Sobe o broker limpo e uma Platform com histórico temporário, publica um
# ciclo simulado (3.210 sensores, com retain) e mede:
#   a) Platform reiniciada → quanto tempo até a API voltar com as 3.210 células;
#   b) broker reiniciado → a API continua respondendo? quanto tempo até a
#      Platform reconectar? as leituras retidas sobrevivem (persistência)?
set -euo pipefail
cd "$(dirname "$0")/.."

API=http://localhost:5128/risco/atual
TMP=$(mktemp -d)
PLATFORM_PID=

total() { curl -s -m 5 "$API" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("total_celulas",0))' 2>/dev/null || echo 0; }
agora() { date +%s.%N; }
dt() { python3 -c "print(f'{$2 - $1:.2f}')"; }

subir_platform() {
  : > "$TMP/platform.log"
  (cd Platform && exec dotnet bin/Debug/net10.0/VirtualSensor.Platform.dll \
      --urls http://localhost:5128 --Estado:HistoricoPath="$TMP/historico.jsonl") > "$TMP/platform.log" 2>&1 &
  PLATFORM_PID=$!
}
esperar_total() { # $1 = alvo
  for _ in $(seq 1 600); do [ "$(total)" -ge "$1" ] && return 0; sleep 0.1; done
  echo "timeout esperando $1 células" >&2; return 1
}
trap '[ -n "$PLATFORM_PID" ] && kill $PLATFORM_PID 2>/dev/null || true' EXIT

echo "# E5 · Resiliência — $(date '+%Y-%m-%d %H:%M')"
docker compose down -v >/dev/null 2>&1 || true   # broker sem nenhuma mensagem retida
docker compose up -d >/dev/null 2>&1
sleep 2

subir_platform
until grep -q "Assinando" "$TMP/platform.log"; do sleep 0.2; done
dotnet Sensor/bin/Debug/net10.0/VirtualSensor.Sensor.dll --simular --once >/dev/null
esperar_total 3210
echo
echo "Ciclo simulado publicado (3210 sensores, retain)."

# a) Platform reinicia
kill $PLATFORM_PID; wait $PLATFORM_PID 2>/dev/null || true
t0=$(agora); subir_platform; esperar_total 3210; t1=$(agora)
REINICIO_PLATFORM=$(dt "$t0" "$t1")

# b) broker reinicia com a Platform no ar
falhas=0; consultas=0
t0=$(agora)
docker compose restart mosquitto >/dev/null 2>&1 &
RESTART_PID=$!
until [ "$(grep -c "Assinando" "$TMP/platform.log")" -ge 2 ]; do
  consultas=$((consultas+1))
  [ "$(total)" -eq 3210 ] || falhas=$((falhas+1))
  sleep 0.2
done
t1=$(agora); wait $RESTART_PID
RECONEXAO=$(dt "$t0" "$t1")

# c) retidas sobreviveram ao restart do broker? Platform nova, histórico novo.
kill $PLATFORM_PID; wait $PLATFORM_PID 2>/dev/null || true
rm -f "$TMP/historico.jsonl"
subir_platform; esperar_total 3210
RETIDAS=$(total)

cat <<EOF

| cenário | resultado |
|---|---|
| Platform reiniciada → API de volta com as 3210 células (retain) | ${REINICIO_PLATFORM} s |
| broker reiniciado → Platform reconectada e assinando | ${RECONEXAO} s |
| consultas à API durante a queda do broker que falharam | ${falhas} de ${consultas} |
| leituras retidas após restart do broker (persistência) | ${RETIDAS} de 3210 |
EOF
