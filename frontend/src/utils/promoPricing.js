export function normalizePromo(input = {}) {
  return {
    id: input.id ?? `promo-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    productId: Number(input.productId),
    distributionCentreIds: Array.isArray(input.distributionCentreIds)
      ? [...new Set(input.distributionCentreIds.map((value) => Number(value)).filter((value) => Number.isFinite(value)))]
      : [],
    promoPrice: Number(input.promoPrice),
    startDate: String(input.startDate ?? "").slice(0, 10),
    endDate: String(input.endDate ?? "").slice(0, 10),
    updatedAt: input.updatedAt ?? new Date().toISOString(),
  };
}

export function isValidPromo(promo) {
  return (
    Number.isFinite(Number(promo?.productId)) &&
    Array.isArray(promo?.distributionCentreIds) &&
    promo.distributionCentreIds.length > 0 &&
    Number.isFinite(Number(promo?.promoPrice)) &&
    Number(promo.promoPrice) > 0 &&
    Boolean(promo?.startDate) &&
    Boolean(promo?.endDate)
  );
}

export function getPromoState(promo, dateValue = new Date()) {
  const start = new Date(`${promo?.startDate}T00:00:00`);
  const end = new Date(`${promo?.endDate}T23:59:59`);

  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) {
    return "invalid";
  }

  if (dateValue < start) {
    return "upcoming";
  }

  if (dateValue > end) {
    return "expired";
  }

  return "active";
}

export function findActivePromoPrice(promos, productId, distributionCentreId, dateValue = new Date()) {
  const normalizedProductId = Number(productId);
  const normalizedDcId = Number(distributionCentreId);

  if (!Array.isArray(promos) || !Number.isFinite(normalizedProductId) || !Number.isFinite(normalizedDcId)) {
    return null;
  }

  const candidates = promos.filter((promo) => {
    if (Number(promo.productId) !== normalizedProductId) {
      return false;
    }

    if (!Array.isArray(promo.distributionCentreIds) || !promo.distributionCentreIds.includes(normalizedDcId)) {
      return false;
    }

    return getPromoState(promo, dateValue) === "active";
  });

  if (!candidates.length) {
    return null;
  }

  return candidates.sort((left, right) => {
    const leftDate = new Date(left.updatedAt ?? 0).getTime();
    const rightDate = new Date(right.updatedAt ?? 0).getTime();
    return rightDate - leftDate;
  })[0];
}
