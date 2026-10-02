"""C4 design repair: derives the objective name families' calibration (§15.4) from the aggregate name censuses.

Usage:  python calibrate.py census/system.json census/data.json > census/calibration.json

For each census ("system": the reference machine's system drive; "data": a data volume of the same machine) it derives
  length_quantiles  the file-name length in UTF-16 code units at the 0th to 100th percentile (inverse CDF of the histogram)
  beta              the repeat skew of the generator (a repeat occurrence takes vocabulary rank floor(V * u^beta)) that reproduces
                    the census's share of files carried by the most frequent 1% of distinct names, at the census's own distinct ratio
  model_check       the generator model's predicted top-0.1% share and share of distinct names seen once, beside the census's
Only aggregates are read and written.
"""
import json
import math
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


def quantiles(hist):
    total = sum(hist)
    out = []
    for p in range(101):
        target = max(1, math.ceil(p / 100 * total))
        acc = 0
        for length, count in enumerate(hist):
            acc += count
            if acc >= target:
                out.append(length)
                break
    out[0] = next(i for i, c in enumerate(hist) if c)
    return out


def top_share(d, beta, fraction):
    """Share of files carried by the top `fraction` of vocabulary ranks: first occurrences plus the repeats that land there."""
    repeats_per_name = (1 - d) / d          # R / V
    files_per_name = 1 / d                  # n / V
    return (fraction + repeats_per_name * fraction ** (1 / beta)) / files_per_name


def singleton_share(d, beta, steps=200_000):
    """Expected share of vocabulary entries that occur exactly once (Poisson approximation of the repeats)."""
    r = (1 - d) / d
    acc = 0.0
    for i in range(steps):
        x = (i + 0.5) / steps
        lam = r / beta * x ** (1 / beta - 1)
        acc += math.exp(-lam)
    return acc / steps


def fit_beta(d, target_top1):
    lo, hi = 1.0, 200.0
    for _ in range(200):
        mid = (lo + hi) / 2
        if top_share(d, mid, 0.01) < target_top1:
            lo = mid
        else:
            hi = mid
    return (lo + hi) / 2


def model(census):
    d = census['distinct_ratio']
    top1 = census['frequency']['files_in_top_1pct_names']
    beta = fit_beta(d, top1)
    freq = census['frequency']['distinct_names_by_occurrences']
    return {
        'label': census['label'],
        'files': census['files'],
        'distinct_ratio': d,
        'mean_length': census['length']['mean'],
        'length_quantiles': quantiles(census['length']['histogram']),
        'beta': round(beta, 3),
        'model_check': {
            'top_1pct_share': {'census': top1, 'model': round(top_share(d, beta, 0.01), 4)},
            'top_0.1pct_share': {'census': census['frequency']['files_in_top_0.1pct_names'], 'model': round(top_share(d, beta, 0.001), 4)},
            'top_10pct_share': {'census': census['frequency']['files_in_top_10pct_names'], 'model': round(top_share(d, beta, 0.1), 4)},
            'names_seen_once': {'census': round(freq['once'] / census['distinct_file_names'], 4), 'model': round(singleton_share(d, beta), 4)},
        },
    }


if __name__ == '__main__':
    system = json.load(open(sys.argv[1], encoding='utf-8'))
    data = json.load(open(sys.argv[2], encoding='utf-8'))
    print(json.dumps({'system': model(system), 'data': model(data)}, indent=1))
