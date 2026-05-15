import { formatCurrency } from "../services/api";
import { getPromoState } from "../utils/promoPricing";

function PromoPriceDisplay({ basePrice, promo, compact = false }) {
  if (!promo) {
    return <span>{formatCurrency(basePrice)}</span>;
  }

  const state = getPromoState(promo);
  const promoPrice = Number(promo.promoPrice);

  return (
    <div className={compact ? "promo-price compact" : "promo-price"}>
      <span className={state === "active" ? "promo-price-active" : "promo-price-base"}>
        {formatCurrency(promoPrice)}
      </span>
      <span className="promo-price-original">Normal: {formatCurrency(basePrice)}</span>
      <span className={`promo-state ${state}`}>
        {state === "active" && `Active until ${promo.endDate}`}
        {state === "upcoming" && `Starts ${promo.startDate}`}
        {state === "expired" && `Expired on ${promo.endDate}`}
      </span>
    </div>
  );
}

export default PromoPriceDisplay;
