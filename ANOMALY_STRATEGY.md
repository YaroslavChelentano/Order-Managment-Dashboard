# Anomaly detection strategy

## Implemented rules

| Rule | Detection | Severity (heuristic) |
|------|-----------|----------------------|
| `price_mismatch` | `abs(total_price - quantity * unit_price) > 0.01` | **high** |
| `inactive_supplier` | Join `suppliers`; `active = false` | **medium** |
| `negative_quantity` | `quantity < 0` | **high** |
| `timestamp_anomaly` | `updated_at < created_at` | **high** |
| `price_spike` (bonus) | `unit_price > 3 * products.price` | **medium** |
| `after_hours` (bonus) | UTC hour of `created_at` is `>= 22` or `< 6` | **low** |
| `risky_supplier` (bonus) | For the order’s supplier, share of orders matching **any of the four required anomalies** exceeds **50%** | **low** |

Rules are evaluated in a **single SQL** query that builds a PostgreSQL array per order, then rows with at least one flag are returned as `{ order_id, anomaly_types[], severity }`.

## Severity

Severity is a simple function of which rules fired (e.g. data integrity issues → `high`, supplier/price issues → `medium`, time/risk heuristics → `low`). It satisfies the contract that `severity` is one of `low` | `medium` | `high`.

## Data observations

The CSV set includes intentional **price total drift**, **inactive suppliers** with live orders, **returns** as negative quantities, **impossible timestamps**, and **off-hours** timestamps. Bonus rules widen coverage for supplier risk and pricing spikes aligned with `expected-values.json` in the test suite.

## Future improvements

- Materialized view or nightly job for **risky_supplier** if the live subquery becomes hot.
- Configurable thresholds (price spike multiplier, business hours TZ) via configuration.
- Store per-rule metadata (e.g. “expected total vs actual”) for operator UX without changing the public API shape.
