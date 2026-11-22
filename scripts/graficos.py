#!/usr/bin/env python3
"""Gera os gráficos dos testes experimentais a partir dos CSVs.

    python3 scripts/graficos.py [pasta]      # padrão: docs/resultados

Lê escalabilidade.csv, estabilidade.csv e modelo.csv e grava PNGs em
<pasta>/graficos/. Chamado no fim de scripts/experimentos.sh.
"""
import csv
import statistics
import sys
from collections import defaultdict
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

PASTA = Path(sys.argv[1] if len(sys.argv) > 1 else "docs/resultados")
SAIDA = PASTA / "graficos"

# Paleta categórica fixa (slot 1 azul, slot 2 laranja), superfície e tintas.
AZUL, LARANJA = "#2a78d6", "#eb6834"
SUPERFICIE, TEXTO, TEXTO_2, GRADE = "#fcfcfb", "#0b0b0b", "#52514e", "#e4e3df"

plt.rcParams.update({
    "figure.facecolor": SUPERFICIE, "axes.facecolor": SUPERFICIE,
    "savefig.facecolor": SUPERFICIE, "font.size": 10,
    "text.color": TEXTO, "axes.labelcolor": TEXTO_2, "axes.edgecolor": GRADE,
    "xtick.color": TEXTO_2, "ytick.color": TEXTO_2,
    "axes.grid": True, "axes.axisbelow": True, "grid.color": GRADE, "grid.linewidth": 0.8,
    "axes.spines.top": False, "axes.spines.right": False,
    "axes.titlesize": 11, "axes.titleweight": "bold", "axes.titlelocation": "left",
    "legend.frameon": False, "lines.linewidth": 2, "lines.markersize": 6,
})


def ler(nome):
    with open(PASTA / nome, newline="") as f:
        return list(csv.DictReader(f))


def mediana_por_n(linhas, campo):
    grupos = defaultdict(list)
    for linha in linhas:
        grupos[int(linha["n_sensores"])].append(float(linha[campo]))
    ns = sorted(grupos)
    return ns, [statistics.median(grupos[n]) for n in ns]


def rotular_fim(ax, xs, ys, texto, cor):
    ax.annotate(texto, (xs[-1], ys[-1]), xytext=(6, 0), textcoords="offset points",
                va="center", fontsize=9, color=TEXTO_2)
    ax.plot(xs[-1], ys[-1], "o", color=cor, markeredgecolor=SUPERFICIE, markeredgewidth=2)


def escalabilidade():
    execucoes = ler("escalabilidade.csv")
    mensagens = ler("escalabilidade_mensagens.csv")

    por_n = defaultdict(list)
    for m in mensagens:
        por_n[int(m["n_sensores"])].append(float(m["latencia_ms"]))
    ns = sorted(por_n)
    xs = list(range(len(ns)))
    p50 = [statistics.median(por_n[n]) for n in ns]
    p95 = [statistics.quantiles(por_n[n], n=20)[-1] if len(por_n[n]) > 1 else por_n[n][0] for n in ns]

    fig, eixos = plt.subplots(1, 3, figsize=(14, 4.2), constrained_layout=True)
    fig.suptitle("E2 · Escalabilidade: N sensores publicando no mesmo ciclo (5 repetições)",
                 x=0.01, ha="left", fontweight="bold")

    ax = eixos[0]
    ax.plot(xs, p50, "-o", color=AZUL, label="p50")
    ax.plot(xs, p95, "-o", color=LARANJA, label="p95")
    rotular_fim(ax, xs, p50, "p50", AZUL)
    rotular_fim(ax, xs, p95, "p95", LARANJA)
    ax.set_title("Latência por mensagem (publish → risco calculado)")
    ax.set_ylabel("ms")

    ax = eixos[1]
    _, ciclo = mediana_por_n(execucoes, "ciclo_pipeline_ms")
    _, visivel = mediana_por_n(execucoes, "visivel_api_ms")
    ax.plot(xs, [c / 1000 for c in ciclo], "-o", color=AZUL, label="ciclo no pipeline")
    ax.plot(xs, [v / 1000 for v in visivel], "-o", color=LARANJA, label="todas visíveis na API")
    rotular_fim(ax, xs, [c / 1000 for c in ciclo], "pipeline", AZUL)
    rotular_fim(ax, xs, [v / 1000 for v in visivel], "na API", LARANJA)
    ax.set_title("Tempo para processar o ciclo inteiro")
    ax.set_ylabel("s")

    ax = eixos[2]
    _, api = mediana_por_n(execucoes, "api_resposta_ms")
    _, mb = mediana_por_n(execucoes, "api_mb")
    ax.plot(xs, api, "-o", color=AZUL)
    rotular_fim(ax, xs, api, f"{mb[-1]:.1f} MB", AZUL)
    ax.set_title("Resposta de GET /risco/atual")
    ax.set_ylabel("ms (mediana de 10 consultas)")

    for ax in eixos:
        ax.set_xticks(xs, [str(n) for n in ns])
        ax.set_xlabel("sensores publicando no ciclo")
        ax.set_ylim(bottom=0)
        ax.margins(x=0.15)
    eixos[0].legend(loc="upper right")
    eixos[1].legend(loc="upper left")
    fig.savefig(SAIDA / "escalabilidade.png", dpi=150)
    plt.close(fig)


