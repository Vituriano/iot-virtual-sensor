# E5 · Resiliência — 2026-11-22 18:59

Ciclo simulado publicado (3210 sensores, retain).

| cenário | resultado |
|---|---|
| Platform reiniciada → API de volta com as 3210 células (retain) | 5.75 s |
| broker reiniciado → Platform reconectada e assinando | 5.20 s |
| consultas à API durante a queda do broker que falharam | 0 de 18 |
| leituras retidas após restart do broker (persistência) | 3210 de 3210 |
