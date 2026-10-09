import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import {
  addOrderToPlan,
  addProductionDeliveryProductionEvent,
  addProductionDeliveryStockAdjustmentEvent,
  deleteProductionDeliveryEvent,
  getExcludedOrdersFromPlan,
  getProductionDeliveryPlan,
  removeOrderFromPlan,
  scheduleDelivery,
  setOrderEnRoute,
  updateProductionDeliveryEventQuantities,
  updateProductionDeliveryOpeningStock,
  updateProductionDeliveryOrderDate,
} from "../services/api";
import StatusLabel from "../components/StatusLabel";
import { businessTimeInput, formatBusinessDateTime, isDeliveryLocked } from "../utils/date";

const EMPTY_EVENTS = [];

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
  const [excludedOrders, setExcludedOrders] = useState([]);
  const [removingFromPlan, setRemovingFromPlan] = useState(null);
  const [addingToPlan, setAddingToPlan] = useState(null);
  const [dispatchEventId, setDispatchEventId] = useState(null);
  const [durationHours, setDurationHours] = useState("");
  const [departureTime, setDepartureTime] = useState("");
  const plannerContainerRef = useRef(null);
  const plannerTableRef = useRef(null);
  const dirtyDateIdsRef = useRef(new Set());

  useLayoutEffect(() => {
    const container = plannerContainerRef.current;
    const table = plannerTableRef.current;
    let animationFrame;

    function fitPlanner() {
      const width = Math.max(table.offsetWidth, table.scrollWidth);
      if (!container.clientWidth || !width) return;

      const scale = Math.min(1, container.clientWidth / width);
      container.style.setProperty("--planner-scale", String(scale));
      container.style.setProperty("--planner-height", `${Math.ceil((table.offsetHeight + 1) * scale)}px`);
    }

    function scheduleFit() {
      cancelAnimationFrame(animationFrame);
      animationFrame = requestAnimationFrame(fitPlanner);
    }

    fitPlanner();
    const observer = new ResizeObserver(scheduleFit);
    observer.observe(container);
    observer.observe(table);
    window.addEventListener("resize", scheduleFit);
    window.visualViewport?.addEventListener("resize", scheduleFit);

    return () => {
      cancelAnimationFrame(animationFrame);
      observer.disconnect();
      window.removeEventListener("resize", scheduleFit);
      window.visualViewport?.removeEventListener("resize", scheduleFit);
    };
  }, []);

  async function loadPlan() {
    setLoading(true);
    setError("");

    try {
      const [response, excluded] = await Promise.all([
        getProductionDeliveryPlan(),
        getExcludedOrdersFromPlan(),
      ]);
      setPlan(response);
      setExcludedOrders(excluded || []);

      const nextDates = {};
      (response?.events || []).forEach((event) => {
        if (event.eventType === "Order") {
          nextDates[event.id] = event.plannedDeliveryDate || "";
        }
      });
      setPendingDates(nextDates);
      dirtyDateIdsRef.current.clear();
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

  useEffect(() => {
    let current = true;
    async function refreshStatuses() {
      try {
        const response = await getProductionDeliveryPlan();
        if (!current) return;
        const latestById = new Map((response.events || []).map((event) => [event.id, event]));
        setPendingDates((previous) => {
          const next = { ...previous };
          for (const event of response.events || []) {
            if (event.eventType === "Order" && (!dirtyDateIdsRef.current.has(event.id) || ["EnRoute", "Delivered"].includes(event.status))) {
              next[event.id] = event.plannedDeliveryDate || "";
              if (["EnRoute", "Delivered"].includes(event.status)) dirtyDateIdsRef.current.delete(event.id);
            }
          }
          return next;
        });
        setPlan((previous) => previous ? {
          ...previous,
          events: previous.events.map((event) => {
            const latest = latestById.get(event.id);
            if (!latest) return event;
            return {
              ...event,
              status: latest.status,
              plannedDeliveryDate: latest.plannedDeliveryDate,
              scheduleStatus: latest.scheduleStatus,
              isScheduled: latest.isScheduled,
              canSchedule: latest.canSchedule,
              canSetEnRoute: latest.canSetEnRoute,
              enRouteAtUtc: latest.enRouteAtUtc,
              expectedDeliveryDurationHours: latest.expectedDeliveryDurationHours,
              expectedDeliveryAtUtc: latest.expectedDeliveryAtUtc,
              deliveredAtUtc: latest.deliveredAtUtc,
              isDeliveryEstimated: latest.isDeliveryEstimated,
            };
          }),
        } : previous);
      } catch (requestError) {
        if (current) setError(requestError.message || "Unable to refresh delivery statuses");
      }
    }
    const interval = setInterval(refreshStatuses, 60000);
    window.addEventListener("focus", refreshStatuses);
    return () => {
      current = false;
      clearInterval(interval);
      window.removeEventListener("focus", refreshStatuses);
    };
  }, []);

  const products = plan?.products || [];
  const events = plan?.events || EMPTY_EVENTS;

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
    } catch (requestError) {
      setError(requestError.message || "Unable to update the delivery date");
    } finally {
      setEventSaving(event.id, "date", false);
    }
  }

  async function handleSchedule(event) {
    const deliveryDate = pendingDates[event.id] || event.plannedDeliveryDate;
    if (!event.orderId || !deliveryDate) return;

    setSchedulingId(event.id);
    try {
      await scheduleDelivery({
        orderId: event.orderId,
        deliveryDate,
        notes: null,
      });
      await loadPlan();
    } catch (requestError) {
      setError(requestError.message || "Unable to schedule delivery");
    } finally {
      setSchedulingId(null);
    }
  }

  async function confirmEnRoute(event) {
    if (!/^(?:[01]\d|2[0-3]):[0-5]\d$/.test(departureTime)) {
      setError("Enter a valid departure time on the saved scheduled delivery date.");
      return;
    }
    const hours = Number(durationHours);
    if (!durationHours.trim() || !Number.isFinite(hours) || hours <= 0) {
      setError("Enter a valid positive delivery duration in hours.");
      return;
    }
    setEventSaving(event.id, "dispatch", true);
    setError("");
    try {
      await setOrderEnRoute(event.orderId, departureTime, hours);
      setDispatchEventId(null);
      setDurationHours("");
      await loadPlan();
    } catch (requestError) {
      setError(requestError.message || "Unable to set the order to En Route");
    } finally {
      setEventSaving(event.id, "dispatch", false);
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

  async function handleRemoveFromPlan(orderId) {
    setRemovingFromPlan(orderId);
    try {
      await removeOrderFromPlan(orderId);
      await loadPlan();
    } finally {
      setRemovingFromPlan(null);
    }
  }

  async function handleAddToPlan(orderId) {
    setAddingToPlan(orderId);
    try {
      await addOrderToPlan(orderId);
      await loadPlan();
    } finally {
      setAddingToPlan(null);
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
    dirtyDateIdsRef.current.add(eventId);
    setPendingDates((current) => ({ ...current, [eventId]: value }));
  }

  return (
    <section className="production-delivery-page">
      <header className="page-header">
        <h2>Production / Delivery</h2>
        <p>Persistent Excel-style planning that reuses the live production and delivery workflow.</p>
        <p>Times shown in {plan?.businessTimeZone || "Africa/Johannesburg"}. Automatic Delivered status is estimated, not confirmation of receipt.</p>
      </header>

      {error && <p className="alert error">{error}</p>}

      <div className="panel production-delivery-table-panel">
        <div className="table-wrap production-delivery-table-wrap" ref={plannerContainerRef}>
          <table className="production-delivery-table" ref={plannerTableRef}>
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
                            aria-label={`Scheduled Delivery Date for ${event.orderNumber}`}
                            disabled={isDeliveryLocked(event.status, event.enRouteAtUtc)}
                            min={event.orderDate}
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
                            <button type="button" onClick={() => void saveOrderDate(event)} disabled={isSaving(event.id, "date") || !event.orderId || isDeliveryLocked(event.status, event.enRouteAtUtc)}>
                              {isSaving(event.id, "date") ? "Saving..." : "Save Date"}
                            </button>
                          )}
                          {isOrder && event.canSetEnRoute && !isDeliveryLocked(event.status, event.enRouteAtUtc) && (
                            dispatchEventId === event.id ? (
                              <form onSubmit={(submitEvent) => { submitEvent.preventDefault(); void confirmEnRoute(event); }}>
                                <div>Departure on {formatDate(event.plannedDeliveryDate)} ({plan.businessTimeZone})</div>
                                <label>
                                  Departure time
                                  <input
                                    aria-label={`Departure time for ${event.orderNumber}`}
                                    type="time"
                                    required
                                    value={departureTime}
                                    onChange={(inputEvent) => setDepartureTime(inputEvent.target.value)}
                                  />
                                </label>
                                <label>
                                  Expected duration (hours)
                                  <input
                                    aria-label={`Expected delivery duration for ${event.orderNumber}`}
                                    type="number"
                                    step="any"
                                    required
                                    value={durationHours}
                                    onChange={(inputEvent) => setDurationHours(inputEvent.target.value)}
                                  />
                                </label>
                                <button type="submit" disabled={isSaving(event.id, "dispatch")}>Save Departure</button>
                                <button type="button" className="secondary" disabled={isSaving(event.id, "dispatch")} onClick={() => setDispatchEventId(null)}>Cancel</button>
                              </form>
                            ) : (
                              <button type="button" onClick={() => {
                                setDispatchEventId(event.id);
                                setDepartureTime(businessTimeInput(event.enRouteAtUtc, plan.businessTimeZone));
                                setDurationHours(event.expectedDeliveryDurationHours == null ? "" : String(event.expectedDeliveryDurationHours));
                                setError("");
                              }}>Set En Route</button>
                            )
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
                          {isOrder && event.orderId && (
                            <button type="button" className="danger" onClick={() => void handleRemoveFromPlan(event.orderId)} disabled={removingFromPlan === event.orderId}>
                              {removingFromPlan === event.orderId ? "Removing..." : "Remove from Plan"}
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>

                    <tr className={isOrder ? "stock-after-row order-stock-row" : "stock-after-row"}>
                      <td className="sticky-col sticky-col-1"><strong>STOCK AFTER</strong></td>
                      <td className="sticky-col sticky-col-2">{isOrder && <StatusLabel status={event.status || event.scheduleStatus} />}</td>
                      <td className="sticky-col sticky-col-3">
                        {isOrder && event.enRouteAtUtc && <div>Departure: {formatBusinessDateTime(event.enRouteAtUtc, plan.businessTimeZone)}</div>}
                        {isOrder && event.expectedDeliveryDurationHours != null && <span>{event.expectedDeliveryDurationHours} hours from dispatch</span>}
                      </td>
                      <td className="sticky-col sticky-col-4">
                        {isOrder && event.expectedDeliveryAtUtc && <div>Expected: {formatBusinessDateTime(event.expectedDeliveryAtUtc, plan.businessTimeZone)}</div>}
                        {isOrder && event.isDeliveryEstimated && <div>Delivered (estimated): {formatBusinessDateTime(event.deliveredAtUtc, plan.businessTimeZone)}</div>}
                      </td>
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
                            <button type="button" onClick={() => void handleSchedule(event)} disabled={schedulingId === event.id || !event.canSchedule || !event.orderId || isDeliveryLocked(event.status, event.enRouteAtUtc) || !(pendingDates[event.id] || event.plannedDeliveryDate)}>
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

      {excludedOrders.length > 0 && (
        <div className="panel" style={{ marginTop: "1.5rem" }}>
          <h3 style={{ margin: "0 0 0.75rem" }}>Orders Removed from Plan</h3>
          <p style={{ margin: "0 0 0.75rem", color: "#666", fontSize: "0.9em" }}>
            These orders are active but have been removed from the Production / Delivery plan. Click Add to Plan to restore them.
          </p>
          <table className="orders-table" style={{ width: "100%" }}>
            <thead>
              <tr>
                <th>Order No.</th>
                <th>DC</th>
                <th>Delivery Date</th>
                <th>Status</th>
                <th>Action</th>
              </tr>
            </thead>
            <tbody>
              {excludedOrders.map((order) => (
                <tr key={order.id}>
                  <td>{order.orderNumber}</td>
                  <td>{order.distributionCentre}</td>
                  <td>{formatDate(order.deliveryDate)}</td>
                  <td>{order.status}</td>
                  <td>
                    <button
                      type="button"
                      onClick={() => void handleAddToPlan(order.id)}
                      disabled={addingToPlan === order.id}
                    >
                      {addingToPlan === order.id ? "Adding..." : "Add to Plan"}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export default ProductionDeliveryPage;