def estabilidade():
    linhas = ler("estabilidade.csv")
    ciclos = [int(l["ciclo"]) for l in linhas]
    p50 = [float(l["latencia_p50_ms"]) for l in linhas]
    p95 = [float(l["latencia_p95_ms"]) for l in linhas]
    rss = [float(l["platform_rss_mb"]) for l in linhas if l["platform_rss_mb"]]

    paineis = 2 if len(rss) == len(ciclos) else 1
    fig, eixos = plt.subplots(1, paineis, figsize=(5.5 * paineis, 4), constrained_layout=True, squeeze=False)
    fig.suptitle("E6 · Estabilidade: ciclos seguidos com as 3.210 células", x=0.01, ha="left", fontweight="bold")

    ax = eixos[0][0]
    ax.plot(ciclos, p50, "-o", color=AZUL, label="p50")
    ax.plot(ciclos, p95, "-o", color=LARANJA, label="p95")
    ax.set_title("Latência por mensagem em cada ciclo")
    ax.set_ylabel("ms")
    ax.legend(loc="upper left")

    if paineis == 2:
        ax = eixos[0][1]
        ax.plot(ciclos, rss, "-o", color=AZUL)
        ax.set_title("Memória da Platform (RSS)")
        ax.set_ylabel("MB")

    for ax in eixos[0]:
        ax.set_xlabel("ciclo")
        ax.set_ylim(bottom=0)
        ax.set_xticks(ciclos[::2] if len(ciclos) > 10 else ciclos)
    fig.savefig(SAIDA / "estabilidade.png", dpi=150)
    plt.close(fig)


def modelo():
    linhas = ler("modelo.csv")
    passos = [int(l["passo"]) for l in linhas]
    chuva = [float(l["chuva_mm"]) for l in linhas]
    nivel = [int(l["nivel"]) for l in linhas]

    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(7, 5), sharex=True, constrained_layout=True,
                                   gridspec_kw={"height_ratios": [3, 2]})
    fig.suptitle("E3 · Resposta do modelo a uma tempestade (passos de 15 min)", x=0.01, ha="left", fontweight="bold")

    ax1.bar(passos, chuva, width=0.6, color=AZUL)
    ax1.set_title("Chuva publicada pelo sensor")
    ax1.set_ylabel("mm em 15 min")

    ax2.step(passos, nivel, where="mid", color=LARANJA)
    ax2.plot(passos, nivel, "o", color=LARANJA, markeredgecolor=SUPERFICIE, markeredgewidth=2)
    ax2.set_title("Nível de risco devolvido pela API")
    ax2.set_yticks([0, 1, 2, 3], ["0 baixo", "1 moderado", "2 alto", "3 crítico"])
    ax2.set_ylim(-0.3, 3.3)
    ax2.set_xlabel("passo (15 min cada)")
    ax2.set_xticks(passos)
    fig.savefig(SAIDA / "modelo.png", dpi=150)
    plt.close(fig)


if __name__ == "__main__":
    SAIDA.mkdir(parents=True, exist_ok=True)
    escalabilidade()
    estabilidade()
    modelo()
    print(f"Gráficos em {SAIDA}/")
