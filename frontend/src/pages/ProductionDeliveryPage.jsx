import { Fragment, useEffect, useMemo, useState } from "react";
import {
  addProductionDeliveryProductionEvent,
  addProductionDeliveryStockAdjustmentEvent,
  deleteProductionDeliveryEvent,
  getProductionDeliveryPlan,
  scheduleDelivery,
  updateProductionDeliveryEventQuantities,
  updateProductionDeliveryOpeningStock,
  updateProductionDeliveryOrderDate,
} from "../services/api";

function toNumber(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatDate(value) {
  if (!value) return "";
  const parsed = new Date(`${value}T00:00:00`);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleDateString(undefined, { day: "2-digit", month: "short", year: "numeric" });
}

function formatValue(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed.toFixed(0) : "0";
}

function mapByProduct(items = []) {
  return items.reduce((accumulator, item) => {
    accumulator[item.productId] = toNumber(item.quantity);
    return accumulator;
  }, {});
}

function cloneQuantities(products, values) {
  return products.map((product) => ({
    productId: product.productId,
    quantity: toNumber(values[product.productId] ?? 0),
  }));
}

function ProductionDeliveryPage() {
  const [plan, setPlan] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [saving, setSaving] = useState({});
  const [schedulingId, setSchedulingId] = useState(null);
  const [pendingDates, setPendingDates] = useState({});

  async function loadPlan() {
    setLoading(true);
    setError("");

    try {
      const response = await getProductionDeliveryPlan();
      setPlan(response);

      const nextDates = {};
      (response?.events || []).forEach((event) => {
        if (event.eventType === "Order") {
          nextDates[event.id] = event.plannedDeliveryDate || "";
        }
      });
      setPendingDates(nextDates);
    } catch (requestError) {
      setPlan(null);
      setError(requestError.message || "Unable to load Production / Delivery plan");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void loadPlan();
  }, []);

  const products = plan?.products || [];
  const events = plan?.events || [];

  const openingEvent = useMemo(() => events.find((event) => event.eventType === "OpeningStock") || null, [events]);

  function setEventSaving(eventId, field, value) {
    setSaving((current) => ({
      ...current,
      [eventId]: {
        ...(current[eventId] || {}),
        [field]: value,
      },
    }));
  }

  function isSaving(eventId, field) {
    return Boolean(saving[eventId]?.[field]);
  }

  async function saveOpeningStock() {
    if (!openingEvent) return;

    setEventSaving(openingEvent.id, "opening", true);
    try {
      await updateProductionDeliveryOpeningStock(cloneQuantities(products, mapByProduct(openingEvent.productQuantities)));
      await loadPlan();
    } finally {
      setEventSaving(openingEvent.id, "opening", false);
    }
  }

  async function saveEventQuantities(event) {
    setEventSaving(event.id, "quantities", true);
    try {
      await updateProductionDeliveryEventQuantities(event.id, event.productQuantities || []);
      await loadPlan();
    } finally {
      setEventSaving(event.id, "quantities", false);
    }
  }

  async function saveOrderDate(event) {
    setEventSaving(event.id, "date", true);
    try {
      await updateProductionDeliveryOrderDate(event.id, pendingDates[event.id] || null);
      await loadPlan();
    } finally {
      setEventSaving(event.id, "date", false);
    }
  }

  async function handleSchedule(event) {
    if (!event.orderId) return;

    setSchedulingId(event.id);
    try {
      await scheduleDelivery({
        orderId: event.orderId,
        deliveryDate: pendingDates[event.id] || event.plannedDeliveryDate || event.orderDate,
        notes: null,
      });
      await loadPlan();
    } finally {
      setSchedulingId(null);
    }
  }

  async function addProduction(afterEventId) {
    setEventSaving(afterEventId, "insert", true);
    try {
      await addProductionDeliveryProductionEvent(afterEventId);
      await loadPlan();
    } finally {
      setEventSaving(afterEventId, "insert", false);
    }
  }

  async function addAdjustment(afterEventId) {
    setEventSaving(afterEventId, "insertAdjustment", true);
    try {
      await addProductionDeliveryStockAdjustmentEvent(afterEventId);
      await loadPlan();
    } finally {
      setEventSaving(afterEventId, "insertAdjustment", false);
    }
  }

  async function removeEvent(eventId) {
    setEventSaving(eventId, "delete", true);
    try {
      await deleteProductionDeliveryEvent(eventId);
      await loadPlan();
    } finally {
      setEventSaving(eventId, "delete", false);
    }
  }

  function updateQuantitiesForEvent(eventId, productId, value) {
    setPlan((current) => {
      if (!current) return current;

      return {
        ...current,
        events: current.events.map((event) => {
          if (event.id !== eventId) return event;

          const nextQuantities = mapByProduct(event.productQuantities);
          nextQuantities[productId] = value === "" ? 0 : toNumber(value);

          return {
            ...event,
            productQuantities: cloneQuantities(products, nextQuantities),
          };
        }),
      };
    });
  }

  function updatePendingDate(eventId, value) {
    setPendingDates((current) => ({ ...current, [eventId]: value }));
  }

  function renderCellValue(event, productId) {
    const stockValue = (event.stockAfter || []).find((entry) => entry.productId === productId)?.quantity;
    const quantityValue = (event.productQuantities || []).find((entry) => entry.productId === productId)?.quantity;
    return { stockValue, quantityValue };
  }

  return (
    <section className="production-delivery-page">
      <header className="page-header">
        <h2>Production / Delivery</h2>
        <p>Persistent Excel-style planning that reuses the live production and delivery workflow.</p>
      </header>

      {error && <p className="alert error">{error}</p>}

      <div className="panel production-delivery-table-panel">
        <div className="table-wrap production-delivery-table-wrap">
          <table className="production-delivery-table">
            <thead>
              <tr>
                <th className="sticky-col sticky-col-1">Order No.</th>
                <th className="sticky-col sticky-col-2">DC</th>
                <th className="sticky-col sticky-col-3">Order Date</th>
                <th className="sticky-col sticky-col-4">Delivery Date</th>
                {products.map((product) => (
                  <th key={product.productId}>{product.productName || product.productCode || `Product ${product.productId}`}</th>
                ))}
                <th className="sticky-action-col">Action</th>
              </tr>
            </thead>
            <tbody>
              {!loading && openingEvent && (
                <tr className="production-delivery-row opening-row">
                  <td className="sticky-col sticky-col-1" colSpan={4}>
                    <strong>OPENING STOCK</strong>
                  </td>
                  {products.map((product) => {
                    const value = (openingEvent.productQuantities || []).find((entry) => entry.productId === product.productId)?.quantity ?? 0;
                    return (
                      <td key={product.productId}>
                        <input
                          type="number"
                          step="0.01"
                          value={value}
                          onChange={(event) => updateQuantitiesForEvent(openingEvent.id, product.productId, event.target.value)}
                        />
                      </td>
                    );
                  })}
                  <td className="sticky-action-col">
                    <button type="button" className="secondary" onClick={saveOpeningStock} disabled={isSaving(openingEvent.id, "opening")}>
                      {isSaving(openingEvent.id, "opening") ? "Saving..." : "Save"}
                    </button>
                  </td>
                </tr>
              )}

              {events.filter((event) => event.eventType !== "OpeningStock").map((event) => {
                const quantities = mapByProduct(event.productQuantities || []);
                const before = mapByProduct(event.stockBefore || []);
                const after = mapByProduct(event.stockAfter || []);
                const isOrder = event.eventType === "Order";
                const isProduction = event.eventType === "Production";
                const isAdjustment = event.eventType === "StockAdjustment";

                return (
                  <Fragment key={event.id}>
                    <tr className={isOrder ? "production-delivery-row order-row" : "production-delivery-row event-row"}>
                      <td className="sticky-col sticky-col-1">{isOrder ? event.orderNumber : event.eventType}</td>
                      <td className="sticky-col sticky-col-2">{isOrder ? event.distributionCentreName || "" : ""}</td>
                      <td className="sticky-col sticky-col-3">{isOrder ? formatDate(event.orderDate) : ""}</td>
                      <td className="sticky-col sticky-col-4">
                        {isOrder ? (
                          <input
                            type="date"
                            value={pendingDates[event.id] || ""}
                            onChange={(e) => updatePendingDate(event.id, e.target.value)}
                          />
                        ) : (
                          ""
                        )}
                      </td>
                      {products.map((product) => (
                        <td key={product.productId}>
                          {isProduction || isAdjustment ? (
                            <input
                              type="number"
                              step="0.01"
                              value={quantities[product.productId] ?? 0}
                              onChange={(e) => updateQuantitiesForEvent(event.id, product.productId, e.target.value)}
                            />
                          ) : (
                            <span>{formatValue(quantities[product.productId] ?? 0)}</span>
                          )}
                        </td>
                      ))}
                      <td className="sticky-action-col">
                        <div className="action-stack">
                          {isOrder && (
                            <button type="button" onClick={() => void saveOrderDate(event)} disabled={isSaving(event.id, "date") || !event.orderId}>
                              {isSaving(event.id, "date") ? "Saving..." : "Save Date"}
                            </button>
                          )}
                          {(isProduction || isAdjustment) && (
                            <button type="button" className="secondary" onClick={() => void saveEventQuantities(event)} disabled={isSaving(event.id, "quantities")}>
                              {isSaving(event.id, "quantities") ? "Saving..." : "Save"}
                            </button>
                          )}
                          {(isProduction || isAdjustment || isOrder) && (
                            <button type="button" className="secondary" onClick={() => void addAdjustment(event.id)} disabled={isSaving(event.id, "insertAdjustment")}>
                              + Stock Adjustment
                            </button>
                          )}
                          {(isProduction || isAdjustment) && (
                            <button type="button" className="danger" onClick={() => void removeEvent(event.id)} disabled={isSaving(event.id, "delete")}>
                              {isSaving(event.id, "delete") ? "Deleting..." : "Delete"}
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>

                    <tr className={isOrder ? "stock-after-row order-stock-row" : "stock-after-row"}>
                      <td className="sticky-col sticky-col-1"><strong>STOCK AFTER</strong></td>
                      <td className="sticky-col sticky-col-2">{isOrder ? event.scheduleStatus : ""}</td>
                      <td className="sticky-col sticky-col-3" />
                      <td className="sticky-col sticky-col-4" />
                      {products.map((product) => {
                        const afterValue = after[product.productId] ?? 0;
                        const beforeValue = before[product.productId] ?? 0;
                        return (
                          <td key={product.productId} className={afterValue < 0 ? "shortage-cell" : ""}>
                            <div className="stock-stack">
                              <strong>{formatValue(afterValue)}</strong>
                              <span className="status-text">{formatValue(beforeValue)} → {formatValue(quantities[product.productId] ?? 0)}</span>
                            </div>
                          </td>
                        );
                      })}
                      <td className="sticky-action-col">
                        <div className="action-stack">
                          <button type="button" className="secondary" onClick={() => void addProduction(event.id)} disabled={isSaving(event.id, "insert") }>
                            + Add Production
                          </button>
                          {isOrder && (
                            <button type="button" onClick={() => void handleSchedule(event)} disabled={schedulingId === event.id || !event.orderId}>
                              {schedulingId === event.id ? "Scheduling..." : "Schedule"}
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
    </section>
  );
}

export default ProductionDeliveryPage;