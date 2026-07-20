from __future__ import annotations

import json
import sys
from pathlib import Path

import pandas as pd


source = Path(sys.argv[1])
df = pd.read_csv(source, encoding="utf-8-sig", low_memory=False)

numeric_columns = [
    "effectivePrice",
    "quantity",
    "costInBillingCurrency",
    "costInPricingCurrency",
    "costInUsd",
    "paygCostInBillingCurrency",
    "paygCostInUsd",
    "unitPrice",
]
for column in numeric_columns:
    if column in df.columns:
        df[column] = pd.to_numeric(df[column], errors="coerce").fillna(0)

date_columns = [
    "billingPeriodStartDate",
    "billingPeriodEndDate",
    "servicePeriodStartDate",
    "servicePeriodEndDate",
    "date",
]
for column in date_columns:
    if column in df.columns:
        df[column] = pd.to_datetime(df[column], errors="coerce")


def grouped(columns: list[str], limit: int = 30) -> list[dict]:
    available = [column for column in columns if column in df.columns]
    result = (
        df.groupby(available, dropna=False)
        .agg(
            cost_sek=("costInBillingCurrency", "sum"),
            payg_cost_sek=("paygCostInBillingCurrency", "sum"),
            quantity=("quantity", "sum"),
            rows=("costInBillingCurrency", "size"),
        )
        .reset_index()
        .sort_values("cost_sek", ascending=False)
        .head(limit)
    )
    return json.loads(result.to_json(orient="records", date_format="iso"))


text_columns = [
    "serviceFamily",
    "consumedService",
    "meterName",
    "meterCategory",
    "meterSubCategory",
    "product",
    "productId",
    "resourceGroupName",
    "resourceId",
    "resourceLocation",
    "location",
    "serviceInfo1",
    "serviceInfo2",
    "additionalInfo",
    "provider",
]
search_text = (
    df[[column for column in text_columns if column in df.columns]]
    .fillna("")
    .astype(str)
    .agg(" | ".join, axis=1)
    .str.lower()
)
ai_mask = search_text.str.contains(
    r"foundry|openai|gpt|grok|xai|machine learning|cognitive|language model|tokens",
    regex=True,
)
ai = df.loc[ai_mask].copy()

output = {
    "rows": len(df),
    "columns": list(df.columns),
    "billing_currencies": sorted(df["billingCurrency"].dropna().astype(str).unique().tolist()),
    "pricing_currencies": sorted(df["pricingCurrency"].dropna().astype(str).unique().tolist()),
    "date_min": df["date"].min().isoformat() if df["date"].notna().any() else None,
    "date_max": df["date"].max().isoformat() if df["date"].notna().any() else None,
    "billing_periods": sorted(
        {
            (
                row.billingPeriodStartDate.isoformat() if pd.notna(row.billingPeriodStartDate) else None,
                row.billingPeriodEndDate.isoformat() if pd.notna(row.billingPeriodEndDate) else None,
            )
            for row in df[["billingPeriodStartDate", "billingPeriodEndDate"]].itertuples(index=False)
        }
    ),
    "total_cost_sek": float(df["costInBillingCurrency"].sum()),
    "total_payg_cost_sek": float(df["paygCostInBillingCurrency"].sum()),
    "total_cost_usd": float(df["costInUsd"].sum()),
    "positive_cost_rows": int((df["costInBillingCurrency"] > 0).sum()),
    "zero_cost_rows": int((df["costInBillingCurrency"] == 0).sum()),
    "negative_cost_rows": int((df["costInBillingCurrency"] < 0).sum()),
    "top_service_family": grouped(["serviceFamily"]),
    "top_meter_category": grouped(["meterCategory", "meterSubCategory"]),
    "top_product": grouped(["product"]),
    "top_resource_group": grouped(["resourceGroupName"]),
    "top_resource": grouped(["resourceGroupName", "resourceId"]),
    "top_day": grouped(["date"]),
    "ai_rows": len(ai),
    "ai_cost_sek": float(ai["costInBillingCurrency"].sum()),
    "ai_payg_cost_sek": float(ai["paygCostInBillingCurrency"].sum()),
}

if not ai.empty:
    ai_grouped = lambda columns, limit=50: json.loads(
        ai.groupby(columns, dropna=False)
        .agg(
            cost_sek=("costInBillingCurrency", "sum"),
            payg_cost_sek=("paygCostInBillingCurrency", "sum"),
            quantity=("quantity", "sum"),
            unit=("unitOfMeasure", lambda values: " | ".join(sorted(set(values.dropna().astype(str))))),
            effective_price_min=("effectivePrice", "min"),
            effective_price_max=("effectivePrice", "max"),
            rows=("costInBillingCurrency", "size"),
        )
        .reset_index()
        .sort_values("cost_sek", ascending=False)
        .head(limit)
        .to_json(orient="records", date_format="iso")
    )
    output["ai_by_meter"] = ai_grouped(
        ["meterCategory", "meterSubCategory", "meterName", "product"]
    )
    output["ai_by_resource"] = ai_grouped(
        ["resourceGroupName", "resourceId", "resourceLocation"]
    )
    output["ai_by_day"] = ai_grouped(["date"])
    output["ai_by_service_info"] = ai_grouped(
        ["serviceInfo1", "serviceInfo2", "additionalInfo"], limit=100
    )
    output["ai_rows_detail"] = json.loads(
        ai[
            [
                "date",
                "meterName",
                "meterCategory",
                "meterSubCategory",
                "product",
                "resourceGroupName",
                "resourceId",
                "resourceLocation",
                "effectivePrice",
                "quantity",
                "unitOfMeasure",
                "costInBillingCurrency",
                "paygCostInBillingCurrency",
                "serviceInfo1",
                "serviceInfo2",
                "additionalInfo",
            ]
        ]
        .sort_values("costInBillingCurrency", ascending=False)
        .to_json(orient="records", date_format="iso")
    )

print(json.dumps(output, indent=2, ensure_ascii=False))
